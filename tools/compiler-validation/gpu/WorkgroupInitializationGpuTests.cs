using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Sia.WebGPU;

namespace SiaGpuDiagnostics;

internal static partial class CompilerGpuTests
{
    [ModuleInitializer]
    internal static void InitializeWorkgroupInitialization() => TestModules.Register("workgroup-initialization", registry => {
        foreach (string fixture in new[] { "WorkgroupInitializationMulti", "WorkgroupInitializationPending5", "WorkgroupInitializationPending9" })
            foreach (string variant in new[] { "source-wgsl", "canonical-wgsl", "canonical-spirv" })
                registry.Add(fixture + "/" + variant, context => RunWorkgroupInitialization(context, fixture, variant), TestMode.Native, requiresGpu: true);
    });

    private static async Task RunWorkgroupInitialization(TestContext context, string fixture, string variant)
    {
        bool multi = fixture == "WorkgroupInitializationMulti";
        int length = multi ? 64 : fixture.EndsWith("5", StringComparison.Ordinal) ? 5 : 9;
        uint[] input = Enumerable.Range(0, length).Select(i => (uint)(i * 7 + 13)).ToArray();
        uint[] expected = new uint[multi ? 192 : length * 2];
        for (int i = 0; i < length; i++) {
            if (multi) { expected[i] = input[(i + 1) % length]; expected[128 + i] = input[(i + 1) % length] + 100; }
            else expected[2 * i + 1] = input[i];
        }
        byte[] text = Asset(fixture + (variant == "source-wgsl" ? ".source.wgsl" : ".wgsl"));
        string source = Encoding.UTF8.GetString(text);
        byte[]? spirv = variant == "canonical-spirv" ? Asset(fixture + ".spv") : null;
        context.RecordShader(spirv is null ? "executed" : "wgsl-sidecar", source);
        context.Capture.Resources.Add(new { Fixture = fixture, Variant = variant, Input = input, Expected = expected,
            Purpose = "Every invocation observes zero before user workgroup writes; neighbor reads observe collective barriers",
            ZeroInitializeWorkgroupMemory = true, Length = length, WorkgroupSize = multi ? 64 : 16,
            WgslSha256 = Convert.ToHexString(SHA256.HashData(text)), SpirvSha256 = spirv is null ? null : Convert.ToHexString(SHA256.HashData(spirv)) });
        using var gpu = new GpuResources(context.Gpu.Device, context.Gpu.Queue);
        var inputs = gpu.Upload<uint>(input, WGPUBufferUsage.Storage);
        var outputs = gpu.Upload<uint>(Enumerable.Repeat(0xa5a5a5a5u, expected.Length).ToArray(), WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
        Dispatch(context, gpu, [inputs, outputs], [GpuBinding.Buffer(0, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
            GpuBinding.Buffer(1, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute)], source, spirv, "main");
        uint[] actual = await Read(context, gpu, outputs, expected.Length);
        context.CompareWords("output", expected, actual);
    }
}
