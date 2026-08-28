using Sia.Spirv;

namespace Smoke.Modules;

internal static class PostProcessKernels
{
    [SpirvKernel(128)]
    public static void ToneMap(
        StorageBuffer<float> input,
        StorageBuffer<float> output,
        float exposure,
        uint count)
    {
        var index = Gpu.GlobalInvocationId.X;
        if (index >= count) {
            return;
        }

        var value = input[index] * exposure;
        if (value < 0.0f) {
            value = 0.0f;
        }
        if (value > 1.0f) {
            value = 1.0f;
        }
        output[index] = value * value *
            (ShaderConstants.SmoothFactor - ShaderConstants.SmoothScale * value);
    }

    [SpirvKernel(8, 4)]
    public static void Classify2D(
        StorageBuffer<float> values,
        StorageBuffer<uint> classes,
        float threshold,
        uint width,
        uint height)
    {
        var invocation = Gpu.GlobalInvocationId;
        var x = invocation.X;
        var y = invocation.Y;
        if (x >= width || y >= height) {
            return;
        }

        var index = y * width + x;
        var category = 0u;
        if (values[index] >= threshold) {
            category = 1u;
        }
        classes[index] = category;
    }
}
