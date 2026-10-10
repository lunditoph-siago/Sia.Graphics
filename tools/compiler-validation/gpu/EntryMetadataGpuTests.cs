using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Sia.WebGPU;

namespace SiaGpuDiagnostics;

internal static partial class CompilerGpuTests
{
    [ModuleInitializer]
    internal static void InitializeEntryMetadata() => TestModules.Register("entry-metadata", registry => {
        foreach (string sample in new[] { "Shared", "DifferentFirst", "DifferentSecond" })
            foreach (string variant in new[] { "source-wgsl", "canonical-wgsl", "canonical-spirv", "reverse-wgsl" })
                registry.Add(sample + "/" + variant, context => RunEntryMetadata(context, sample, variant), TestMode.Native, requiresGpu: true);
    });
    private static async Task RunEntryMetadata(TestContext context, string sample, string variant)
    {
        const uint poison = 0xa5a5a5a5;
        uint[] expected = sample switch {
            "Shared" => [3, 4, 5, 6, poison], "DifferentFirst" => [3, 4, 5, poison, poison], "DifferentSecond" => [5, 6, 7, 8, 9],
            _ => throw new ArgumentOutOfRangeException(nameof(sample))
        };
        string fixture = sample == "Shared" ? "EntryPolicyShared" : "EntryPolicyDifferent";
        string entry = sample == "Shared" ? "main" : sample == "DifferentFirst" ? "first" : "second";
        string suffix = variant == "source-wgsl" ? ".source.wgsl" : variant == "reverse-wgsl" ? ".reverse.wgsl" : ".wgsl";
        byte[] text = Asset(fixture + suffix); string source = Encoding.UTF8.GetString(text);
        byte[]? spirv = variant == "canonical-spirv" ? Asset(fixture + ".spv") : null;
        context.RecordShader(spirv is null ? "executed" : "wgsl-sidecar", source);
        context.Capture.Resources.Add(new { Fixture = fixture, Sample = sample, Entry = entry, Variant = variant, Expected = expected,
            Purpose = "Specialized workgroup dimensions and body values, distinct entry dimensions, untouched trailing words",
            Target = sample == "Shared" ? "vulkan1.2/SPIR-V1.5/shared-WorkgroupSize" : "vulkan1.3/SPIR-V1.6/LocalSizeId",
            WgslSha256 = Convert.ToHexString(SHA256.HashData(text)), SpirvSha256 = spirv is null ? null : Convert.ToHexString(SHA256.HashData(spirv)) });
        using var gpu = new GpuResources(context.Gpu.Device, context.Gpu.Queue);
        var output = gpu.Upload<uint>([poison, poison, poison, poison, poison], WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
        Dispatch(context, gpu, [output], [GpuBinding.Buffer(0, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute)], source, spirv, entry);
        uint[] actual = await Read(context, gpu, output, 5);
        context.Capture.Resources.Add(new { ActualBuffer = actual }); context.CompareWords("values", expected, actual);
    }
}
