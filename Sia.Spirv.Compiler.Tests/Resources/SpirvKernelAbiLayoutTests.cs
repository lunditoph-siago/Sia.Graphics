using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Legalization;
using Sia.Spirv.Compiler.Model;
using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Tests.Resources;

public sealed class SpirvKernelAbiLayoutTests
{
    [Theory]
    [InlineData(SpirvKernelAbi.Vulkan)]
    [InlineData(SpirvKernelAbi.WebGpu)]
    public void InterleavedParametersKeepTheirBindingsAndScalarSlots(SpirvKernelAbi abi)
    {
        var kernel = SpirvTestAssembly.GetKernel(typeof(ComputeShaders), nameof(ComputeShaders.CopyWithPackedParameters));
        var limits = SpirvTargetProfile.Default with { PreferUniformForBoundedReadOnlyBuffers = true };
        var (plan, layout) = new SpirvLegalizationPlanner().ResolveWithLayout(kernel, limits, abi);
        var entries = layout.GetParameters(plan.Kernel).ToArray();

        Assert.Equal(SpirvKernelParameterKind.UniformBuffer, entries[1].Parameter.Kind);
        Assert.Equal(new int?[] { null, 0, null, null, null, 1, null, null }, entries.Select(entry => entry.Slot.Binding));
        Assert.Equal(new int?[] { 0, null, 4, null, 8, null, 12, 16 }, entries.Select(entry => entry.Slot.Scalar?.ByteOffset));
        Assert.Equal(1, entries[^1].Slot.Scalar!.Value.VectorIndex);
        Assert.Equal(0, entries[^1].Slot.Scalar!.Value.Lane);

        var target = SpirvCompilationTarget.Default with { KernelAbi = abi, ResourceLimits = limits };
        var module = new SpirvCompiler().CompileModule(new(
            File.ReadAllBytes(SpirvTestAssembly.Path),
            kernel.MetadataToken,
            File.ReadAllBytes(typeof(Gpu).Assembly.Location)) { Target = target });
        Assert.Contains(module.Globals, global => global.Binding == new ResourceBinding(0, 0) && global.Space == AddressSpace.Uniform);
        Assert.Contains(module.Globals, global => global.Binding == new ResourceBinding(0, 1) && global.Space == AddressSpace.Storage);
        Assert.Contains(module.Globals, global => global.Binding is null && global.Space == AddressSpace.Workgroup);

        var parameters = Assert.Single(module.Globals, global => global.Name == "sia_parameters");
        var structure = Assert.IsType<ShaderType.Structure>(parameters.Type);
        if (abi == SpirvKernelAbi.WebGpu) {
            Assert.Equal(new SpirvPackedParameters(2, 2), layout.PackedParameters);
            Assert.Equal(new ResourceBinding(0, 2), parameters.Binding);
            Assert.Equal(AddressSpace.Uniform, parameters.Space);
            Assert.Equal(2u, Assert.IsType<ShaderType.Array>(Assert.Single(structure.Members).Type).Length);
        }
        else {
            Assert.Null(layout.PackedParameters);
            Assert.Null(parameters.Binding);
            Assert.Equal(AddressSpace.Immediate, parameters.Space);
            Assert.Equal(new uint?[] { 0, 4, 8, 12, 16 }, structure.Members.Select(member => member.Offset));
        }
    }
}
