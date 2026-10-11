using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Sia.WebGPU;

namespace SiaGpuDiagnostics;

internal static partial class CompilerGpuTests
{
    [ModuleInitializer]
    internal static void InitializeWorkgroupConversion() => TestModules.Register("workgroup-conversion", registry => {
        foreach (string variant in new[] { "source-wgsl", "canonical-wgsl", "canonical-spirv" })
            registry.Add("WorkgroupConversionNested/" + variant,
                context => RunWorkgroupConversion(context, variant), TestMode.Native, requiresGpu: true);
    });

    private static async Task RunWorkgroupConversion(TestContext context, string variant, string fixture = "WorkgroupConversionNested")
    {
        // Element: scalar at 0, vec2 at 8, size 16. Two elements precede vec4 at 32.
        int[] fields = [0, 2, 3, 4, 6, 7, 8, 9, 10, 11];
        uint[] expected = [11, 12, 13, 21, 22, 23, 31, 32, 33, 34];
        byte[] text = Asset(fixture + (variant == "source-wgsl" ? ".source.wgsl" : variant == "reverse-wgsl" ? ".reverse.wgsl" : ".wgsl"));
        string source = Encoding.UTF8.GetString(text);
        byte[]? spirv = variant == "canonical-spirv" ? Asset(fixture + ".spv") : null;
        context.RecordShader(spirv is null ? "executed" : "wgsl-sidecar", source);
        context.Capture.Resources.Add(new { Fixture = fixture, Variant = variant, Expected = expected, BufferFieldIndices = fields,
            CounterExpected = 1u, BufferSize = 48, ElementStride = 16, TailOffset = 32,
            Purpose = "Nested logical/physical workgroup conversion preserves one effectful producer call",
            WgslSha256 = Convert.ToHexString(SHA256.HashData(text)), SpirvSha256 = spirv is null ? null : Convert.ToHexString(SHA256.HashData(spirv)) });
        using var gpu = new GpuResources(context.Gpu.Device, context.Gpu.Queue);
        var output = gpu.Upload<uint>(Enumerable.Repeat(0xa5a5a5a5u, 12).ToArray(), WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
        var counter = gpu.Upload<uint>([0u], WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
        Dispatch(context, gpu, [output, counter], [GpuBinding.Buffer(0, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute),
            GpuBinding.Buffer(1, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute)], source, spirv, "main");
        uint[] actual = await Read(context, gpu, output, 12);
        uint[] count = await Read(context, gpu, counter, 1);
        context.Capture.Resources.Add(new { ActualBuffer = actual, ActualCounter = count });
        context.CompareWords("field-words", expected, fields.Select(i => actual[i]).ToArray());
        context.CompareWords("counter", [1u], count);
    }
}
