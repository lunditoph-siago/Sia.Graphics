using Sia.RenderGraph;

namespace Sia.WebGPU;

public readonly record struct WgpuRenderGraphBufferPlan(
    CompiledRenderGraphBuffer Resource,
    WGPUBufferUsage Usage);
