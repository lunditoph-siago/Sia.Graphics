using Sia.Graphics.Compatibility;

namespace Sia.Graphics.UI;

internal static class UiLegalizationPlanner
{
    public static UiLegalizationPlan Resolve(
        UiPipelineRequirements requirements,
        GpuTargetProfile target)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(target);

        var bufferPlan = GpuLegalizationPlanner.Resolve(requirements.Buffers, target);
        if (bufferPlan.IsSupported && bufferPlan.Buffers.All(
            static buffer => buffer.BindingKind == GpuBufferBindingKind.Storage)) {
            return new UiLegalizationPlan(
                UiVertexDataMode.StorageBuffers,
                bufferPlan,
                "ui.native_storage_buffers");
        }

        if (requirements.VertexBufferCount <= target.MaxVertexBuffers &&
            requirements.VertexAttributeCount <= target.MaxVertexAttributes &&
            requirements.VertexBufferArrayStride <= target.MaxVertexBufferArrayStride &&
            requirements.VertexBufferArrayStride <= target.MaxBufferSize) {
            return new UiLegalizationPlan(
                UiVertexDataMode.VertexBuffer,
                bufferPlan,
                "ui.storage_buffers_to_vertex_stream");
        }

        var reasons = string.Join(
            " ",
            bufferPlan.Buffers
                .Where(static buffer => !buffer.IsSupported)
                .Select(static buffer => $"{buffer.Requirement.Name}: {buffer.Reason}"));
        throw new NotSupportedException(
            $"The target cannot legalize the UI vertex-data ABI. {reasons}");
    }
}
