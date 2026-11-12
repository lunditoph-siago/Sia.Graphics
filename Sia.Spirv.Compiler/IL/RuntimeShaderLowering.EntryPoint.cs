using Sia.Spirv.Compiler.Legalization;
using Sia.Spirv.Compiler.Metadata;
using Sia.Spirv.Compiler.Model;
using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.IL;

internal sealed partial class RuntimeShaderLowering
{
    private void LowerEntryPoint(SpirvKernel kernel, SpirvKernelAbiLayout layout)
    {
        entry = new ShaderFunction(kernel.Name) {
            Stage = kernel.Stage switch {
                SpirvShaderStage.Compute => ShaderStage.Compute,
                SpirvShaderStage.Vertex => ShaderStage.Vertex,
                _ => ShaderStage.Fragment
            },
            WorkgroupSize = [
                Expression.U32(kernel.WorkgroupSize.X),
                Expression.U32(kernel.WorkgroupSize.Y),
                Expression.U32(kernel.WorkgroupSize.Z)
            ]
        };
        module.Functions.Add(entry);
        if (kernel.ReturnLayout is { } result) {
            entry.ReturnType = IoType(result);
        }

        var arguments = BindEntryParameters(kernel, layout);
        Function(entry, kernel.MetadataToken, arguments, kernel.ControlFlowGraph);
        LowerLegacyOutputs(kernel.ReturnLayout);
    }

    private Value[] BindEntryParameters(SpirvKernel kernel, SpirvKernelAbiLayout layout)
    {
        var (parameters, packedType) = DeclarePushConstants(kernel, layout);
        var usesAtomics = kernel.ControlFlowGraph.Blocks
            .SelectMany(static block => block.Instructions)
            .Any(instruction => instruction.OpCode.Name is "call" or "callvirt"
                && calls.Resolve(instruction.Operand.GetInt32(instruction.Offset)).Intrinsic
                    is IntrinsicKind.AtomicAdd or IntrinsicKind.AtomicExchange);

        var arguments = new Value[kernel.Parameters.Count];
        foreach (var (parameter, slot) in layout.GetParameters(kernel)) {
            var name = $"sia_resource_{parameter.Position}";
            if (parameter.StageIoLayout is { } input) {
                var type = IoType(input);
                entry.Arguments.Add(new(name, type));
                arguments[parameter.Position] = new(new Expression.Reference(name, type));
                continue;
            }

            switch (parameter.Kind) {
                case SpirvKernelParameterKind.SampledTexture2D:
                case SpirvKernelParameterKind.SampledTexture2DArray:
                case SpirvKernelParameterKind.Sampler:
                    var handle = HandleType(parameter.Kind);
                    module.Globals.Add(new(name, handle, AddressSpace.Handle, StorageAccess.Read,
                        new(SpirvKernelAbiLayout.DescriptorSet, (uint)slot.Binding!.Value)));
                    arguments[parameter.Position] = new(new Expression.Reference(name, handle));
                    break;
                case SpirvKernelParameterKind.PushConstant:
                    var element = ParameterElementType(parameter);
                    arguments[parameter.Position] = new(LoadPushConstant(parameters!, packedType, slot.Scalar!.Value, element));
                    break;
                default:
                    arguments[parameter.Position] = BindBuffer(kernel, parameter, name, usesAtomics, slot.Binding);
                    break;
            }
        }
        return arguments;
    }

    private (Expression.Reference? Reference, ShaderType.Array? PackedType) DeclarePushConstants(
        SpirvKernel kernel,
        SpirvKernelAbiLayout layout)
    {
        if (layout.ScalarCount == 0) {
            return (null, null);
        }

        var packed = layout.PackedParameters;
        var packedType = packed is { } block
            ? new ShaderType.Array(new ShaderType.Vector(SpirvScalarSlot.VectorWidth, ShaderType.U32), (uint)block.VectorCount)
            : null;
        var members = packedType is null
            ? layout.GetParameters(kernel)
                .Where(static entry => entry.Slot.Scalar is not null)
                .Select(static entry => {
                    var slot = entry.Slot.Scalar!.Value;
                    return new StructMember($"p{slot.Index}", Type(entry.Parameter.ScalarType), (uint)slot.ByteOffset);
                }).ToArray()
            : [new StructMember("data", packedType)];
        var type = new ShaderType.Structure("SiaParameters", members);
        module.Structures.Add(type);

        var space = packedType is null ? AddressSpace.Immediate : AddressSpace.Uniform;
        const string name = "sia_parameters";
        module.Globals.Add(new(name, type, space, StorageAccess.Read,
            packed is { } binding ? new(SpirvKernelAbiLayout.DescriptorSet, (uint)binding.Binding) : null));
        return (new(name, new ShaderType.Pointer(type, space, StorageAccess.Read)), packedType);
    }

    private static Expression LoadPushConstant(
        Expression parameters,
        ShaderType.Array? packedType,
        SpirvScalarSlot slot,
        ShaderType element)
    {
        if (packedType is null) {
            return new Expression.Load(new Expression.Member(parameters, $"p{slot.Index}", Pointer(parameters, element)));
        }

        var array = new Expression.Member(parameters, "data", Pointer(parameters, packedType));
        var vector = new Expression.Access(array, Expression.U32((uint)slot.VectorIndex), Pointer(array, packedType.Element));
        var value = new Expression.Load(new Expression.Access(
            vector, Expression.U32((uint)slot.Lane), Pointer(vector, ShaderType.U32)));
        return element == ShaderType.Bool
            ? new Expression.Binary("!=", value, Expression.U32(0), ShaderType.Bool)
            : Convert(value, element, bitcast: true);
    }

