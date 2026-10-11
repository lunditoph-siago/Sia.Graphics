using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Sia.WebGPU;

namespace SiaGpuDiagnostics;

internal static partial class CompilerGpuTests
{
    [ModuleInitializer]
    internal static void InitializeEntryWrapper() => TestModules.Register("entry-wrapper", registry => {
        foreach (string sample in new[] { "EntryInputStruct", "EntryMultipleFirst", "EntryMultipleSecond" })
            foreach (string variant in new[] { "source-wgsl", "canonical-wgsl", "canonical-spirv", "reverse-wgsl" })
                registry.Add(sample + "/" + variant, context => RunEntryWrapper(context, sample, variant), TestMode.Native, requiresGpu: true);
    });

    private static async Task RunEntryWrapper(TestContext context, string sample, string variant)
    {
        const uint untouched = 0xa5a5a5a5;
        uint[] expected = sample switch {
            "EntryInputStruct" => [17, 18, 19, 20],
            "EntryMultipleFirst" => [7, untouched, untouched, untouched],
            "EntryMultipleSecond" => [11, untouched, untouched, untouched],
            _ => throw new ArgumentOutOfRangeException(nameof(sample))
        };
        string fixture = sample == "EntryInputStruct" ? sample : "EntryMultiple";
        string entry = sample == "EntryInputStruct" ? "main" : sample == "EntryMultipleFirst" ? "first" : "second";
        string suffix = variant == "source-wgsl" ? ".source.wgsl" : variant == "reverse-wgsl" ? ".reverse.wgsl" : ".wgsl";
        byte[] text = Asset(fixture + suffix); string source = Encoding.UTF8.GetString(text);
        byte[]? spirv = variant == "canonical-spirv" ? Asset(fixture + ".spv") : null;
        context.RecordShader(spirv is null ? "executed" : "wgsl-sidecar", source);
        context.Capture.Resources.Add(new { Fixture = fixture, Sample = sample, Entry = entry, Variant = variant, Expected = expected,
            Purpose = "Entry IO assembly, implicit initialization and independent interface identities across entries",
            WgslSha256 = Convert.ToHexString(SHA256.HashData(text)), SpirvSha256 = spirv is null ? null : Convert.ToHexString(SHA256.HashData(spirv)) });
        using var gpu = new GpuResources(context.Gpu.Device, context.Gpu.Queue);
        var output = gpu.Upload<uint>([untouched, untouched, untouched, untouched], WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
        Dispatch(context, gpu, [output], [GpuBinding.Buffer(0, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute)], source, spirv, entry);
        uint[] actual = await Read(context, gpu, output, 4);
        context.Capture.Resources.Add(new { ActualBuffer = actual });
        context.CompareWords("values", expected, actual);
    }
}
