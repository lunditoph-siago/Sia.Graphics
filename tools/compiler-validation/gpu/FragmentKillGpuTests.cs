using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Sia.WebGPU;

namespace SiaGpuDiagnostics;

internal static partial class CompilerGpuTests
{
    private const string FragmentVertex = "@vertex fn main(@builtin(vertex_index) i:u32)->@builtin(position) vec4<f32>{var p=array<vec2<f32>,3>(vec2<f32>(-1.0,-1.0),vec2<f32>(3.0,-1.0),vec2<f32>(-1.0,3.0));return vec4<f32>(p[i],0.0,1.0);}";
    private sealed record FragmentCase(string Fixture, string Sample, uint Input, uint[] Expected, uint? Color = null);

    [ModuleInitializer]
    internal static void InitializeFragmentKill() => TestModules.Register("fragment-kill", registry => {
        FragmentCase[] samples = [
            new("NativeInvocationKillBranch", "killed", 0, [1, 10]), new("NativeInvocationKillBranch", "alive", 5, [14, 26]),
            new("NativeInvocationKillNested", "killed", 2, [3, 10]), new("NativeInvocationKillNested", "alive", 5, [14, 26]),
            new("NativeInvocationKillRepeated", "killed", 0, [1, 10]), new("NativeInvocationKillRepeated", "alive", 5, [22, 42]),
            new("NativeInvocationKillScalar", "killed", 1, [0, 0]), new("NativeInvocationKillScalar", "alive", 0, [9, 99]),
            new("NativeInvocationKillColor", "killed", 1, [43, 23], 37), new("NativeInvocationKillColor", "alive", 0, [9, 99], 109),
            new("NativeInvocationKillContinuing", "killed", 1, [40, 23], 37), new("NativeInvocationKillContinuing", "alive", 0, [158, 126], 1158),
            new("NativeInvocationKillContinuing", "break", 2, [40, 120], 1040),
            new("NativeInvocationKillContinuingRepeated", "killed", 1, [40, 23], 37), new("NativeInvocationKillContinuingRepeated", "alive", 0, [276, 132], 1276),
            new("NativeInvocationKillContinuingRepeated", "break", 2, [40, 120], 1040),
            new("NativeTerminateContinuing", "killed", 1, [40, 23], 37), new("NativeTerminateContinuing", "alive", 0, [158, 126], 1158),
            new("NativeTerminateContinuing", "break", 2, [40, 120], 1040),
            new("NativeTerminateContinuingRepeated", "killed", 1, [40, 23], 37), new("NativeTerminateContinuingRepeated", "alive", 0, [276, 132], 1276),
            new("NativeTerminateContinuingRepeated", "break", 2, [40, 120], 1040),
            new("CanonicalKillMultiple", "killed", 1, [0, 8], 37), new("CanonicalKillMultiple", "alive", 0, [2, 8], 4),
            new("CanonicalKillMultiple", "alternate", 2, [2, 8], 3),
            new("CanonicalTerminateMultiple", "killed", 1, [0, 8], 37), new("CanonicalTerminateMultiple", "alive", 0, [2, 8], 4),
            new("CanonicalTerminateMultiple", "alternate", 2, [2, 8], 3),
            new("CanonicalKillZero", "killed", 1, [5, 8], 37), new("CanonicalKillZero", "alive", 0, [5, 8], 5),
            new("CanonicalTerminateZero", "killed", 1, [5, 8], 37), new("CanonicalTerminateZero", "alive", 0, [5, 8], 5),
            new("CanonicalKillNestedContinue", "killed", 1, [0, 88], 37), new("CanonicalKillNestedContinue", "alive", 0, [2, 8], 4),
            new("CanonicalKillNestedContinue", "alternate", 2, [2, 8], 3), new("CanonicalKillNestedContinue", "ordinary", 3, [2, 88], 4),
            new("CanonicalTerminateNestedContinue", "killed", 1, [0, 88], 37), new("CanonicalTerminateNestedContinue", "alive", 0, [2, 8], 4),
            new("CanonicalTerminateNestedContinue", "alternate", 2, [2, 8], 3), new("CanonicalTerminateNestedContinue", "ordinary", 3, [2, 88], 4),
            new("DemotionFunction", "killed", 1, [43, 23], 37), new("DemotionFunction", "alive", 0, [19, 99], 128),
            new("NativeDemotePointer", "killed", 0, [1, 10]), new("NativeDemotePointer", "alive", 5, [14, 26]),
            new("NativeTerminatePointer", "killed", 0, [1, 10]), new("NativeTerminatePointer", "alive", 5, [14, 26]),
            new("NativeTerminateColor", "killed", 1, [43, 23], 37), new("NativeTerminateColor", "alive", 0, [9, 99], 109)
        ];
        foreach (var sample in samples)
            foreach (string variant in sample.Fixture == "DemotionFunction"
                ? new[] { "source-wgsl", "source-spirv", "canonical-wgsl", "canonical-spirv" }
                : new[] { "source-spirv", "canonical-wgsl", "canonical-spirv" })
                registry.Add(sample.Fixture + "/" + sample.Sample + "/" + variant,
                    context => RunFragmentKill(context, sample, variant), TestMode.Native, requiresGpu: true,
                    configureDevice: configuration => configuration.CompatibilityLimits.MaxStorageBuffersInFragmentStage =
                        Math.Max(configuration.CompatibilityLimits.MaxStorageBuffersInFragmentStage, 2));
    });

