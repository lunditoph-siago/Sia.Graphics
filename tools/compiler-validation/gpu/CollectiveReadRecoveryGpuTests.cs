using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Sia.WebGPU;

namespace SiaGpuDiagnostics;

internal static partial class CompilerGpuTests
{
    [ModuleInitializer]
    internal static void InitializeCollectiveReadRecovery() => TestModules.Register("collective-recovery",registry => {
        foreach (string fixture in new[] {"CollectiveUniformLoop","CollectiveDivergentRead"})
            foreach (string variant in new[] {"source-wgsl","canonical-wgsl","canonical-spirv","reverse-wgsl"})
                registry.Add(fixture+"/"+variant,context => RunCollectiveReadRecovery(context,fixture,variant),TestMode.Native,requiresGpu:true);
    });

    private static async Task RunCollectiveReadRecovery(TestContext context,string fixture,string variant)
    {
        uint[] expected = fixture == "CollectiveUniformLoop" ? [10,11,12,13] : [12,13,10,11];
        string suffix = variant == "source-wgsl" ? ".source.wgsl" : variant == "reverse-wgsl" ? ".reverse.wgsl" : ".wgsl";
        byte[] text = Asset(fixture+suffix); string source = Encoding.UTF8.GetString(text);
        byte[]? spirv = variant == "canonical-spirv" ? Asset(fixture+".spv") : null;
        context.RecordShader(spirv is null ? "executed" : "wgsl-sidecar",source);
        context.Capture.Resources.Add(new { Fixture=fixture,Variant=variant,Expected=expected,CounterExpectedWords=Array.Empty<uint>(),BufferSize=16,
            Purpose="Four invocations preserve proved collective results or divergent pointer fallback",
            WgslSha256=Convert.ToHexString(SHA256.HashData(text)),SpirvSha256=spirv is null ? null : Convert.ToHexString(SHA256.HashData(spirv)) });
        using var gpu = new GpuResources(context.Gpu.Device,context.Gpu.Queue);
        var output = gpu.Upload<uint>([0xa5a5a5a5u,0xa5a5a5a5u,0xa5a5a5a5u,0xa5a5a5a5u],WGPUBufferUsage.Storage|WGPUBufferUsage.CopySrc);
        Dispatch(context,gpu,[output],[GpuBinding.Buffer(0,WGPUBufferBindingType.Storage,WGPUShaderStage.Compute)],source,spirv,"main");
        uint[] actual = await Read(context,gpu,output,4);
        context.Capture.Resources.Add(new { ActualBuffer=actual });
        context.CompareWords("values",expected,actual);
    }
}
