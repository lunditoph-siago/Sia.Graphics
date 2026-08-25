using Sia.Spirv;

namespace Sia.Spirv.Tests.WorkloadSmoke;

internal static class SmokeKernel
{
    [SpirvKernel(64)]
    public static void Fill(StorageBuffer<float> output, float value)
    {
        output[Gpu.GlobalInvocationId.X] = value;
    }
}
