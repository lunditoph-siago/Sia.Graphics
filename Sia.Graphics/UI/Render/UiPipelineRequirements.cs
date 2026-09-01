using Sia.Graphics.Compatibility;

namespace Sia.Graphics.UI;

internal sealed record UiPipelineRequirements(
    IReadOnlyList<GpuBufferRequirement> Buffers,
    uint VertexBufferCount,
    uint VertexAttributeCount,
    ulong VertexBufferArrayStride);
