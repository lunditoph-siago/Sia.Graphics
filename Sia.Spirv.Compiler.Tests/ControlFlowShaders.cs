using Sia.Math;
using Sia.Spirv;

namespace Sia.Spirv.Compiler.Tests;

internal static class ControlFlowShaders
{
    [SpirvKernel(64)]
    public static void IntegerControlFlow(
        StorageBuffer<int> signedOutput,
        StorageBuffer<uint> unsignedOutput,
        int value,
        uint unsignedValue,
        int count,
        uint iterations,
        int selector)
    {
        var sum = 0u;
        for (var index = 0u; index < iterations; index++) {
            sum += index;
        }

        var selected = selector;
        switch (selector) {
            case 0:
                selected = value;
                break;
            case 1:
                selected = ~value;
                break;
            case 2:
                selected = int.MinValue;
                break;
        }

        signedOutput[0] = selected ^ (value >> count) ^ int.MinValue;
        unsignedOutput[0] = (unsignedValue << count) ^
            (unsignedValue >> count) ^ sum;
        unsignedOutput[1] = unsignedValue << 40;
    }

    [SpirvKernel(64)]
    public static void SpeculativeSelection(
        StorageBuffer<float> output,
        float condition,
        uint material)
    {
        var throughput = new float3(0.8f, 0.7f, 0.6f);
        var albedo = new float3(0.5f, 0.4f, 0.3f);
        var normal = math.normalize(new float3(0.2f, 1f, 0.1f));
        var direction = math.normalize(new float3(0.3f, -1f, 0.2f));
        var origin = new float3(0f, 1f, 0f);
        var radiance = new float3(0f, 0f, 0f);
        var shouldBreak = false;

        if (condition < -1f) {
            shouldBreak = true;
        }
        else if (condition < 0f) {
            radiance += throughput * albedo;
            shouldBreak = true;
        }
        else if (material == 1u) {
            throughput *= albedo;
            origin += normal * 0.002f;
            direction = math.reflect(direction, normal);
        }
        else {
            var cosine = math.max(0f, math.dot(normal, -direction));
            if (cosine > 0f) {
                radiance += throughput * albedo * cosine;
            }
            throughput *= albedo;
            if (condition > 2f) {
                shouldBreak = true;
            }
            if (!shouldBreak) {
                origin += normal * 0.002f;
                direction = math.normalize(direction + normal);
            }
        }

        if (shouldBreak) {
            radiance += throughput;
        }
        output[0] = radiance.x + origin.x + direction.x + throughput.x;
    }
}
