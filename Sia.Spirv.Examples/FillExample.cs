using Sia.Spirv;

namespace Sia.Spirv.Examples;

internal static class FillExample
{
    [SpirvKernel(64)]
    private static void Fill(StorageBuffer<float> output, float value)
    {
        output[Gpu.GlobalInvocationId.X] = value;
    }
}
