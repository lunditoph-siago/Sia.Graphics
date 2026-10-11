using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Metadata;
using Sia.Spirv.Compiler.Legalization;
using Sia.Spirv.Compiler.Model;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Valid;
using Sia.Spirv.Compiler.Translation.Proc;
using Module = Sia.Spirv.Compiler.Translation.IR.Module;

namespace Sia.Spirv.Compiler.IL;

/// <summary>CIL is input data. This lowering never loads or executes a managed assembly.</summary>
internal sealed partial class RuntimeShaderLowering(PEReader pe, MetadataReader metadata, CilCallResolver calls)
{
    private readonly Module module = new();
    private readonly Dictionary<int, ShaderFunction> helpers = [];
    private readonly HashSet<int> activeHelpers = [];
    private readonly Dictionary<IntrinsicKind, Expression> builtins = [];
    private readonly Dictionary<string, ShaderType.Structure> structures = [];
    private readonly Dictionary<(uint Location, bool Flat), Expression> outputs = [];
    private ShaderFunction entry = null!;
    private int next;
    private sealed record Value(Expression Expression, bool Place = false, bool ArgumentSlot = false);
    private static InvalidDataException Error(int offset, string message) =>
        new($"Runtime shader lowering at IL_{offset:x4}: {message}");
    private string Name(string prefix) => "sia_" + prefix + "_" + next++;
    private static ShaderType ValueType(Expression expression) => expression.Type is ShaderType.Pointer p ? p.Base : expression.Type;
    private static Expression.Reference Place(string name, ShaderType type) => new(name, new ShaderType.Pointer(type, AddressSpace.Function));
    private static Expression Zero(ShaderType type) => new Expression.Construct(type, []);

    public static Module Run(ReadOnlyMemory<byte> assemblyImage, ReadOnlyMemory<byte> intrinsicImage,
        SpirvKernel kernel, SpirvKernelAbi abi)
    {
        var frontend = ReadCanonical(assemblyImage, intrinsicImage, kernel, abi);
        var canonical = CanonicalShaderPipeline.Prepare(frontend.Declarations, frontendGraphs: frontend.Functions,
            verifyFrontend: (graph, input) => ControlFlowVerifier.Validate(graph, input));
        canonical = CanonicalHelperInliner.RunReferences(canonical);
        canonical = CanonicalReferenceLowering.Run(canonical);
        return ShaderTargetLowering.ForStructured(CanonicalControlFlowRegions.Run(canonical));
    }

    internal static CanonicalModule ReadCanonical(ReadOnlyMemory<byte> assemblyImage, ReadOnlyMemory<byte> intrinsicImage,
        SpirvKernel kernel, SpirvKernelAbi abi)
    {
        using var stream = new MemoryStream(assemblyImage.ToArray(), writable: false);
        using var pe = new PEReader(stream, PEStreamOptions.PrefetchEntireImage);
        using var intrinsics = IntrinsicCatalog.Open(intrinsicImage);
        var metadata = pe.GetMetadataReader();
        var lowering = new RuntimeShaderLowering(pe, metadata, new CilCallResolver(metadata, intrinsics));
        lowering.Entry(kernel, abi);
        var graphs = lowering.ReadGraphs();
        var canonical = new CanonicalModule(lowering.module, graphs, new Dictionary<string, string>(),
            Translation.Proc.ControlFlowAnalysis.EntryFunctions(lowering.module, graphs));
        Translation.Proc.ShaderEffectAnalysis.RefreshCalls(canonical);
        ModuleValidator.Validate(canonical, native: true);
        return canonical;
    }

