using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Sia.WebGPU;

namespace SiaGpuDiagnostics;

internal static partial class CompilerGpuTests
{
    [ModuleInitializer]
    internal static void InitializeWorkgroupAccess() => TestModules.Register("workgroup-access", registry => {
        foreach (string variant in new[] { "source-wgsl", "canonical-wgsl", "canonical-spirv" }) {
            registry.Add("WorkgroupAccessUniform/" + variant,
                context => RunWorkgroupConversion(context, variant, "WorkgroupAccessUniform"), TestMode.Native, requiresGpu: true);
            foreach (int index in new[] { 0, 1 })
                registry.Add("WorkgroupAccessAlias/index-" + index + "/" + variant,
                    context => RunWorkgroupAccessAlias(context, index, variant), TestMode.Native, requiresGpu: true);
        }
        registry.Add("WorkgroupAccessUniform/reverse-wgsl",
            context => RunWorkgroupConversion(context,"reverse-wgsl","WorkgroupAccessUniform"),TestMode.Native,requiresGpu:true);
        registry.Add("WorkgroupAccessUniformVulkan/canonical-spirv",
            context => RunWorkgroupConversion(context, "canonical-spirv", "WorkgroupAccessUniformVulkan"), TestMode.Native, requiresGpu: true);
        foreach (int index in new[] { 0, 1 })
            registry.Add("WorkgroupAccessAliasVulkan/index-" + index + "/canonical-spirv",
                context => RunWorkgroupAccessAlias(context, index, "canonical-spirv", "WorkgroupAccessAliasVulkan"), TestMode.Native, requiresGpu: true);
    });

    private static async Task RunWorkgroupAccessAlias(TestContext context, int index, string variant, string fixture = "WorkgroupAccessAlias")
    {
        uint[] expected = index == 0 ? [111, 22, 33] : [11, 122, 33];
        byte[] text = Asset(fixture + (variant == "source-wgsl" ? ".source.wgsl" : ".wgsl"));
        string source = Encoding.UTF8.GetString(text);
        byte[]? spirv = variant == "canonical-spirv" ? Asset(fixture + ".spv") : null;
        context.RecordShader(spirv is null ? "executed" : "wgsl-sidecar", source);
        context.Capture.Resources.Add(new { Fixture = fixture, Variant = variant, Index = index, Expected = expected,
            CounterExpected = 1u, BufferSize = 12, TailOffset = 8,
            Purpose = "Captured workgroup alias evaluates its index once and retains the outer global after lexical shadowing",
            WgslSha256 = Convert.ToHexString(SHA256.HashData(text)), SpirvSha256 = spirv is null ? null : Convert.ToHexString(SHA256.HashData(spirv)) });
        using var gpu = new GpuResources(context.Gpu.Device, context.Gpu.Queue);
        var output = gpu.Upload<uint>([0xa5a5a5a5u, 0xa5a5a5a5u, 0xa5a5a5a5u], WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
        var counter = gpu.Upload<uint>([0u, (uint)index], WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
        Dispatch(context, gpu, [output, counter], [GpuBinding.Buffer(0, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute),
            GpuBinding.Buffer(1, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute)], source, spirv, "main");
        uint[] actual = await Read(context, gpu, output, 3);
        uint[] count = await Read(context, gpu, counter, 2);
        context.Capture.Resources.Add(new { ActualBuffer = actual, ActualCounter = count });
        context.CompareWords("field-words", expected, actual);
        context.CompareWords("counter", [1u, (uint)index], count);
    }
}
