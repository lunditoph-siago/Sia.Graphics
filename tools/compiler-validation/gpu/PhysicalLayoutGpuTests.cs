using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Sia.WebGPU;

namespace SiaGpuDiagnostics;

internal static partial class CompilerGpuTests
{
    [ModuleInitializer]
    internal static void InitializePhysicalLayout() => TestModules.Register("physical-layout", registry => {
        foreach (var sample in new[] { (Fixture: "PhysicalLayoutShared", Index: 1u), (Fixture: "PhysicalLayoutAlias", Index: 0u), (Fixture: "PhysicalLayoutAlias", Index: 1u) })
            foreach (string variant in new[] { "source-wgsl", "canonical-wgsl", "canonical-spirv" })
                registry.Add(sample.Fixture + "/index-" + sample.Index + "/" + variant,
                    context => RunPhysicalLayout(context, sample.Fixture, sample.Index, variant), TestMode.Native, requiresGpu: true);
    });

    private static async Task RunPhysicalLayout(TestContext context, string fixture, uint index, string variant)
    {
        bool shared = fixture == "PhysicalLayoutShared";
        // Data ABI: index at 0, two matrix columns at 8/16, tail at 24, size 32.
        uint[] input = [index, 0xeeeeeeee, 0x3fa00000, 0x40200000, 0x40700000, 0x40800000, 0x40f00000, 0xdddddddd];
        int[] fields = shared ? [0, 2, 3, 4, 5, 6] : [0, 1];
        uint[] expected = shared ? [index, 0x3fa00000, 0x40200000, 0x40700000, 0x40800000, 0x40f00000]
            : [index == 0 ? 0x40200000u : 0x40800000u, 0x40f00000];
        byte[] text = Asset(fixture + (variant == "source-wgsl" ? ".source.wgsl" : ".wgsl"));
        string source = Encoding.UTF8.GetString(text);
        byte[]? spirv = variant == "canonical-spirv" ? Asset(fixture + ".spv") : null;
        context.RecordShader(spirv is null ? "executed" : "wgsl-sidecar", source);
        context.Capture.Resources.Add(new { Fixture = fixture, Variant = variant, Index = index, Input = input, Expected = expected,
            BufferFieldIndices = fields, UniformSize = 32, MatrixColumnOffsets = new[] { 8, 16 }, TailOffset = 24,
            Purpose = shared ? "One logical structure passes uniform -> workgroup -> storage with distinct physical identities"
                : "Uniform pointer aliases retain the index captured before cursor changes",
            WgslSha256 = Convert.ToHexString(SHA256.HashData(text)), SpirvSha256 = spirv is null ? null : Convert.ToHexString(SHA256.HashData(spirv)) });
        using var gpu = new GpuResources(context.Gpu.Device, context.Gpu.Queue);
        var inputs = gpu.Upload<uint>(input, WGPUBufferUsage.Uniform);
        var outputs = gpu.Upload<uint>(Enumerable.Repeat(0xa5a5a5a5u, shared ? 8 : 2).ToArray(), WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
        Dispatch(context, gpu, [inputs, outputs], [GpuBinding.Buffer(0, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute),
            GpuBinding.Buffer(1, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute)], source, spirv, "main");
        uint[] actual = await Read(context, gpu, outputs, shared ? 8 : 2);
        context.Capture.Resources.Add(new { ActualBuffer = actual });
        uint[] selected = fields.Select(i => actual[i]).ToArray();
        context.CompareWords("field-words", expected, selected);
    }
}
