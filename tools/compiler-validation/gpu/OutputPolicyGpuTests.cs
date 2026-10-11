using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Sia.WebGPU;

namespace SiaGpuDiagnostics;

internal static partial class CompilerGpuTests
{
    private sealed record OutputProbe(string Fixture, string Sample, uint[] Input, uint[] Expected);
    private sealed record OutputRaster(string Fixture, bool Vertex, bool Marker, uint Depth);

    [ModuleInitializer]
    internal static void InitializeOutputPolicy() => TestModules.Register("output-policy", registry => {
        OutputProbe[] probes = [
            new("OutputPositionProbe", "positive-y", [0x3e800000, 0x3f400000], [0xbf400000, 0x3e800000]),
            new("OutputPositionProbe", "negative-y", [0xbe800000, 0xbf400000], [0x3f400000, 0xbe800000]),
            new("OutputDepthProbe", "below", [0xbf000000, 71], [0, 71]),
            new("OutputDepthProbe", "inside", [0x3e800000, 83], [0x3e800000, 83]),
            new("OutputDepthProbe", "above", [0x40000000, 97], [0x3f800000, 97]),
            new("BuiltinIdentityOrderedProbe", "below", [0xbf000000, 0], [0, 1]),
            new("BuiltinIdentityOrderedProbe", "inside", [0x3e800000, 0], [0x3e800000, 1]),
            new("BuiltinIdentityOrderedProbe", "above", [0x40000000, 0], [0x3f800000, 1])
        ];
        foreach (var probe in probes)
            foreach (string variant in new[] { "canonical-wgsl", "canonical-spirv" })
                registry.Add("probe/" + probe.Fixture + "/" + probe.Sample + "/" + variant,
                    context => RunOutputProbe(context, probe, variant), TestMode.Native, requiresGpu: true);
        OutputRaster[] rasters = [new("OutputRasterVertex", true, false, 0x3f000000), new("OutputRasterVertexStruct", true, true, 0x3f000000),
            new("OutputRasterDepthBelow", false, false, 0), new("OutputRasterDepthInside", false, false, 0x3e800000),
            new("OutputRasterDepthAbove", false, false, 0x3f800000)];
        foreach (var raster in rasters)
            foreach (string variant in new[] { "source-wgsl", "default-spirv", "optout-spirv" })
                registry.Add("raster/" + raster.Fixture + "/" + variant,
                    context => RunOutputRaster(context, raster, variant), TestMode.Native, requiresGpu: true);
    });

