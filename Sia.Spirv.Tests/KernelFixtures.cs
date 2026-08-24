using Sia.Spirv;

namespace Sia.Spirv.Tests;

internal static class KernelFixtures
{
    [SpirvKernel(64)]
    public static void Saxpy(
        StorageBuffer<float> x,
        StorageBuffer<float> y,
        StorageBuffer<float> output,
        float a)
    {
        var index = Gpu.GlobalInvocationId.X;
        output[index] = (a * x[index]) + y[index];
    }

    [SpirvKernel(8, 4, 2)]
    public static void ControlFlow(StorageBuffer<uint> output, uint count)
    {
        for (uint index = 0; index < count; index++) {
            if ((index & 1) == 0) {
                output[index] = index;
            }
        }
    }

    [SpirvKernel(0)]
    public static void InvalidWorkgroup()
    {
    }

    [SpirvKernel(1)]
    public static void AllocatesManagedObject()
    {
        GC.KeepAlive(new object());
    }
}

internal sealed class InvalidKernelFixtures
{
    [SpirvKernel(1)]
    public void InstanceKernel()
    {
    }
}
