using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Sia.WebGPU;

namespace SiaGpuDiagnostics;

internal static partial class CompilerGpuTests
{
    [ModuleInitializer]
    internal static void InitializeSynchronization() => TestModules.Register("synchronization",registry => {
        foreach (string variant in new[] {"source-wgsl","canonical-wgsl","canonical-spirv","reverse-wgsl"})
            registry.Add("SynchronizationOrdered/"+variant,context => RunSynchronization(context,"SynchronizationOrdered",variant),TestMode.Native,requiresGpu:true);
        foreach (string variant in new[] {"canonical-wgsl","canonical-spirv","reverse-wgsl"})
            registry.Add("SynchronizationRawOrdered/"+variant,context => RunSynchronization(context,"SynchronizationRawOrdered",variant),TestMode.Native,requiresGpu:true);
        foreach (string variant in new[] {"canonical-spirv","reverse-wgsl"})
            registry.Add("SynchronizationAtomic/"+variant,context => RunSynchronization(context,"SynchronizationAtomic",variant),TestMode.Native,requiresGpu:true);
    });

    private static async Task RunSynchronization(TestContext context,string fixture,string variant)
    {
        bool atomic = fixture == "SynchronizationAtomic";
        uint[] expected = atomic ? [9,9,14] : [25,15,22,11];
        uint[] counters = atomic ? [] : [31456];
        string suffix = variant == "source-wgsl" ? ".source.wgsl" : variant == "reverse-wgsl" ? ".reverse.wgsl" : ".wgsl";
        byte[] text = Asset(fixture+suffix); string source = Encoding.UTF8.GetString(text);
        byte[]? spirv = variant == "canonical-spirv" ? Asset(fixture+".spv") : null;
        context.RecordShader(spirv is null ? "executed" : "wgsl-sidecar",source);
        context.Capture.Resources.Add(new { Fixture=fixture,Variant=variant,Expected=expected,CounterExpectedWords=counters,
            BufferSize=expected.Length*4, Purpose=atomic ? "Two collective atomic reads retain 9/9/14 around atomicAdd" : "Ordered calls, captured address and continuing read retain marker sequence 3,1,4,5,6",
            WgslSha256=Convert.ToHexString(SHA256.HashData(text)),SpirvSha256=spirv is null ? null : Convert.ToHexString(SHA256.HashData(spirv)) });
        using var gpu = new GpuResources(context.Gpu.Device,context.Gpu.Queue);
        var output = gpu.Upload<uint>(Enumerable.Repeat(0xa5a5a5a5u,expected.Length).ToArray(),WGPUBufferUsage.Storage|WGPUBufferUsage.CopySrc);
        if (atomic) {
            Dispatch(context,gpu,[output],[GpuBinding.Buffer(0,WGPUBufferBindingType.Storage,WGPUShaderStage.Compute)],source,spirv,"main");
            uint[] actual = await Read(context,gpu,output,expected.Length);
            context.Capture.Resources.Add(new { ActualBuffer=actual });
            context.CompareWords("values",expected,actual);
        }
        else {
            var counter = gpu.Upload<uint>([0u],WGPUBufferUsage.Storage|WGPUBufferUsage.CopySrc);
            Dispatch(context,gpu,[output,counter],[GpuBinding.Buffer(0,WGPUBufferBindingType.Storage,WGPUShaderStage.Compute),GpuBinding.Buffer(1,WGPUBufferBindingType.Storage,WGPUShaderStage.Compute)],source,spirv,"main");
            uint[] actual = await Read(context,gpu,output,expected.Length); uint[] count = await Read(context,gpu,counter,1);
            context.Capture.Resources.Add(new { ActualBuffer=actual,ActualCounter=count });
            context.CompareWords("values",expected,actual); context.CompareWords("marker-sequence",counters,count);
        }
    }
}
