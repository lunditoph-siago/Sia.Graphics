using System.Collections.Immutable;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler.Legalization;

internal sealed class SpirvKernelAbiLayout
{
    public const int DescriptorSet = 0;

    private readonly ImmutableArray<SpirvParameterSlot> _slots;

    public SpirvKernelAbi Abi { get; }
    public int ScalarCount { get; }
    public SpirvPackedParameters? PackedParameters { get; }

    private SpirvKernelAbiLayout(
        SpirvKernelAbi abi,
        ImmutableArray<SpirvParameterSlot> slots,
        int scalarCount,
        SpirvPackedParameters? packedParameters)
    {
        Abi = abi;
        _slots = slots;
        ScalarCount = scalarCount;
        PackedParameters = packedParameters;
    }

    public static SpirvKernelAbiLayout Create(IReadOnlyList<SpirvKernelParameter> parameters, SpirvKernelAbi abi)
    {
        var slots = ImmutableArray.CreateBuilder<SpirvParameterSlot>(parameters.Count);
        var binding = 0;
        var scalar = 0;
        foreach (var parameter in parameters) {
            slots.Add(parameter.Kind == SpirvKernelParameterKind.PushConstant
                ? new(Scalar: new SpirvScalarSlot(scalar++))
                : parameter.IsResource ? new(Binding: binding++) : default);
        }

        SpirvPackedParameters? packed = abi == SpirvKernelAbi.WebGpu && scalar != 0
            ? new(binding, (int)(((long)scalar + SpirvScalarSlot.VectorWidth - 1) / SpirvScalarSlot.VectorWidth))
            : null;
        return new(abi, slots.MoveToImmutable(), scalar, packed);
    }

    public IEnumerable<(SpirvKernelParameter Parameter, SpirvParameterSlot Slot)> GetParameters(SpirvKernel kernel)
        => kernel.Parameters.Zip(_slots);
}

internal readonly record struct SpirvParameterSlot(int? Binding = null, SpirvScalarSlot? Scalar = null);

internal readonly record struct SpirvScalarSlot(int Index)
{
    public const int Size = 4;
    public const int VectorWidth = 4;
    public const int VectorSize = VectorWidth * Size;

    public int ByteOffset => Index * Size;
    public int VectorIndex => Index / VectorWidth;
    public int Lane => Index % VectorWidth;
}

internal readonly record struct SpirvPackedParameters(int Binding, int VectorCount)
{
    public const string Name = "sia.parameters";

    public ulong ByteSize => (ulong)VectorCount * SpirvScalarSlot.VectorSize;
}
