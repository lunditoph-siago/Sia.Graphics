using System.Runtime.InteropServices;
using Sia;
using Sia.Graphics.Reactive;
using Sia.Graphics.Text;
using Sia.WebGPU;

namespace Sia.Graphics.UI;

public sealed class UiRenderer(UiPipeline pipeline)
{
    private Entity _vertexBuffer;
    private ulong _vertexBufferCapacity;
    private long _uploadedVersion = -1;
    private Size? _uploadedViewport;

    public void Render(
        World world,
        WgpuReactiveRenderGraphPassContext context,
        RenderGraphTextureKey output,
        Size viewport,
        WGPULoadOp loadOp = WGPULoadOp.Load)
    {
        var batches = PrepareFrame(world, viewport);
        var view = context.GetTextureView(output);
        EncodeDrawCalls(world, context.CommandEncoder, view, batches, loadOp);
    }

    public List<UiBatch> PrepareFrame(World world, Size viewport)
    {
        var cache = world.AcquireAddon<UiRenderCache>();
        cache.Prepare();
        var queue = pipeline.Queue.GetWgpu<WGPUQueue>();

        if (_uploadedViewport != viewport) {
            var projection = UiOrthographicProjection.Build(viewport);
            Wgpu.WriteBuffer<float>(
                queue, pipeline.ViewUniformBuffer.GetWgpu<WGPUBuffer>(), 0, projection);
            _uploadedViewport = viewport;
        }

        if (_uploadedVersion != cache.PreparedVersion) {
            var vertices = CollectionsMarshal.AsSpan(cache.VertexStorage);
            if (vertices.Length > 0) {
                EnsureVertexBufferCapacity(world, (ulong)vertices.Length * UiVertexLayout.Stride);
                Wgpu.WriteBuffer<UiVertex>(
                    queue, _vertexBuffer.GetWgpu<WGPUBuffer>(), 0, vertices);
            }
            _uploadedVersion = cache.PreparedVersion;
        }

        return cache.Batches;
    }

    private void EnsureVertexBufferCapacity(World world, ulong requiredBytes)
    {
        if (_vertexBufferCapacity >= requiredBytes)
            return;

        var newCapacity = _vertexBufferCapacity == 0 ? requiredBytes : _vertexBufferCapacity;
        while (newCapacity < requiredBytes)
            newCapacity *= 2;

        if (_vertexBuffer.IsValid)
            _vertexBuffer.Destroy();

        _vertexBuffer = world.CreateWgpuBuffer(pipeline.Device, new WGPUBufferDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUBufferUsage.Vertex | WGPUBufferUsage.CopyDst,
            Size = newCapacity,
            MappedAtCreation = 0
        });
        _vertexBufferCapacity = newCapacity;
    }

    public unsafe void EncodeDrawCalls(
        World world,
        WgpuHandle<WGPUCommandEncoder> encoder,
        WgpuHandle<WGPUTextureView> target,
        List<UiBatch> batches,
        WGPULoadOp loadOp)
    {
        Span<WGPURenderPassColorAttachment> colorAttachments = stackalloc WGPURenderPassColorAttachment[1];
        colorAttachments[0] = WGPURenderPassColorAttachment.Default;
        colorAttachments[0].View = ToPointer(target);
        colorAttachments[0].LoadOp = loadOp;
        colorAttachments[0].StoreOp = WGPUStoreOp.Store;
        colorAttachments[0].ClearValue = new WGPUColor { R = 0, G = 0, B = 0, A = 0 };

        fixed (WGPURenderPassColorAttachment* colorAttachmentsPtr = colorAttachments) {
            var descriptor = WGPURenderPassDescriptor.Default;
            descriptor.ColorAttachmentCount = 1;
            descriptor.ColorAttachments = colorAttachmentsPtr;

            var renderPass = Wgpu.BeginRenderPass(encoder, in descriptor);
            Wgpu.SetRenderPipeline(renderPass, pipeline.RenderPipeline.GetWgpu<WGPURenderPipeline>());
            Wgpu.SetBindGroup(renderPass, 0, pipeline.ViewBindGroup.GetWgpu<WGPUBindGroup>());

            if (batches.Count > 0) {
                Wgpu.SetVertexBuffer(renderPass, 0, _vertexBuffer.GetWgpu<WGPUBuffer>());
                foreach (var batch in batches) {
                    var textureBindGroup = batch.TextureKey is FontAtlas atlas
                        ? atlas.GetOrCreateBindGroup(world, pipeline)
                        : pipeline.DefaultTextureBindGroup.GetWgpu<WGPUBindGroup>();
                    Wgpu.SetBindGroup(renderPass, 1, textureBindGroup);
                    Wgpu.Draw(renderPass, (uint)batch.VertexCount, firstVertex: (uint)batch.VertexOffset);
                }
            }

            Wgpu.EndRenderPass(renderPass);
        }
    }

    private static unsafe WGPUTextureView* ToPointer(WgpuHandle<WGPUTextureView> handle) =>
        (WGPUTextureView*)handle.DangerousGetHandle();
}
