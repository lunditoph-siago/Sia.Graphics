using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Metadata;
using Sia.Spirv.Compiler.Model;
using Sia.Spirv.Compiler.Translation.Front;
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
    private readonly Dictionary<int, List<ShaderFunction>> helpers = [];
    private readonly HashSet<int> activeHelpers = [];
    private readonly Dictionary<IntrinsicKind, Expression> builtins = [];
    private readonly Dictionary<string, ShaderType.Structure> structures = [];
    private readonly Dictionary<(uint Location, bool Flat), Expression> outputs = [];
    private readonly Dictionary<(uint Location, bool Flat), Expression> inputs = [];
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
        var source = new NativeModuleSource(frontend.Declarations, frontend.Functions,
            static (graph, input) => ControlFlowVerifier.Validate(graph, input));
        var canonical = CanonicalShaderPipeline.Prepare(source);
        canonical = CanonicalHelperInliner.RunTargetHelpers(canonical);
        canonical = CanonicalReferenceLowering.Run(canonical);
        return StructuredModuleLowering.Run(CanonicalControlFlowRegions.Run(canonical));
    }

    internal static CanonicalModule ReadCanonical(ReadOnlyMemory<byte> assemblyImage, ReadOnlyMemory<byte> intrinsicImage,
        SpirvKernel kernel, SpirvKernelAbi abi)
    {
        using var stream = new MemoryStream(assemblyImage.ToArray(), writable: false);
        using var pe = new PEReader(stream, PEStreamOptions.PrefetchEntireImage);
        using var intrinsics = IntrinsicCatalog.Open(intrinsicImage);
        var metadata = pe.GetMetadataReader();
        var lowering = new RuntimeShaderLowering(pe, metadata, new CilCallResolver(metadata, intrinsics));
        lowering.LowerEntryPoint(kernel, abi);
        var graphs = lowering.ReadGraphs();
        var canonical = new CanonicalModule(lowering.module, graphs, new Dictionary<string, string>(),
            ControlFlowAnalysis.EntryFunctions(lowering.module, graphs));
        ShaderEffectAnalysis.RefreshCalls(canonical);
        ModuleValidator.Validate(canonical, native: true);
        return canonical;
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