    private static async Task RunOutputProbe(TestContext context, OutputProbe probe, string variant)
    {
        byte[] text = Asset(probe.Fixture + ".wgsl"); string source = Encoding.UTF8.GetString(text);
        byte[]? spirv = variant == "canonical-spirv" ? Asset(probe.Fixture + ".spv") : null;
        context.RecordShader(spirv is null ? "executed" : "wgsl-sidecar", source);
        context.Capture.Resources.Add(new { probe.Fixture, probe.Sample, Variant = variant, probe.Input, probe.Expected,
            Purpose = "Execute exact target-prepared output function independently of fixed-function raster clamping",
            WgslSha256 = Convert.ToHexString(SHA256.HashData(text)), SpirvSha256 = spirv is null ? null : Convert.ToHexString(SHA256.HashData(spirv)) });
        using var gpu = new GpuResources(context.Gpu.Device, context.Gpu.Queue);
        var input = gpu.Upload<uint>(probe.Input, WGPUBufferUsage.Storage);
        var output = gpu.Upload<uint>(new uint[2], WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
        Dispatch(context, gpu, [input, output], [GpuBinding.Buffer(0, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
            GpuBinding.Buffer(1, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute)], source, spirv, "main");
        uint[] actual = await Read(context, gpu, output, 2);
        context.CompareWords("output", probe.Expected, actual);
    }

    private static async Task RunOutputRaster(TestContext context, OutputRaster raster, string variant)
    {
        byte[] text = Asset(raster.Fixture + ".source.wgsl"); string source = Encoding.UTF8.GetString(text);
        byte[]? spirv = variant == "source-wgsl" ? null : Asset(raster.Fixture + (variant == "default-spirv" ? ".spv" : ".optout.spv"));
        string companion = raster.Vertex ? (raster.Marker
            ? "@fragment fn main(@location(0) @interpolate(flat) marker:u32)->@location(0) u32{return marker;}"
            : "@fragment fn main()->@location(0) u32{return 19u;}") : FragmentVertex;
        // The native consumer uses the supplied positions: target-enabled Y negation moves the upper triangle to the lower half.
        bool inverted = raster.Vertex && variant == "default-spirv";
        uint drawn = raster.Marker ? 1u : 19u;
        uint[] color = raster.Vertex ? (inverted ? [37, 37, drawn, drawn] : [drawn, drawn, 37, 37]) : [19, 19, 19, 19];
        uint[] depth = raster.Vertex ? (inverted ? [0x3f800000, 0x3f800000, raster.Depth, raster.Depth]
            : [raster.Depth, raster.Depth, 0x3f800000, 0x3f800000]) : [raster.Depth, raster.Depth, raster.Depth, raster.Depth];
        context.RecordShader(spirv is null ? "executed" : "wgsl-sidecar", source); context.RecordShader("companion", companion);
        context.Capture.Resources.Add(new { raster.Fixture, Variant = variant, raster.Vertex, raster.Marker,
            AdjustCoordinateSpace = variant != "optout-spirv", ClampFragmentDepth = variant != "optout-spirv",
            WgslSha256 = Convert.ToHexString(SHA256.HashData(text)), SpirvSha256 = spirv is null ? null : Convert.ToHexString(SHA256.HashData(spirv)),
            CompanionSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(companion))),
            Width = 2, Height = 2, ExpectedColor = color, ExpectedDepthBits = depth,
            DepthWrite = true, DepthCompare = "Always", DepthClear = 1f, ColorClear = 37u,
            DepthOptOutNote = "Fixed-function depth clamping may match opt-out; pure-function probes verify shader arithmetic separately" });
        using var gpu = new GpuResources(context.Gpu.Device, context.Gpu.Queue);
        var textures = DrawOutputRaster(context, gpu, source, spirv, companion, raster.Vertex);
        uint[] actualColor = await ReadOutputTexture(context, gpu, textures.Color);
        uint[] actualDepth = await ReadOutputTexture(context, gpu, textures.Depth);
        context.CompareWords("color", color, actualColor);
        context.CompareWords("depth-bits", depth, actualDepth);
    }

