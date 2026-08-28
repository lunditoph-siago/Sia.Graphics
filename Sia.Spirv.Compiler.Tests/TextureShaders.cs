using Sia.Spirv;

namespace Sia.Spirv.Compiler.Tests;

internal static class TextureShaders
{
    [SpirvFragmentShader]
    public static void SampleAndLoad(
        Texture2D texture,
        Texture2DArray textureArray,
        Sampler sampler,
        float u,
        float v)
    {
        var sampled = texture.SampleLevel(sampler, u, v, 1.0f, 0u);
        var loaded = texture.Load(0, 0, 1, 1u);
        var arraySampled = textureArray.SampleLevel(sampler, u, v, 0.0f, 1.0f, 2u);
        var arrayLoaded = textureArray.Load(0, 0, 0, 1, 3u);
        Gpu.SetOutput(0, sampled, loaded, arraySampled, arrayLoaded);
    }
}
