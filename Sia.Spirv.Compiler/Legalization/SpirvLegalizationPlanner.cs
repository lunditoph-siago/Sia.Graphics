using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler.Legalization;

public sealed class SpirvLegalizationPlanner
{
    public SpirvLegalizationPlan Resolve(
        SpirvKernel kernel,
        SpirvTargetProfile target,
        SpirvKernelAbi kernelAbi = SpirvKernelAbi.Vulkan)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(target);
        target.Validate();

        var parameters = kernel.Parameters.ToArray();
        var storageLimit = target.GetStorageBufferLimit(kernel.Stage);
        var uniformCount = 0;
        foreach (var parameter in parameters) {
            if (parameter.Kind != SpirvKernelParameterKind.UniformBuffer) {
                continue;
            }
            if (GetMinimumBindingSize(parameter) > target.MaxUniformBufferBindingSize) {
                throw new InvalidDataException(
                    $"Resource '{parameter.Name}' exceeds the target uniform-buffer binding size.");
            }
            uniformCount++;
        }
        var pushConstantCount = parameters.Count(static parameter =>
            parameter.Kind == SpirvKernelParameterKind.PushConstant);
        if (kernelAbi == SpirvKernelAbi.WebGpu && pushConstantCount != 0) {
            var parameterSize = ((ulong)pushConstantCount + 3) / 4 * 16;
            if (parameterSize > target.MaxUniformBufferBindingSize) {
                throw new InvalidDataException(
                    "Resource 'sia.parameters' exceeds the target uniform-buffer binding size.");
            }
            uniformCount++;
        }
        if (uniformCount > target.MaxUniformBuffersPerShaderStage) {
            throw new InvalidDataException(
                "The target profile cannot provide the required uniform-buffer bindings.");
        }
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
        if (mandatoryStorageCount > storageLimit) {
            throw new InvalidDataException(
                "The target profile cannot provide the required storage-buffer bindings.");
        }

        var mandatoryUniformRemaining = parameters.Count(parameter =>
            uniformCandidates.ContainsKey(parameter.Position) &&
            (storageLimit == 0 || GetMinimumBindingSize(parameter) > target.MaxStorageBufferBindingSize));
        if (mandatoryUniformRemaining > target.MaxUniformBuffersPerShaderStage - uniformCount) {
            throw new InvalidDataException(
                "The target profile cannot provide the required uniform-buffer fallbacks.");
        }

        var storageCount = 0;
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
            var requiresUniform = hasUniformFallback &&
                (storageLimit == 0 || GetMinimumBindingSize(parameter) > target.MaxStorageBufferBindingSize);
            if (requiresUniform) {
                mandatoryUniformRemaining--;
            }
            var storageSlotAvailable = hasUniformFallback
                ? storageCount + mandatoryStorageRemaining < storageLimit
                : storageCount < storageLimit;
            var storageAvailable = storageSlotAvailable &&
                GetMinimumBindingSize(parameter) <= target.MaxStorageBufferBindingSize;
            var uniformAvailable = hasUniformFallback &&
                uniformCount + mandatoryUniformRemaining < target.MaxUniformBuffersPerShaderStage;
            var useUniform = hasUniformFallback &&
                (target.PreferUniformForBoundedReadOnlyBuffers && uniformAvailable || !storageAvailable);
            if (useUniform) {
                if (!uniformAvailable) {
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
        checked((ulong)(parameter.PhysicalLayout?.ArrayStride ??
            SpirvTypeLayout.GetArrayStride(parameter.ScalarType)) * (ulong)(parameter.BufferLength ?? 1));
}
