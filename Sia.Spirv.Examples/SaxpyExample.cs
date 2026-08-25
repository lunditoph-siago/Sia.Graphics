using Sia.Spirv;

namespace Sia.Spirv.Examples;

internal static class SaxpyExample
{
    [SpirvKernel(64)]
    private static void Saxpy(
        StorageBuffer<float> x,
        StorageBuffer<float> y,
        StorageBuffer<float> output,
        float a)
    {
        var index = Gpu.GlobalInvocationId.X;
        output[index] = (a * x[index]) + y[index];
    }
}