    private static async Task RunFragmentKill(TestContext context, FragmentCase sample, string variant)
    {
        byte[] text = Asset(sample.Fixture + (variant == "source-wgsl" ? ".source.wgsl" : ".wgsl")); string source = Encoding.UTF8.GetString(text);
        byte[]? spirv = variant is "canonical-wgsl" or "source-wgsl" ? null : Asset(sample.Fixture + (variant == "source-spirv" ? ".input.spv" : ".spv"));
        context.RecordShader(spirv is null ? "executed" : "wgsl-sidecar", source); context.RecordShader("vertex", FragmentVertex);
        context.Capture.Resources.Add(new {
            sample.Fixture, sample.Sample, Variant = variant, Input = new[] { sample.Input }, sample.Expected, ExpectedColor = sample.Color,
            WgslSha256 = Convert.ToHexString(SHA256.HashData(text)), SpirvSha256 = spirv is null ? null : Convert.ToHexString(SHA256.HashData(spirv)),
            VertexSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FragmentVertex))), Width = 1, Height = 1,
            ColorFormat = sample.Color.HasValue ? "R32Uint" : null, ColorClear = sample.Color.HasValue ? 37u : (uint?)null,
            DepthFormat = "Depth32Float", DepthCompare = "Always", DepthWrite = false, SampleCount = 1
        });
        using var gpu = new GpuResources(context.Gpu.Device, context.Gpu.Queue);
        GpuResource input = gpu.Upload<uint>([sample.Input], WGPUBufferUsage.Storage);
        GpuResource output = gpu.Upload<uint>(new uint[2], WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
        context.Progress("Creating fragment pipeline " + variant);
        var color = RenderFragmentKill(context, gpu, input, output, source, spirv, sample.Color.HasValue);
        context.Progress("Reading fragment storage");
        uint[] actual = await Read(context, gpu, output, 2);
        context.CompareWords("output", sample.Expected, actual);
        if (sample.Color is uint expected) {
            uint pixel = await ReadFragmentColor(context, gpu, color);
            context.CompareWords("color", [expected], [pixel]);
        }
    }

    private static unsafe GpuResource RenderFragmentKill(TestContext context, GpuResources gpu, GpuResource input, GpuResource output,
        string source, byte[]? spirv, bool hasColor)
    {
        var layout = GpuBinding.Layout(gpu, [GpuBinding.Buffer(0, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Fragment),
            GpuBinding.Buffer(1, WGPUBufferBindingType.Storage, WGPUShaderStage.Fragment)]);
        var group = GpuBinding.Group(gpu, layout, [GpuBinding.Buffer(0, input), GpuBinding.Buffer(1, output)]);
        var pipelineLayout = GpuBinding.PipelineLayout(gpu, layout);
        var vertex = gpu.Own(Wgpu.CreateWgslShaderModule(gpu.Device, FragmentVertex, "vertex"));
        var fragment = gpu.Own(spirv is null ? Wgpu.CreateWgslShaderModule(gpu.Device, source, "fragment") : Wgpu.CreateSpirvShaderModule(gpu.Device, spirv, "fragment"));
        CheckFragmentError();
        var depth = FragmentTexture(gpu, WGPUTextureFormat.Depth32Float, copy: false);
        var depthView = gpu.Own(Wgpu.CreateTextureView(depth.GetWgpu<WGPUTexture>(), WGPUTextureViewDescriptor.Default));
        var color = hasColor ? FragmentTexture(gpu, WGPUTextureFormat.R32Uint, copy: true) : default;
        var colorView = hasColor ? gpu.Own(Wgpu.CreateTextureView(color.GetWgpu<WGPUTexture>(), WGPUTextureViewDescriptor.Default)) : default;
        byte[] name = Encoding.UTF8.GetBytes("main");
        fixed (byte* entry = name) {
            var descriptor = WGPURenderPipelineDescriptor.Default;
            descriptor.Layout = (WGPUPipelineLayout*)pipelineLayout.Handle;
            descriptor.Vertex.Module = (WGPUShaderModule*)vertex.Handle; descriptor.Vertex.EntryPoint = new() { Data = entry, Length = (nuint)name.Length };
            descriptor.Primitive.Topology = WGPUPrimitiveTopology.TriangleList; descriptor.Primitive.CullMode = WGPUCullMode.None;
            var target = WGPUColorTargetState.Default; target.Format = WGPUTextureFormat.R32Uint; target.WriteMask = WGPUColorWriteMask.All;
            var frag = WGPUFragmentState.Default; frag.Module = (WGPUShaderModule*)fragment.Handle;
            frag.EntryPoint = new() { Data = entry, Length = (nuint)name.Length }; frag.TargetCount = hasColor ? 1u : 0u; frag.Targets = hasColor ? &target : null;
            descriptor.Fragment = &frag;
            var stencil = WGPUDepthStencilState.Default; stencil.Format = WGPUTextureFormat.Depth32Float;
            stencil.DepthWriteEnabled = WGPUOptionalBool.False; stencil.DepthCompare = WGPUCompareFunction.Always; descriptor.DepthStencil = &stencil;
            var pipeline = gpu.Own(Wgpu.CreateRenderPipeline(gpu.Device, descriptor)); CheckFragmentError();
            var encoder = Wgpu.CreateCommandEncoder(gpu.Device);
            var colorAttachment = WGPURenderPassColorAttachment.Default;
            colorAttachment.View = (WGPUTextureView*)colorView.Handle; colorAttachment.LoadOp = WGPULoadOp.Clear; colorAttachment.StoreOp = WGPUStoreOp.Store;
            colorAttachment.ClearValue = new() { R = 37 };
            var depthAttachment = WGPURenderPassDepthStencilAttachment.Default; depthAttachment.View = (WGPUTextureView*)depthView.Handle;
            depthAttachment.DepthClearValue = 1; depthAttachment.DepthLoadOp = WGPULoadOp.Clear; depthAttachment.DepthStoreOp = WGPUStoreOp.Store;
            var passDescriptor = WGPURenderPassDescriptor.Default; passDescriptor.ColorAttachmentCount = hasColor ? 1u : 0u;
            passDescriptor.ColorAttachments = hasColor ? &colorAttachment : null; passDescriptor.DepthStencilAttachment = &depthAttachment;
            var pass = Wgpu.BeginRenderPass(encoder, passDescriptor);
            try {
                Wgpu.SetRenderPipeline(pass, pipeline.GetWgpu<WGPURenderPipeline>()); Wgpu.SetBindGroup(pass, 0, group.GetWgpu<WGPUBindGroup>());
                Wgpu.Draw(pass, 3); Wgpu.EndRenderPass(pass); Submit(context.Gpu, encoder);
                context.Capture.Commands.Add("beginRenderPass(1x1); clear depth=1" + (hasColor ? " color=37" : "; no color attachment"));
                context.Capture.Commands.Add("setRenderPipeline; setBindGroup(0); draw(3,1,0,0); endRenderPass; queue.submit");
                CheckFragmentError();
            } finally { Wgpu.Release(ref pass); Wgpu.Release(ref encoder); }
        }
        return color;
    }

    private static unsafe GpuResource FragmentTexture(GpuResources gpu, WGPUTextureFormat format, bool copy)
    {
        var descriptor = WGPUTextureDescriptor.Default; descriptor.Dimension = WGPUTextureDimension._2D;
        descriptor.Size = new() { Width = 1, Height = 1, DepthOrArrayLayers = 1 }; descriptor.Format = format;
        descriptor.MipLevelCount = 1; descriptor.SampleCount = 1; descriptor.Usage = WGPUTextureUsage.RenderAttachment | (copy ? WGPUTextureUsage.CopySrc : 0);
        return gpu.Own(Wgpu.CreateTexture(gpu.Device, descriptor));
    }

    private static async Task<uint> ReadFragmentColor(TestContext context, GpuResources gpu, GpuResource color)
    {
        var staging = gpu.Buffer(256, WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst).GetWgpu<WGPUBuffer>();
        var encoder = Wgpu.CreateCommandEncoder(gpu.Device);
        try { Wgpu.CopyTextureToBuffer(encoder, color.GetWgpu<WGPUTexture>(), staging, 1, 1, 256); Submit(context.Gpu, encoder); }
        finally { Wgpu.Release(ref encoder); }
        context.Capture.Commands.Add("copyTextureToBuffer(R32Uint,1x1,bytesPerRow=256); queue.submit; mapRead(256)");
        await context.Gpu.WaitAsync(Wgpu.MapBufferReadAsync(staging, 0, 256));
        try { return Wgpu.GetMappedRangeReadOnly<uint>(staging, 0, 1)[0]; } finally { Wgpu.UnmapBuffer(staging); }
    }

    private static void CheckFragmentError()
    {
        if (DeviceSession.Errors.TryPeek(out var error)) throw new InvalidOperationException(error);
    }
}
