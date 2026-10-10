using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.Tests;
using Sia.WebGPU;

namespace SiaGpuDiagnostics;

internal static partial class CompilerGpuTests
{
    [ModuleInitializer]
    internal static void InitializeHandleAliases() => TestModules.Register("handle-alias", registry => {
        foreach (string fixture in new[] { "Image", "Sampler" })
            foreach (string variant in new[] { "wgsl", "spirv", "native-wgsl" })
                registry.Add(fixture + "/" + variant, context => RunHandleAlias(context, fixture, variant), TestMode.Native, requiresGpu: true);
    });

    private static async Task RunHandleAlias(TestContext context, string fixture, string variant)
    {
        string input = HandleAliasFixtures.Source(fixture);
        var module = WgslReader.Parse(input); var target = SpirvCompilationTarget.Default;
        byte[] native = SpirvWriter.Write(module, target);
        string source = WgslWriter.Write(variant == "native-wgsl" ? SpirvReader.Parse(native) : module, target);
        byte[]? spirv = variant == "spirv" ? native : null;
        uint[] expected = HandleAliasFixtures.Expected(fixture);
        context.RecordShader(spirv is null ? "executed" : "wgsl-sidecar", source);
        context.Capture.Resources.Add(new { Fixture = fixture, Variant = variant, TargetIdentity = target.Identity,
            InputSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))),
            NativeSha256 = Convert.ToHexString(SHA256.HashData(native)), Expected = expected,
            TextureClearValues = fixture == "Image" ? new[] { 13.0, 29.0 } : [0.25],
            Purpose = "Graph-owned resource aliases: distinct image identity, ordered coordinate calls, sampler helper expansion" });
        using var gpu = new GpuResources(context.Gpu.Device, context.Gpu.Queue);
        var output = gpu.Upload<uint>(new uint[expected.Length], WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
        DispatchHandleAlias(context, gpu, output, source, spirv, fixture == "Sampler");
        uint[] actual = await Read(context, gpu, output, expected.Length);
        context.CompareWords("values", expected, actual);
    }

    private static unsafe GpuResource ClearHandleTexture(TestContext context, GpuResources gpu, bool floating, double value)
    {
        var descriptor = WGPUTextureDescriptor.Default; descriptor.Dimension = WGPUTextureDimension._2D;
        descriptor.Size = new() { Width = 1, Height = 1, DepthOrArrayLayers = 1 };
        descriptor.Format = floating ? WGPUTextureFormat.R32Float : WGPUTextureFormat.R32Uint;
        descriptor.MipLevelCount = 1; descriptor.SampleCount = 1;
        descriptor.Usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.TextureBinding;
        var texture = gpu.Own(Wgpu.CreateTexture(gpu.Device, descriptor));
        var view = gpu.Own(Wgpu.CreateTextureView(texture.GetWgpu<WGPUTexture>(), WGPUTextureViewDescriptor.Default));
        var attachment = WGPURenderPassColorAttachment.Default; attachment.View = (WGPUTextureView*)view.Handle;
        attachment.LoadOp = WGPULoadOp.Clear; attachment.StoreOp = WGPUStoreOp.Store; attachment.ClearValue = new() { R = value };
        var passDescriptor = WGPURenderPassDescriptor.Default; passDescriptor.ColorAttachments = &attachment; passDescriptor.ColorAttachmentCount = 1;
        var encoder = Wgpu.CreateCommandEncoder(gpu.Device);
        try {
            var pass = Wgpu.BeginRenderPass(encoder, passDescriptor);
            try { Wgpu.EndRenderPass(pass); Submit(context.Gpu, encoder); }
            finally { Wgpu.Release(ref pass); }
        } finally { Wgpu.Release(ref encoder); }
        context.Capture.Commands.Add($"clearTexture({descriptor.Format},1x1,R={value}); queue.submit");
        return view;
    }

    private static unsafe void DispatchHandleAlias(TestContext context, GpuResources gpu, GpuResource output, string source, byte[]? spirv, bool sampling)
    {
        var first = ClearHandleTexture(context, gpu, sampling, sampling ? 0.25 : 13);
        var second = sampling ? gpu.Own(Wgpu.CreateSampler(gpu.Device, WGPUSamplerDescriptor.Default))
            : ClearHandleTexture(context, gpu, false, 29);
        var textureLayout = WGPUBindGroupLayoutEntry.Default; textureLayout.Binding = 0; textureLayout.Visibility = WGPUShaderStage.Compute;
        textureLayout.Texture = WGPUTextureBindingLayout.Default; textureLayout.Texture.ViewDimension = WGPUTextureViewDimension._2D;
        textureLayout.Texture.SampleType = sampling ? WGPUTextureSampleType.UnfilterableFloat : WGPUTextureSampleType.Uint;
        var secondLayout = sampling ? WGPUBindGroupLayoutEntry.Default : textureLayout; secondLayout.Binding = 1; secondLayout.Visibility = WGPUShaderStage.Compute;
        if (sampling) { secondLayout.Sampler = WGPUSamplerBindingLayout.Default; secondLayout.Sampler.Type = WGPUSamplerBindingType.NonFiltering; }
        var layout = GpuBinding.Layout(gpu, [textureLayout, secondLayout, GpuBinding.Buffer(2, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute)]);
        var firstEntry = WGPUBindGroupEntry.Default; firstEntry.Binding = 0; firstEntry.TextureView = (WGPUTextureView*)first.Handle;
        var secondEntry = WGPUBindGroupEntry.Default; secondEntry.Binding = 1;
        if (sampling) secondEntry.Sampler = (WGPUSampler*)second.Handle; else secondEntry.TextureView = (WGPUTextureView*)second.Handle;
        var group = GpuBinding.Group(gpu, layout, [firstEntry, secondEntry, GpuBinding.Buffer(2, output)]);
        var shader = gpu.Own(spirv is null ? Wgpu.CreateWgslShaderModule(gpu.Device, source, "main") : Wgpu.CreateSpirvShaderModule(gpu.Device, spirv, "main"));
        byte[] name = Encoding.UTF8.GetBytes("main");
        fixed (byte* entry = name) {
            var descriptor = WGPUComputePipelineDescriptor.Default; descriptor.Layout = (WGPUPipelineLayout*)GpuBinding.PipelineLayout(gpu, layout).Handle;
            descriptor.Compute.Module = (WGPUShaderModule*)shader.Handle; descriptor.Compute.EntryPoint = new() { Data = entry, Length = (nuint)name.Length };
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