    private void Entry(SpirvKernel kernel, SpirvKernelAbi abi)
    {
        entry = new ShaderFunction(kernel.Name) { Stage = kernel.Stage switch {
            SpirvShaderStage.Compute => ShaderStage.Compute, SpirvShaderStage.Vertex => ShaderStage.Vertex,
            _ => ShaderStage.Fragment },
            WorkgroupSize = [Expression.U32(kernel.WorkgroupSize.X), Expression.U32(kernel.WorkgroupSize.Y), Expression.U32(kernel.WorkgroupSize.Z)] };
        module.Functions.Add(entry);
        if (kernel.ReturnLayout is { } result) entry.ReturnType = IoType(result);
        var values = new Value[kernel.Parameters.Count];
        uint binding = 0;
        var scalar = kernel.Parameters.Where(p => p.Kind == SpirvKernelParameterKind.PushConstant).ToArray();
        Expression? parameters = null;
        if (scalar.Length != 0) {
            ShaderType type;
            if (abi == SpirvKernelAbi.WebGpu) {
                var vectors = new ShaderType.Array(new ShaderType.Vector(4, ShaderType.U32), (uint)((scalar.Length+3)/4));
                type = new ShaderType.Structure("SiaParameters", [new("data", vectors)]);
            }
            else type = new ShaderType.Structure("SiaParameters", scalar.Select((p, i) => new StructMember("p"+i, Type(p.ScalarType), (uint)i*4)).ToArray());
            module.Structures.Add((ShaderType.Structure)type);
            uint resources = (uint)kernel.Parameters.Count(p => p.IsResource);
            var space = abi == SpirvKernelAbi.WebGpu ? AddressSpace.Uniform : AddressSpace.Immediate;
            module.Globals.Add(new("sia_parameters", type, space, StorageAccess.Read,
                abi == SpirvKernelAbi.WebGpu ? new(0, resources) : null));
            parameters = new Expression.Reference("sia_parameters", new ShaderType.Pointer(type, space, StorageAccess.Read));
        }
        var graph = kernel.ControlFlowGraph;
        bool atomics = graph.Blocks.SelectMany(b => b.Instructions).Any(i => i.OpCode.Name is "call" or "callvirt"
            && calls.Resolve(i.Operand.GetInt32(i.Offset)).Intrinsic is IntrinsicKind.AtomicAdd or IntrinsicKind.AtomicExchange);
        int scalarIndex = 0;
        foreach (var parameter in kernel.Parameters) {
            string name = "sia_resource_"+parameter.Position;
            if (parameter.StageIoLayout is { } input) {
                var inputType = IoType(input);
                entry.Arguments.Add(new(name, inputType));
                values[parameter.Position] = new(new Expression.Reference(name, inputType));
                continue;
            }
            if (parameter.Kind is SpirvKernelParameterKind.SampledTexture2D or SpirvKernelParameterKind.SampledTexture2DArray or SpirvKernelParameterKind.Sampler) {
                ShaderType handle = parameter.Kind == SpirvKernelParameterKind.Sampler ? new ShaderType.Sampler()
                    : new ShaderType.Image(ImageDimension.D2, ShaderType.F32, Arrayed: parameter.Kind == SpirvKernelParameterKind.SampledTexture2DArray);
                module.Globals.Add(new(name, handle, AddressSpace.Handle, StorageAccess.Read, new(0, binding++)));
                values[parameter.Position] = new(new Expression.Reference(name, handle));
                continue;
            }
            ShaderType element = parameter.PhysicalLayout is { } layout ? PhysicalType(layout) : Type(parameter.ScalarType);
            if (parameter.Kind == SpirvKernelParameterKind.PushConstant) {
                Expression value;
                if (abi == SpirvKernelAbi.WebGpu) {
                    var array = new Expression.Member(parameters!, "data", Pointer(parameters!, new ShaderType.Array(new ShaderType.Vector(4, ShaderType.U32), (uint)((scalar.Length+3)/4))));
                    var vector = new Expression.Access(array, Expression.U32((uint)scalarIndex/4), Pointer(array, new ShaderType.Vector(4, ShaderType.U32)));
                    value = new Expression.Load(new Expression.Access(vector, Expression.U32((uint)scalarIndex%4), Pointer(vector, ShaderType.U32)));
                    value = element == ShaderType.Bool ? new Expression.Binary("!=", value, Expression.U32(0), ShaderType.Bool) : Convert(value, element, bitcast: true);
                }
                else value = new Expression.Load(new Expression.Member(parameters!, "p"+scalarIndex, Pointer(parameters!, element)));
                scalarIndex++;
                values[parameter.Position] = new(value);
                continue;
            }
            if (parameter.Kind is not (SpirvKernelParameterKind.StorageBuffer or SpirvKernelParameterKind.ReadOnlyStorageBuffer
                or SpirvKernelParameterKind.UniformBuffer or SpirvKernelParameterKind.WorkgroupMemory))
                throw Error(0, $"Runtime resource kind {parameter.Kind} is not supported.");
            bool workgroup = parameter.Kind == SpirvKernelParameterKind.WorkgroupMemory;
            bool readOnly = parameter.Kind is SpirvKernelParameterKind.ReadOnlyStorageBuffer or SpirvKernelParameterKind.UniformBuffer;
            if (atomics && !readOnly && element is ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint } integer)
                element = new ShaderType.Atomic(integer);
            uint? count = workgroup ? checked(kernel.WorkgroupSize.X * kernel.WorkgroupSize.Y * kernel.WorkgroupSize.Z)
                : parameter.BufferLength is int length ? (uint)length : null;
            var arrayType = new ShaderType.Array(element, count, parameter.PhysicalLayout is { } physical ? (uint)physical.ArrayStride : null);
            var space = workgroup ? AddressSpace.Workgroup : parameter.Kind == SpirvKernelParameterKind.UniformBuffer ? AddressSpace.Uniform : AddressSpace.Storage;
            var access = readOnly ? StorageAccess.Read : StorageAccess.ReadWrite;
            ShaderType globalType = arrayType;
            if (!workgroup) {
                globalType = new ShaderType.Structure("SiaResource"+parameter.Position, [new("data", arrayType)]);
                module.Structures.Add((ShaderType.Structure)globalType);
            }
            module.Globals.Add(new(name, globalType, space, access, workgroup ? null : new(0, binding++)));
            Expression reference = new Expression.Reference(name, new ShaderType.Pointer(globalType, space, access));
            if (!workgroup) reference = new Expression.Member(reference, "data", Pointer(reference, arrayType));
            values[parameter.Position] = new(reference, true);
        }
        Function(entry, kernel.MetadataToken, values, graph);
        if (outputs.Count != 0) {
            if (kernel.ReturnLayout is not null) throw Error(0, "Explicit stage results cannot be combined with legacy GPU output intrinsics.");
            var members = outputs.Select(pair => new StructMember("output"+pair.Key.Location, ValueType(pair.Value),
                Binding: pair.Key.Location == uint.MaxValue ? new(Builtin: "position") : new(pair.Key.Location, Interpolation: pair.Key.Flat ? "flat" : null))).ToArray();
            var resultType = new ShaderType.Structure("SiaOutput", members);
            module.Structures.Add(resultType); entry.ReturnType = resultType;
            var returned = new Expression.Construct(resultType, outputs.Values.Select(p => (Expression)new Expression.Load(p)).ToArray());
            entryResult = returned;
        }
    }

    private ShaderType.Structure PhysicalType(PhysicalStructLayout layout) {
        if (structures.TryGetValue(layout.LogicalType.Name, out var existing)) return existing;
        var fields = layout.LogicalType.Fields.Select((field, index) => {
            var physical = layout.GetLogicalMember(index);
            return new StructMember(field.Name, Type(field.Type), (uint)physical.Offset);
        }).ToArray();
        var type = new ShaderType.Structure(Name("structure"), fields);
        structures.Add(layout.LogicalType.Name, type); module.Structures.Add(type); return type;
    }
    private ShaderType.Structure IoType(SpirvStageIoLayout layout) {
        if (structures.TryGetValue(layout.Name, out var existing)) return existing;
        var fields = layout.Fields.Select(field => new StructMember(field.Name, Type(field.Type), Binding: new(
            field.Location, field.Kind switch {
                SpirvStageIoKind.Position or SpirvStageIoKind.FragmentPosition => "position",
                SpirvStageIoKind.VertexIndex => "vertex_index", SpirvStageIoKind.InstanceIndex => "instance_index",
                SpirvStageIoKind.FrontFacing => "front_facing", SpirvStageIoKind.FragmentDepth => "frag_depth", _ => null },
            field.Interpolation switch { InterpolationMode.Flat => "flat", InterpolationMode.Linear => "linear", InterpolationMode.Perspective => "perspective", _ => null },
            field.Interpolation == InterpolationMode.Flat ? null : field.Sampling switch { InterpolationSampling.Centroid => "centroid", InterpolationSampling.Center => "center", _ => null }))).ToArray();
        var type = new ShaderType.Structure(Name("io"), fields);
        structures.Add(layout.Name, type); module.Structures.Add(type); return type;
    }
    private static ShaderType Type(SpirvScalarType type) => type switch {
        SpirvScalarType.Boolean => ShaderType.Bool, SpirvScalarType.Int32 => ShaderType.I32,
        SpirvScalarType.UInt32 => ShaderType.U32, SpirvScalarType.Float32 => ShaderType.F32,
        SpirvScalarType.Int32x2 => new ShaderType.Vector(2, ShaderType.I32), SpirvScalarType.Int32x3 => new ShaderType.Vector(3, ShaderType.I32),
        SpirvScalarType.Int32x4 => new ShaderType.Vector(4, ShaderType.I32), SpirvScalarType.UInt32x2 => new ShaderType.Vector(2, ShaderType.U32),
        SpirvScalarType.UInt32x3 => new ShaderType.Vector(3, ShaderType.U32), SpirvScalarType.UInt32x4 => new ShaderType.Vector(4, ShaderType.U32),
        SpirvScalarType.Float32x2 => new ShaderType.Vector(2, ShaderType.F32), SpirvScalarType.Float32x3 => new ShaderType.Vector(3, ShaderType.F32),
        SpirvScalarType.Float32x4 => new ShaderType.Vector(4, ShaderType.F32), _ => throw Error(0, $"Unsupported runtime value type {type}.")
    };
    private ShaderType Type(KernelType type) {
        ShaderType result = type.Name switch {
            "System.Void" => new ShaderType.Void(), "System.Boolean" => ShaderType.Bool,
            "System.Int32" => ShaderType.I32, "System.UInt32" => ShaderType.U32, "System.Single" => ShaderType.F32,
            "Sia.Spirv.Texture2D" => new ShaderType.Image(ImageDimension.D2, ShaderType.F32),
            "Sia.Spirv.Texture2DArray" => new ShaderType.Image(ImageDimension.D2, ShaderType.F32, Arrayed: true),
            "Sia.Spirv.Sampler" => new ShaderType.Sampler(),
            "Sia.Spirv.UInt3" => new ShaderType.Vector(3, ShaderType.U32),
            _ when type.Name.StartsWith("Sia.Math.", StringComparison.Ordinal) => MathType(type.Name[9..]),
            _ when structures.TryGetValue(type.Name, out var structure) => structure,
            _ => throw Error(0, $"Unsupported runtime local/parameter type {type.Name}.")
        };
        return type.IsByReference ? new ShaderType.Pointer(result, AddressSpace.Function) : result;
    }
    private static ShaderType MathType(string name) {
        ShaderType.Scalar component; string dimensions;
        if (name.StartsWith("float", StringComparison.Ordinal)) { component = ShaderType.F32; dimensions = name[5..]; }
        else if (name.StartsWith("uint", StringComparison.Ordinal)) { component = ShaderType.U32; dimensions = name[4..]; }
        else if (name.StartsWith("int", StringComparison.Ordinal)) { component = ShaderType.I32; dimensions = name[3..]; }
        else if (name.StartsWith("bool", StringComparison.Ordinal)) { component = ShaderType.Bool; dimensions = name[4..]; }
        else throw Error(0, $"Unsupported math type {name}.");
        if (dimensions.Length == 1 && dimensions[0] is >= '2' and <= '4') return new ShaderType.Vector(dimensions[0]-'0', component);
        if (dimensions is [>= '2' and <= '4' and var rows, 'x', >= '2' and <= '4' and var columns] && component == ShaderType.F32)
            return new ShaderType.Matrix(columns-'0', rows-'0', component);
        throw Error(0, $"Unsupported math shape {name}.");
    }
    private static Expression Convert(Expression value, ShaderType type, bool bitcast = false) {
        if (value.Type == type) return value;
        if (type == ShaderType.Bool) return Truth(value);
        if (value.Type == ShaderType.Bool && type is ShaderType.Scalar scalar) {
            Expression one = scalar.Kind == ScalarKind.Uint ? Expression.U32(1) : scalar.Kind == ScalarKind.Sint ? Expression.I32(1) : new Expression.Literal(1f, scalar);
            return new Expression.Select(value, one, Zero(type));
        }
        return new Expression.Convert(type, value, bitcast);
    }
    private Value Snapshot(Expression expression, Block body) {
        string name = Name("value");
        body.Statements.Add(new Statement.Declare(name, expression.Type, expression, false));
        return new(new Expression.Reference(name, expression.Type));
    }
    private Expression Read(Value value, Block body) {
        if (!value.Place) return value.Expression;
        var type = ValueType(value.Expression);
        if (type is ShaderType.Atomic atomic) return Snapshot(new Expression.Call("atomicLoad", [Address(value.Expression)], atomic.Component, CallBinding.Builtin), body).Expression;
        return Snapshot(new Expression.Load(value.Expression), body).Expression;
    }
    private static Expression Address(Expression place) => new Expression.Unary("&", place, Pointer(place, ValueType(place)));

}
