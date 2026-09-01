using Sia.WebGPU;

namespace Sia.Graphics.Compatibility;

public static class GpuLegalizationPlanner
{
    public static GpuLegalizationPlan Resolve(
        IReadOnlyList<GpuBufferRequirement> requirements,
        GpuTargetProfile target)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(target);

        var storageCounts = new Dictionary<WGPUShaderStage, uint>();
        var uniformCounts = new Dictionary<WGPUShaderStage, uint>();
        var buffers = new GpuBufferLegalization[requirements.Count];
        var storageEligible = new bool[requirements.Count];
        var uniformEligible = new bool[requirements.Count];
        for (var index = 0; index < requirements.Count; index++) {
            var requirement = requirements[index];
            storageEligible[index] = CanUseStorage(requirement, target);
            uniformEligible[index] = CanUseUniform(requirement, target);
        }

        AllocateRequiredBindings(
            requirements,
            buffers,
            storageEligible,
            uniformEligible,
            true,
            target.MaxStorageBuffersPerShaderStage,
            storageCounts);
        AllocateRequiredBindings(
            requirements,
            buffers,
            uniformEligible,
            storageEligible,
            false,
            target.MaxUniformBuffersPerShaderStage,
            uniformCounts);

        for (var index = 0; index < requirements.Count; index++) {
            if (buffers[index] != null) {
                continue;
            }
            var requirement = requirements[index];
            if (storageEligible[index] && TryReserve(
                requirement.Visibility,
                target.MaxStorageBuffersPerShaderStage,
                storageCounts)) {
                buffers[index] = NativeStorage(requirement);
            }
            else if (uniformEligible[index] && TryReserve(
                requirement.Visibility,
                target.MaxUniformBuffersPerShaderStage,
                uniformCounts)) {
                buffers[index] = Uniform(requirement);
            }
            else {
                buffers[index] = Unsupported(requirement);
            }
        }
        return new GpuLegalizationPlan(buffers);
    }

    private static void AllocateRequiredBindings(
        IReadOnlyList<GpuBufferRequirement> requirements,
        GpuBufferLegalization[] buffers,
        bool[] eligible,
        bool[] alternativeEligible,
        bool storage,
        uint limit,
        Dictionary<WGPUShaderStage, uint> counts)
    {
        for (var index = 0; index < requirements.Count; index++) {
            if (!eligible[index] || alternativeEligible[index]) {
                continue;
            }
            var requirement = requirements[index];
            buffers[index] = TryReserve(requirement.Visibility, limit, counts)
                ? storage
                    ? NativeStorage(requirement)
                    : Uniform(requirement)
                : Unsupported(requirement);
        }
    }

    private static bool CanUseStorage(
        GpuBufferRequirement requirement,
        GpuTargetProfile target)
    {
        if ((requirement.Visibility & WGPUShaderStage.Vertex) != 0 &&
            !target.SupportsVertexStageStorageBuffers) {
            return false;
        }
        return requirement.MinimumStorageBindingSize <= target.MaxStorageBufferBindingSize;
    }

    private static GpuBufferLegalization NativeStorage(GpuBufferRequirement requirement) =>
        new(
            requirement,
            GpuBufferBindingKind.Storage,
            "buffer.native_storage",
            "The target supports the required storage-buffer shape.");

    private static bool CanUseUniform(
        GpuBufferRequirement requirement,
        GpuTargetProfile target) =>
        requirement.Access == GpuBufferAccess.ReadOnly &&
            !requirement.RuntimeSized &&
            requirement.UniformBindingSize is { } uniformSize &&
            uniformSize <= target.MaxUniformBufferBindingSize;

    private static GpuBufferLegalization Uniform(GpuBufferRequirement requirement) =>
        new(
            requirement,
            GpuBufferBindingKind.Uniform,
            "buffer.storage_to_uniform",
            "The bounded read-only resource fits the target uniform-buffer ABI.");

    private static GpuBufferLegalization Unsupported(GpuBufferRequirement requirement)
    {
        var reason = requirement.RuntimeSized
            ? "Runtime-sized storage buffers cannot be represented by a uniform buffer."
            : requirement.Access == GpuBufferAccess.ReadWrite
                ? "Read-write storage buffers cannot be represented by a uniform buffer."
                : requirement.UniformBindingSize == null
                    ? "No legalized uniform-buffer layout is available for this resource."
                    : "The resource exceeds the target buffer count or binding-size limits.";
        return new GpuBufferLegalization(
            requirement,
            GpuBufferBindingKind.Unsupported,
            "buffer.unsupported",
            reason);
    }

    private static bool TryReserve(
        WGPUShaderStage visibility,
        uint limit,
        Dictionary<WGPUShaderStage, uint> counts)
    {
        WGPUShaderStage[] stages = [
            WGPUShaderStage.Vertex,
            WGPUShaderStage.Fragment,
            WGPUShaderStage.Compute
        ];
        foreach (var stage in stages) {
            if ((visibility & stage) == 0) {
                continue;
            }
            if (counts.GetValueOrDefault(stage) >= limit) {
                return false;
            }
        }
        foreach (var stage in stages) {
            if ((visibility & stage) != 0) {
                counts[stage] = counts.GetValueOrDefault(stage) + 1;
            }
        }
        return true;
    }
}
