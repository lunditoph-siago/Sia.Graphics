using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Sia.WebGPU;

namespace SiaGpuDiagnostics;

internal static partial class CompilerGpuTests
{
    [ModuleInitializer]
    internal static void InitializeTargetControlFlow() => TestModules.Register("target-control-flow", registry => {
        for (int index = 0; index < 8; index++) {
            int fixture = index;
            foreach (string variant in new[] { "source-wgsl", "canonical-wgsl", "canonical-spirv", "reverse-wgsl" })
                registry.Add("TargetControl" + fixture + "/" + variant,
                    context => RunTargetControlFlow(context, fixture, variant), TestMode.Native, requiresGpu: true);
        }
    });

    private static async Task RunTargetControlFlow(TestContext context, int fixture, string variant)
    {
        const uint untouched = 0xa5a5a5a5;
        uint[] expected = fixture switch {
            0 => [7, untouched, untouched, untouched],
            1 => [untouched, 1, untouched, untouched],
            2 => [3, untouched, untouched, untouched],
            3 => [9, untouched, untouched, untouched],
            4 => [9, 11, untouched, untouched],
            5 => [24, untouched, untouched, untouched],
            6 => [2, 2, 2, untouched],
            7 => [7, untouched, untouched, untouched],
            _ => throw new ArgumentOutOfRangeException(nameof(fixture))
        };
        string name = "TargetControl" + fixture;
        string suffix = variant == "source-wgsl" ? ".source.wgsl" : variant == "reverse-wgsl" ? ".reverse.wgsl" : ".wgsl";
        byte[] text = Asset(name + suffix); string source = Encoding.UTF8.GetString(text);
        byte[]? spirv = variant == "canonical-spirv" ? Asset(name + ".spv") : null;
        context.RecordShader(spirv is null ? "executed" : "wgsl-sidecar", source);
        context.Capture.Resources.Add(new { Fixture = name, Variant = variant, Expected = expected,
            Purpose = "CFG branches, terminal arms, loop phis, continuing, switch aliases and short-circuit effects",
            WgslSha256 = Convert.ToHexString(SHA256.HashData(text)), SpirvSha256 = spirv is null ? null : Convert.ToHexString(SHA256.HashData(spirv)) });
        using var gpu = new GpuResources(context.Gpu.Device, context.Gpu.Queue);
        var output = gpu.Upload<uint>([untouched, untouched, untouched, untouched], WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
        Dispatch(context, gpu, [output], [GpuBinding.Buffer(0, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute)], source, spirv, "main");
        uint[] actual = await Read(context, gpu, output, 4);
        context.Capture.Resources.Add(new { ActualBuffer = actual });
        context.CompareWords("values", expected, actual);
    }
}
