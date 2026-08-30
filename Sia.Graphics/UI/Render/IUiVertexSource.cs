using Sia.WebGPU;

namespace Sia.Graphics.UI;

internal unsafe interface IUiVertexSource
{
    Entity LoadVertexShaderModule(World world, WgpuHandle<WGPUDevice> device, Entity fragmentShaderModule);

    int WriteBindGroupLayoutEntries(Span<WGPUBindGroupLayoutEntry> entries);

    int WriteVertexAttributes(Span<WGPUVertexAttribute> attributes);

    int WriteBindGroupEntries(Span<WGPUBindGroupEntry> entries);

    bool UploadFrame(World world, Entity device, WgpuHandle<WGPUQueue> queue, UiRenderCache cache);

    bool EnsureBuffers(World world, Entity device);

    void BindForDraw(WgpuHandle<WGPURenderPassEncoder> renderPass);
}
