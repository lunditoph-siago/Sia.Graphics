using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Sia.WebGPU;

namespace SiaGpuDiagnostics;

internal static partial class CompilerGpuTests
{
    [ModuleInitializer]
    internal static void InitializeUniformLegalization() => TestModules.Register("uniform-legalization", registry => {
        foreach (string fixture in new[] { "UniformNested", "UniformContinuing" })
            foreach (int sample in new[] { 0, 1 })
                foreach (string variant in new[] { "source-wgsl", "canonical-wgsl", "canonical-spirv" })
                    registry.Add(fixture + "/sample-" + sample + "/" + variant,
                        context => RunUniformLegalization(context, fixture, sample, variant), TestMode.Native, requiresGpu: true);
        // The SPIR-V target's explicit switch fallback is zero. WGSL leaves
        // out-of-bounds reads implementation-dependent, so this has no WGSL oracle.
        registry.Add("UniformNested/sample-2/canonical-spirv",
            context => RunUniformLegalization(context, "UniformNested", 2, "canonical-spirv"), TestMode.Native, requiresGpu: true);
    });

    private static async Task RunUniformLegalization(TestContext context, string fixture, int sample, string variant)
    {
        bool nested = fixture == "UniformNested";
        uint[] input;
        uint[] expected;
        if (nested) {
            // Outer: indices at 0, values at 16; Inner stride 32, matrix
            // columns at 0/8/16 and tail at 24. The uniform is 80 bytes.
            input = [sample == 1 ? 1u : 0u, sample == 2 ? 7u : sample == 1 ? 2u : 1u, sample == 0 ? 0u : 1u, 0u,
                Bits(1), Bits(2), Bits(3), Bits(4), Bits(5), Bits(6), Bits(7), 0xeeeeeeee,
                Bits(11), Bits(12), Bits(13), Bits(14), Bits(15), Bits(16), Bits(17), 0xdddddddd];
            expected = [Bits(sample == 2 ? 0 : sample == 1 ? 16 : 3), Bits(sample == 1 ? 17 : 7), Bits(1)];
        } else {
            input = [(uint)sample, 0xeeeeeeee, Bits(1.25f), Bits(2.5f), Bits(3.75f), Bits(4), Bits(7.5f), 0xdddddddd];
            expected = [Bits(sample == 0 ? 2.5f : 4), Bits(sample == 0 ? 2.5f : 4), Bits(2.5f)];
        }
        byte[] text = Asset(fixture + (variant == "source-wgsl" ? ".source.wgsl" : ".wgsl"));
        string source = Encoding.UTF8.GetString(text);
        byte[]? spirv = variant == "canonical-spirv" ? Asset(fixture + ".spv") : null;
        context.RecordShader(spirv is null ? "executed" : "wgsl-sidecar", source);
        context.Capture.Resources.Add(new { Fixture = fixture, Variant = variant, Sample = sample, Input = input, Expected = expected,
            UniformSize = input.Length * 4, InnerStride = nested ? 32 : 0, TailOffset = 24,
            Purpose = nested ? "Nested uniform selection evaluates the column helper once; sample 2 checks explicit SPIR-V zero fallback"
                : "Continuing inherits the loop-body alias until its own declaration shadows it",
            WgslSha256 = Convert.ToHexString(SHA256.HashData(text)), SpirvSha256 = spirv is null ? null : Convert.ToHexString(SHA256.HashData(spirv)) });
        using var gpu = new GpuResources(context.Gpu.Device, context.Gpu.Queue);
        var inputs = gpu.Upload<uint>(input, WGPUBufferUsage.Uniform);
        var outputs = gpu.Upload<uint>([0xa5a5a5a5u, 0xa5a5a5a5u, 0xa5a5a5a5u], WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
        Dispatch(context, gpu, [inputs, outputs], [GpuBinding.Buffer(0, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute),
            GpuBinding.Buffer(1, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute)], source, spirv, "main");
        uint[] actual = await Read(context, gpu, outputs, 3);
        context.Capture.Resources.Add(new { ActualBuffer = actual });
        context.CompareWords("field-words", expected, actual);
    }
}