    private static unsafe (GpuResource Color, GpuResource Depth) DrawOutputRaster(TestContext context, GpuResources gpu,
        string source, byte[]? spirv, string companion, bool vertexStage)
    {
        var tested = gpu.Own(spirv is null ? Wgpu.CreateWgslShaderModule(gpu.Device, source, "tested") : Wgpu.CreateSpirvShaderModule(gpu.Device, spirv, "tested"));
        var other = gpu.Own(Wgpu.CreateWgslShaderModule(gpu.Device, companion, "companion")); CheckFragmentError();
        GpuResource Texture(WGPUTextureFormat format) {
            var descriptor = WGPUTextureDescriptor.Default; descriptor.Dimension = WGPUTextureDimension._2D;
            descriptor.Size = new() { Width = 2, Height = 2, DepthOrArrayLayers = 1 }; descriptor.Format = format;
            descriptor.MipLevelCount = 1; descriptor.SampleCount = 1; descriptor.Usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.CopySrc;
            return gpu.Own(Wgpu.CreateTexture(gpu.Device, descriptor));
        }
        var color = Texture(WGPUTextureFormat.R32Uint); var depth = Texture(WGPUTextureFormat.Depth32Float);
        var colorView = gpu.Own(Wgpu.CreateTextureView(color.GetWgpu<WGPUTexture>(), WGPUTextureViewDescriptor.Default));
        var depthView = gpu.Own(Wgpu.CreateTextureView(depth.GetWgpu<WGPUTexture>(), WGPUTextureViewDescriptor.Default));
        byte[] name = Encoding.UTF8.GetBytes("main");
        fixed (byte* entry = name) {
            var descriptor = WGPURenderPipelineDescriptor.Default;
            descriptor.Vertex.Module = (WGPUShaderModule*)(vertexStage ? tested.Handle : other.Handle);
            descriptor.Vertex.EntryPoint = new() { Data = entry, Length = (nuint)name.Length };
            descriptor.Primitive.Topology = WGPUPrimitiveTopology.TriangleList; descriptor.Primitive.CullMode = WGPUCullMode.None;
            var target = WGPUColorTargetState.Default; target.Format = WGPUTextureFormat.R32Uint; target.WriteMask = WGPUColorWriteMask.All;
            var fragment = WGPUFragmentState.Default; fragment.Module = (WGPUShaderModule*)(vertexStage ? other.Handle : tested.Handle);
            fragment.EntryPoint = new() { Data = entry, Length = (nuint)name.Length }; fragment.TargetCount = 1; fragment.Targets = &target; descriptor.Fragment = &fragment;
            var stencil = WGPUDepthStencilState.Default; stencil.Format = WGPUTextureFormat.Depth32Float;
            stencil.DepthWriteEnabled = WGPUOptionalBool.True; stencil.DepthCompare = WGPUCompareFunction.Always; descriptor.DepthStencil = &stencil;
            var pipeline = gpu.Own(Wgpu.CreateRenderPipeline(gpu.Device, descriptor)); CheckFragmentError();
            var encoder = Wgpu.CreateCommandEncoder(gpu.Device);
            var colorAttachment = WGPURenderPassColorAttachment.Default; colorAttachment.View = (WGPUTextureView*)colorView.Handle;
            colorAttachment.LoadOp = WGPULoadOp.Clear; colorAttachment.StoreOp = WGPUStoreOp.Store; colorAttachment.ClearValue = new() { R = 37 };
            var depthAttachment = WGPURenderPassDepthStencilAttachment.Default; depthAttachment.View = (WGPUTextureView*)depthView.Handle;
            depthAttachment.DepthClearValue = 1; depthAttachment.DepthLoadOp = WGPULoadOp.Clear; depthAttachment.DepthStoreOp = WGPUStoreOp.Store;
            var passDescriptor = WGPURenderPassDescriptor.Default; passDescriptor.ColorAttachmentCount = 1;
            passDescriptor.ColorAttachments = &colorAttachment; passDescriptor.DepthStencilAttachment = &depthAttachment;
            var pass = Wgpu.BeginRenderPass(encoder, passDescriptor);
            try {
                Wgpu.SetRenderPipeline(pass, pipeline.GetWgpu<WGPURenderPipeline>()); Wgpu.Draw(pass, 3); Wgpu.EndRenderPass(pass); Submit(context.Gpu, encoder);
                context.Capture.Commands.Add("beginRenderPass(2x2); clear color=37 depth=1; setRenderPipeline; draw(3,1,0,0); endRenderPass; queue.submit");
                CheckFragmentError();
            }
            finally { Wgpu.Release(ref pass); Wgpu.Release(ref encoder); }
        }
        return (color, depth);
    }

    private static async Task<uint[]> ReadOutputTexture(TestContext context, GpuResources gpu, GpuResource texture)
    {
        var staging = gpu.Buffer(512, WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst).GetWgpu<WGPUBuffer>();
        var encoder = Wgpu.CreateCommandEncoder(gpu.Device);
        try { Wgpu.CopyTextureToBuffer(encoder, texture.GetWgpu<WGPUTexture>(), staging, 2, 2, 256); Submit(context.Gpu, encoder); }
        finally { Wgpu.Release(ref encoder); }
        context.Capture.Commands.Add("copyTextureToBuffer(2x2,bytesPerRow=256); queue.submit; mapRead(512)");
        await context.Gpu.WaitAsync(Wgpu.MapBufferReadAsync(staging, 0, 512));
        try { var words = Wgpu.GetMappedRangeReadOnly<uint>(staging, 0, 128); return [words[0], words[1], words[64], words[65]]; }
        finally { Wgpu.UnmapBuffer(staging); }
    }
}
