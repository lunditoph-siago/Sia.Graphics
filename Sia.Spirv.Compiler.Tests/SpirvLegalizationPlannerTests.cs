using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Legalization;
using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler.Tests;

public sealed class SpirvLegalizationPlannerTests
{
    [Fact]
    public void ResolveLowersBoundedReadOnlyStructBufferToUniform()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(ComputeShaders),
            nameof(ComputeShaders.CopyBoundedStructs));
        var target = SpirvTargetProfile.Default with {
            PreferUniformForBoundedReadOnlyBuffers = true
        };

        var plan = new SpirvLegalizationPlanner().Resolve(kernel, target);

        var source = plan.Kernel.Parameters[0];
        Assert.Equal(4, source.BufferLength);
        Assert.Equal(SpirvKernelParameterKind.UniformBuffer, source.Kind);
        Assert.Equal(ShaderAddressSpace.Uniform, source.PhysicalLayout!.AddressSpace);
        Assert.Equal(16, source.PhysicalLayout.ArrayStride);
        Assert.Equal(
            SpirvKernelParameterKind.StorageBuffer,
            plan.Kernel.Parameters[1].Kind);
        Assert.Contains("buffer.storage_to_uniform", plan.StrategyIds);
    }

    [Fact]
    public void ResolveDoesNotLowerUnboundedReadOnlyBuffer()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(ComputeShaders),
            nameof(ComputeShaders.CopyPackedStructs));
        var target = SpirvTargetProfile.Default with {
            PreferUniformForBoundedReadOnlyBuffers = true
        };

        var plan = new SpirvLegalizationPlanner().Resolve(kernel, target);

        Assert.Equal(
            SpirvKernelParameterKind.ReadOnlyStorageBuffer,
            plan.Kernel.Parameters[0].Kind);
    }

    [Fact]
    public void ResolvePreservesStorageSlotForWriteOnlyResource()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(ComputeShaders),
            nameof(ComputeShaders.CopyBoundedStructs));
        var target = SpirvTargetProfile.Default with {
            MaxStorageBuffersPerShaderStage = 1
        };

        var plan = new SpirvLegalizationPlanner().Resolve(kernel, target);

        Assert.Equal(
            SpirvKernelParameterKind.UniformBuffer,
            plan.Kernel.Parameters[0].Kind);
        Assert.Equal(
            SpirvKernelParameterKind.StorageBuffer,
            plan.Kernel.Parameters[1].Kind);
    }
}
