using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler.Legalization;

public sealed class SpirvLegalizationPlanner
{
    public SpirvLegalizationPlan Resolve(
        SpirvKernel kernel,
        SpirvTargetProfile target)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(target);

        var parameters = kernel.Parameters.ToArray();
        var resources = new List<SpirvResourceLegalization>();
        var uniformCandidates = new Dictionary<int, SpirvKernelParameter>();
        foreach (var parameter in parameters) {
            if (parameter.Kind == SpirvKernelParameterKind.ReadOnlyStorageBuffer &&
                TryCreateUniformParameter(parameter, target, out var uniformParameter)) {
                uniformCandidates.Add(parameter.Position, uniformParameter);
            }
        }

        var mandatoryStorageCount = parameters.Count(parameter =>
            parameter.Kind is (
                SpirvKernelParameterKind.ReadOnlyStorageBuffer or
                SpirvKernelParameterKind.StorageBuffer) &&
                !uniformCandidates.ContainsKey(parameter.Position));
        if (!target.SupportsStorageBuffers && mandatoryStorageCount != 0 ||
            mandatoryStorageCount > target.MaxStorageBuffersPerShaderStage) {
            throw new InvalidDataException(
                "The target profile cannot provide the required storage-buffer bindings.");
        }

        var storageCount = 0;
        var uniformCount = 0;
        var mandatoryStorageRemaining = mandatoryStorageCount;
        for (var index = 0; index < parameters.Length; index++) {
            var parameter = parameters[index];
            if (parameter.Kind is not (
                SpirvKernelParameterKind.ReadOnlyStorageBuffer or
                SpirvKernelParameterKind.StorageBuffer)) {
                continue;
            }

            var hasUniformFallback = uniformCandidates.TryGetValue(
                parameter.Position,
                out var uniformParameter);
            if (!hasUniformFallback) {
                mandatoryStorageRemaining--;
            }
            var storageSlotAvailable = hasUniformFallback
                ? storageCount + mandatoryStorageRemaining <
                    target.MaxStorageBuffersPerShaderStage
                : storageCount < target.MaxStorageBuffersPerShaderStage;
            var storageAvailable = target.SupportsStorageBuffers &&
                storageSlotAvailable &&
                GetMinimumBindingSize(parameter) <= target.MaxStorageBufferBindingSize;
            var useUniform = hasUniformFallback &&
                (target.PreferUniformForBoundedReadOnlyBuffers || !storageAvailable);
            if (useUniform) {
                if (uniformCount >= target.MaxUniformBuffersPerShaderStage) {
                    throw new InvalidDataException(
                        $"Resource '{parameter.Name}' exceeds the target uniform-buffer count.");
                }
                parameters[index] = uniformParameter!;
                uniformCount++;
                resources.Add(new SpirvResourceLegalization(
                    parameter.Position,
                    parameter.Kind,
                    SpirvKernelParameterKind.UniformBuffer,
                    "buffer.storage_to_uniform"));
                continue;
            }

            if (!storageAvailable) {
                throw new InvalidDataException(
                    $"Resource '{parameter.Name}' cannot be represented by the target profile.");
            }
            storageCount++;
            resources.Add(new SpirvResourceLegalization(
                parameter.Position,
                parameter.Kind,
                parameter.Kind,
                "buffer.native_storage"));
        }

        return new SpirvLegalizationPlan(
            kernel with { Parameters = parameters },
            resources);
    }

    private static bool TryCreateUniformParameter(
        SpirvKernelParameter parameter,
        SpirvTargetProfile target,
        out SpirvKernelParameter uniformParameter)
    {
        uniformParameter = null!;
        if (parameter.BufferLength is not { } length ||
            parameter.PhysicalLayout is not { } storageLayout) {
            return false;
        }
        var uniformLayout = new ShaderLayoutEngine().Legalize(
            storageLayout.LogicalType,
            ShaderAddressSpace.Uniform);
        var bindingSize = checked((ulong)uniformLayout.ArrayStride * (ulong)length);
        if (bindingSize > target.MaxUniformBufferBindingSize) {
            return false;
        }
        uniformParameter = parameter with {
            Kind = SpirvKernelParameterKind.UniformBuffer,
            PhysicalLayout = uniformLayout
        };
        return true;
    }

    private static ulong GetMinimumBindingSize(SpirvKernelParameter parameter) =>
        (ulong)(parameter.PhysicalLayout?.ArrayStride ??
            SpirvTypeLayout.GetArrayStride(parameter.ScalarType));
}
