using Sia.Spirv;

namespace Sia.Spirv.Compiler.Tests.Fixtures;

internal static class IntegerShaders
{
    [SpirvKernel(1)]
    public static void DivideByEight(StorageBuffer<uint> data)
    {
        var index = Gpu.GlobalInvocationId.X;
        data[index] = data[index] / 8u;
    }

    [SpirvKernel(1)]
    public static void RemainderByThree(StorageBuffer<int> data)
    {
        var index = Gpu.GlobalInvocationId.X;
        data[index] = data[index] % 3;
    }

    [SpirvKernel(1)]
    public static void Shift(StorageBuffer<uint> data, int count)
    {
        var index = Gpu.GlobalInvocationId.X;
        data[index] = data[index] << count;
    }
}
