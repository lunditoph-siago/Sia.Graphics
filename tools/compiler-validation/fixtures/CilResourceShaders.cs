using Sia.Spirv;

namespace Sia.Spirv.Compiler.Validation;

public static class CilResourceShaders
{
    private static Texture2D Image(Texture2D first, Texture2D second, uint choose, scoped ref uint calls)
    {
        calls++;
        if (choose == 0) return first;
        return second;
    }
    private static Texture2DArray ArrayImage(Texture2DArray first, Texture2DArray second, uint choose)
    {
        if (choose == 0) return first;
        return second;
    }
    private static Sampler Sampling(Sampler first, Sampler second, uint choose, scoped ref uint calls)
    {
        calls++;
        if (choose != 0) first = second;
        return first;
    }
    private static int Coordinate(ref uint calls) { calls++; return 0; }
    private static float SamplingCoordinate(ref uint calls) { calls++; return 1.25f; }

    [SpirvKernel(1, 1, 1)]
    public static void ImageAliases(Texture2D first, Texture2D second, StorageBuffer<float> outputs, uint choose)
    {
        uint calls = 0;
        var current = Image(first, second, choose, ref calls);
        var other = choose == 0 ? second : first;
        outputs[0] = current.Load(Coordinate(ref calls), 0, 0u);
        for (uint i = 0; i < 3; i++) {
            var previous = current;
            current = other;
            other = previous;
        }
        outputs[1] = current.Load(Coordinate(ref calls), 0, 0u);
        outputs[2] = calls;
    }

    [SpirvKernel(1, 1, 1)]
    public static void ArrayAliases(Texture2DArray first, Texture2DArray second, StorageBuffer<float> outputs, uint choose)
    {
        var current = ArrayImage(first, second, choose);
        var other = choose == 0 ? second : first;
        for (uint i = 0; i < 3; i++) {
            var previous = current;
            current = other;
            other = previous;
        }
        outputs[0] = current.Load(0, 0, 0, 0u);
    }

    [SpirvKernel(1, 1, 1)]
    public static void SamplerAliases(Texture2D texture, Sampler first, Sampler second, StorageBuffer<float> outputs, uint choose)
    {
        uint calls = 0;
        var current = Sampling(first, second, choose, ref calls);
        var other = choose == 0 ? second : first;
        outputs[0] = texture.SampleLevel(current, SamplingCoordinate(ref calls), 0.5f, 0f, 0u);
        for (uint i = 0; i < 3; i++) {
            var previous = current;
            current = other;
            other = previous;
        }
        outputs[1] = texture.SampleLevel(current, SamplingCoordinate(ref calls), 0.5f, 0f, 0u);
        outputs[2] = calls;
    }
}