    private Value BindBuffer(
        SpirvKernel kernel,
        SpirvKernelParameter parameter,
        string name,
        bool usesAtomics,
        int? binding)
    {
        var element = ParameterElementType(parameter);
        var space = parameter.Kind switch {
            SpirvKernelParameterKind.WorkgroupMemory => AddressSpace.Workgroup,
            SpirvKernelParameterKind.UniformBuffer => AddressSpace.Uniform,
            SpirvKernelParameterKind.StorageBuffer or SpirvKernelParameterKind.ReadOnlyStorageBuffer => AddressSpace.Storage,
            _ => throw Error(0, $"Runtime resource kind {parameter.Kind} is not supported.")
        };
        var readOnly = parameter.Kind is SpirvKernelParameterKind.ReadOnlyStorageBuffer or SpirvKernelParameterKind.UniformBuffer;
        var access = readOnly ? StorageAccess.Read : StorageAccess.ReadWrite;
        if (usesAtomics && !readOnly && element is ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint } integer) {
            element = new ShaderType.Atomic(integer);
        }

        var count = space == AddressSpace.Workgroup
            ? checked(kernel.WorkgroupSize.X * kernel.WorkgroupSize.Y * kernel.WorkgroupSize.Z)
            : (uint?)parameter.BufferLength;
        var array = new ShaderType.Array(element, count,
            parameter.PhysicalLayout is { } layout ? (uint)layout.ArrayStride : null);
        if (space == AddressSpace.Workgroup) {
            module.Globals.Add(new(name, array, space, access));
            return new(new Expression.Reference(name, new ShaderType.Pointer(array, space, access)), Place: true);
        }

        var wrapper = new ShaderType.Structure($"SiaResource{parameter.Position}", [new("data", array)]);
        module.Structures.Add(wrapper);
        module.Globals.Add(new(name, wrapper, space, access, new(SpirvKernelAbiLayout.DescriptorSet, (uint)binding!.Value)));
        var reference = new Expression.Reference(name, new ShaderType.Pointer(wrapper, space, access));
        return new(new Expression.Member(reference, "data", Pointer(reference, array)), Place: true);
    }

    private void LowerLegacyOutputs(SpirvStageIoLayout? returnLayout)
    {
        if (outputs.Count == 0) {
            return;
        }
        if (returnLayout is not null) {
            throw Error(0, "Explicit stage results cannot be combined with legacy GPU output intrinsics.");
        }

        var members = outputs.Select(static pair => new StructMember(
            $"output{pair.Key.Location}",
            ValueType(pair.Value),
            Binding: pair.Key.Location == uint.MaxValue
                ? new(Builtin: "position")
                : new(pair.Key.Location, Interpolation: pair.Key.Flat ? "flat" : null))).ToArray();
        var type = new ShaderType.Structure("SiaOutput", members);
        module.Structures.Add(type);
        entry.ReturnType = type;
        entryResult = new Expression.Construct(type, outputs.Values.Select(static value =>
            (Expression)new Expression.Load(value)).ToArray());
    }

    private static ShaderType HandleType(SpirvKernelParameterKind kind)
        => kind switch {
            SpirvKernelParameterKind.Sampler => new ShaderType.Sampler(),
            SpirvKernelParameterKind.SampledTexture2DArray => new ShaderType.Image(ImageDimension.D2, ShaderType.F32, Arrayed: true),
            _ => new ShaderType.Image(ImageDimension.D2, ShaderType.F32)
        };

    private ShaderType ParameterElementType(SpirvKernelParameter parameter)
        => parameter.PhysicalLayout is { } layout ? PhysicalType(layout) : Type(parameter.ScalarType);

    private ShaderType.Structure PhysicalType(PhysicalStructLayout layout)
    {
        if (structures.TryGetValue(layout.LogicalType.Name, out var existing)) {
            return existing;
        }
        var fields = layout.LogicalType.Fields.Select((field, index) => {
            var physical = layout.GetLogicalMember(index);
            return new StructMember(field.Name, Type(field.Type), (uint)physical.Offset);
        }).ToArray();
        var type = new ShaderType.Structure(Name("structure"), fields);
        structures.Add(layout.LogicalType.Name, type);
        module.Structures.Add(type);
        return type;
    }

    private ShaderType.Structure IoType(SpirvStageIoLayout layout)
    {
        if (structures.TryGetValue(layout.Name, out var existing)) {
            return existing;
        }
        var fields = layout.Fields.Select(StageIoMember).ToArray();
        var type = new ShaderType.Structure(Name("io"), fields);
        structures.Add(layout.Name, type);
        module.Structures.Add(type);
        return type;
    }

    private static StructMember StageIoMember(SpirvStageIoField field)
        => new(field.Name, Type(field.Type), Binding: new(
            field.Location,
            field.Kind switch {
                SpirvStageIoKind.Position or SpirvStageIoKind.FragmentPosition => "position",
                SpirvStageIoKind.VertexIndex => "vertex_index",
                SpirvStageIoKind.InstanceIndex => "instance_index",
                SpirvStageIoKind.FrontFacing => "front_facing",
                SpirvStageIoKind.FragmentDepth => "frag_depth",
                _ => null
            },
            field.Interpolation switch {
                InterpolationMode.Flat => "flat",
                InterpolationMode.Linear => "linear",
                InterpolationMode.Perspective => "perspective",
                _ => null
            },
            field.Interpolation == InterpolationMode.Flat ? null : field.Sampling switch {
                InterpolationSampling.Centroid => "centroid",
                InterpolationSampling.Center => "center",
                _ => null
            }));
}
