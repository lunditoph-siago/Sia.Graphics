using Sia.Graphics.Compatibility;
using Sia.WebGPU;

namespace Sia.Graphics.UI;

internal static class UiRequirementAnalysis
{
    public static UiPipelineRequirements Analyze() => new(
        [
            new GpuBufferRequirement(
                "primitives",
                WGPUShaderStage.Vertex,
                GpuBufferAccess.ReadOnly,
                true,
                UiPrimitive.Stride),
            new GpuBufferRequirement(
                "paint_order",
                WGPUShaderStage.Vertex,
                GpuBufferAccess.ReadOnly,
                true,
                sizeof(uint))
        ],
        1,
        7,
        UiPrimitive.Stride);
}
