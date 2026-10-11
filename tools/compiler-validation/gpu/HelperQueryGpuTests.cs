using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Sia.WebGPU;

namespace SiaGpuDiagnostics;

internal static partial class CompilerGpuTests
{
    [ModuleInitializer]
    internal static void InitializeHelperQuery() => TestModules.Register("helper-query", registry => {
        FragmentCase[] samples = [
            new("NativeHelperQuery", "demoted", 1, [43,23], 37), new("NativeHelperQuery", "alive", 0, [0,99], 100),
            new("NativePointerHelperQuery", "demoted", 0, [1,10]), new("NativePointerHelperQuery", "alive", 5, [14,26])
        ];
        foreach (var sample in samples)
            foreach (string variant in new[] { "source-spirv", "canonical-spirv" })
                registry.Add(sample.Fixture + "/" + sample.Sample + "/" + variant,
                    context => RunHelperQuery(context, sample, variant), TestMode.Native, requiresGpu: true,
                    configureDevice: configuration => configuration.CompatibilityLimits.MaxStorageBuffersInFragmentStage =
                        Math.Max(configuration.CompatibilityLimits.MaxStorageBuffersInFragmentStage, 2));
    });

    private static async Task RunHelperQuery(TestContext context, FragmentCase sample, string variant)
    {
        byte[] spirv = Asset(sample.Fixture + (variant == "source-spirv" ? ".input.spv" : ".spv"));
        context.RecordShader("vertex", FragmentVertex);
        context.Capture.Resources.Add(new {
            sample.Fixture, sample.Sample, Variant = variant, Input = new[] { sample.Input, 0u }, sample.Expected,
            ExpectedColor = sample.Color, SpirvSha256 = Convert.ToHexString(SHA256.HashData(spirv)),
            WgslRepresentation = "unsupported dynamic helper invocation query", Width = 1, Height = 1,
            ColorFormat = sample.Color.HasValue ? "R32Uint" : null, ColorClear = sample.Color.HasValue ? 37u : (uint?)null,
            DepthFormat = "Depth32Float", DepthCompare = "Always", DepthWrite = false, SampleCount = 1
        });
        using var gpu = new GpuResources(context.Gpu.Device, context.Gpu.Queue);
        GpuResource input = gpu.Upload<uint>([sample.Input, 0u], WGPUBufferUsage.Storage);
        GpuResource output = gpu.Upload<uint>(new uint[2], WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
        context.Progress("Creating native helper-query pipeline " + variant);
        var color = RenderFragmentKill(context, gpu, input, output, "", spirv, sample.Color.HasValue);
        uint[] actual = await Read(context, gpu, output, 2); context.CompareWords("output", sample.Expected, actual);
        if (sample.Color is uint expected) context.CompareWords("color", [expected], [await ReadFragmentColor(context, gpu, color)]);
    }
}
