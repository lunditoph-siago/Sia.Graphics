using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Sia.Spirv.Compiler;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.WebGPU;

namespace SiaGpuDiagnostics;

internal static partial class CompilerGpuTests
{
    [ModuleInitializer]
    internal static void InitializeCilResources() => TestModules.Register("cil-resource", registry => {
        foreach (string fixture in new[] { "ImageAliases", "ArrayAliases", "SamplerAliases" })
            foreach (uint choose in new uint[] { 0, 1 })
                foreach (string variant in new[] { "wgsl", "spirv" })
                    registry.Add($"{fixture}/{choose}/{variant}", context => RunCilResource(context, fixture, choose, variant), TestMode.Native, requiresGpu: true);
    });

    private static async Task RunCilResource(TestContext context, string name, uint choose, string variant)
    {
        var assembly = Asset("resources.dll"); var core = Asset("intrinsics.dll");
        var kernel = new SpirvFrontend().Analyze(assembly, core).Kernels.Single(k => k.Name == name);
        var request = new SpirvModuleCompilationRequest(assembly, kernel.MetadataToken, core);
        var module = new SpirvCompiler().CompileModule(request);
        string source = WgslWriter.Write(module, request.Target);
        byte[] native = SpirvWriter.Write(module, request.Target);
        byte[]? spirv = variant == "spirv" ? native : null;
        bool sampling = name == "SamplerAliases", arrayed = name == "ArrayAliases";
        float first = sampling ? (choose == 0 ? 0.75f : 0.25f) : (choose == 0 ? 0.25f : 0.75f);
        float second = 1f - first;
        uint[] expected = arrayed ? [Bits(second)] : [Bits(first), Bits(second), Bits(3f)];
        context.RecordShader(spirv is null ? "executed" : "wgsl-sidecar", source);
        context.Capture.Resources.Add(new { kernel.QualifiedName, kernel.MetadataToken, Fixture = name, Choose = choose, Variant = variant,
            ShaderAssemblySha256 = Convert.ToHexString(SHA256.HashData(assembly)), IntrinsicAssemblySha256 = Convert.ToHexString(SHA256.HashData(core)),
            NativeSha256 = Convert.ToHexString(SHA256.HashData(native)), TargetIdentity = request.Target.Identity, Expected = expected,
            TextureValues = new[] { 0.25f, 0.75f }, Purpose = "Real CIL resource returns, parallel loop swaps and exactly-once reference effects" });
        using var gpu = new GpuResources(context.Gpu.Device, context.Gpu.Queue);
        var output = gpu.Upload<uint>(new uint[expected.Length], WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
        var parameters = gpu.Upload<uint>(new uint[] { choose, 0, 0, 0 }, WGPUBufferUsage.Uniform);
        DispatchCilResource(context, gpu, output, parameters, source, spirv, name, sampling, arrayed);
        uint[] actual = await Read(context, gpu, output, expected.Length);
        context.CompareWords("values", expected, actual);
    }

    private static unsafe GpuResource UploadCilTexture(TestContext context, GpuResources gpu, float[] pixels, bool arrayed)
    {
        var descriptor = WGPUTextureDescriptor.Default; descriptor.Dimension = WGPUTextureDimension._2D;
        descriptor.Size = new() { Width = (uint)pixels.Length, Height = 1, DepthOrArrayLayers = 1 };
        descriptor.Format = WGPUTextureFormat.R32Float; descriptor.MipLevelCount = 1; descriptor.SampleCount = 1;
        descriptor.Usage = WGPUTextureUsage.CopyDst | WGPUTextureUsage.TextureBinding;
        var texture = gpu.Own(Wgpu.CreateTexture(gpu.Device, descriptor));
        var destination = WGPUTexelCopyTextureInfo.Default; destination.Texture = (WGPUTexture*)texture.Handle;
        var layout = WGPUTexelCopyBufferLayout.Default; layout.BytesPerRow = (uint)pixels.Length * 4; layout.RowsPerImage = 1;
        var extent = descriptor.Size;
        fixed (float* data = pixels)
            WgpuUnsafe.wgpuQueueWriteTexture((WGPUQueue*)context.Gpu.Queue.DangerousGetHandle(), &destination, data, (nuint)pixels.Length * 4, &layout, &extent);
        var view = WGPUTextureViewDescriptor.Default; view.Dimension = arrayed ? WGPUTextureViewDimension._2DArray : WGPUTextureViewDimension._2D;
        context.Capture.Commands.Add($"queue.writeTexture(R32Float,{pixels.Length}x1); createView({view.Dimension})");
        return gpu.Own(Wgpu.CreateTextureView(texture.GetWgpu<WGPUTexture>(), view));
    }

    private static unsafe void DispatchCilResource(TestContext context, GpuResources gpu, GpuResource output, GpuResource parameters,
        string source, byte[]? spirv, string name, bool sampling, bool arrayed)
    {
        var first = UploadCilTexture(context, gpu, sampling ? [0.25f, 0.75f] : [0.25f], arrayed);
        var second = sampling ? gpu.Own(Wgpu.CreateSampler(gpu.Device, WGPUSamplerDescriptor.Default)) : UploadCilTexture(context, gpu, [0.75f], arrayed);
        var layouts = new List<WGPUBindGroupLayoutEntry>(); var entries = new List<WGPUBindGroupEntry>();
        void Texture(uint binding, GpuResource resource) {
            var entry = WGPUBindGroupLayoutEntry.Default; entry.Binding = binding; entry.Visibility = WGPUShaderStage.Compute;
            entry.Texture = WGPUTextureBindingLayout.Default; entry.Texture.SampleType = WGPUTextureSampleType.UnfilterableFloat;
            entry.Texture.ViewDimension = arrayed ? WGPUTextureViewDimension._2DArray : WGPUTextureViewDimension._2D;
            layouts.Add(entry);
            var value = WGPUBindGroupEntry.Default; value.Binding = binding; value.TextureView = (WGPUTextureView*)resource.Handle; entries.Add(value);
        }
        void Sampler(uint binding, GpuResource resource) {
            var entry = WGPUBindGroupLayoutEntry.Default; entry.Binding = binding; entry.Visibility = WGPUShaderStage.Compute;
            entry.Sampler = WGPUSamplerBindingLayout.Default; entry.Sampler.Type = WGPUSamplerBindingType.NonFiltering; layouts.Add(entry);
            var value = WGPUBindGroupEntry.Default; value.Binding = binding; value.Sampler = (WGPUSampler*)resource.Handle; entries.Add(value);
        }
        Texture(0, first);
        if (sampling) {
            Sampler(1, second); var repeat = WGPUSamplerDescriptor.Default; repeat.AddressModeU = WGPUAddressMode.Repeat;
            Sampler(2, gpu.Own(Wgpu.CreateSampler(gpu.Device, repeat)));
        } else Texture(1, second);
        uint outputBinding = sampling ? 3u : 2u;
        layouts.Add(GpuBinding.Buffer(outputBinding, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute)); entries.Add(GpuBinding.Buffer(outputBinding, output));
        layouts.Add(GpuBinding.Buffer(outputBinding + 1, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute)); entries.Add(GpuBinding.Buffer(outputBinding + 1, parameters));
        var layout = GpuBinding.Layout(gpu, layouts.ToArray()); var group = GpuBinding.Group(gpu, layout, entries.ToArray());
        var shader = gpu.Own(spirv is null ? Wgpu.CreateWgslShaderModule(gpu.Device, source, name) : Wgpu.CreateSpirvShaderModule(gpu.Device, spirv, name));
        byte[] bytes = Encoding.UTF8.GetBytes(name);
        fixed (byte* text = bytes) {
            var descriptor = WGPUComputePipelineDescriptor.Default; descriptor.Layout = (WGPUPipelineLayout*)GpuBinding.PipelineLayout(gpu, layout).Handle;
            descriptor.Compute.Module = (WGPUShaderModule*)shader.Handle; descriptor.Compute.EntryPoint = new() { Data = text, Length = (nuint)bytes.Length };
            var pipeline = gpu.Own(Wgpu.CreateComputePipeline(gpu.Device, descriptor));
            if (DeviceSession.Errors.TryPeek(out var error)) throw new InvalidOperationException(error);
            var encoder = Wgpu.CreateCommandEncoder(gpu.Device); var pass = Wgpu.BeginComputePass(encoder, WGPUComputePassDescriptor.Default);
            try {
                Wgpu.SetComputePipeline(pass, pipeline.GetWgpu<WGPUComputePipeline>()); Wgpu.SetBindGroup(pass, 0, group.GetWgpu<WGPUBindGroup>());
                Wgpu.DispatchWorkgroups(pass, 1); Wgpu.EndComputePass(pass); Submit(context.Gpu, encoder);
            } finally { Wgpu.Release(ref pass); Wgpu.Release(ref encoder); }
        }
        context.Capture.Commands.Add("dispatch(1,1,1); queue.submit");
    }
}
