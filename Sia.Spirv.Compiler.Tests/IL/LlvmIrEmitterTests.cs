using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.LLVM;

namespace Sia.Spirv.Compiler.Tests.IL;

public sealed class LlvmIrEmitterTests
{
    [Fact]
    public void EmitUsesTheShaderStageInTheTargetTriple()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(FullscreenVertexShaders),
            nameof(FullscreenVertexShaders.Vertex));

        var module = new LlvmIrEmitter().Emit(
            SpirvTestAssembly.Path,
            kernel,
            SpirvKernelAbi.WebGpu);

        Assert.Contains(
            "target triple = \"spirv1.5-vulkan1.2-vertex\"",
            module.Text);
        Assert.Contains("external hidden thread_local addrspace(7)", module.Text);
        Assert.Contains("external hidden thread_local addrspace(8)", module.Text);
    }

    [Fact]
    public void EmitMergesEvaluationStackAcrossShortCircuitBlocks()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(ShortCircuitShaders),
            nameof(ShortCircuitShaders.Fragment));

        var module = new LlvmIrEmitter().Emit(
            SpirvTestAssembly.Path,
            kernel,
            SpirvKernelAbi.WebGpu);

        Assert.Contains(" = phi i1 ", module.Text);
        Assert.Contains("fcmp ult float", module.Text);
        Assert.Contains("fcmp ugt float", module.Text);
        Assert.Contains("fcmp ule float", module.Text);
        Assert.DoesNotContain("undef", module.Text);
    }

    [Fact]
    public void EmitPreservesIntegerControlFlowSemantics()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(ControlFlowShaders),
            nameof(ControlFlowShaders.IntegerControlFlow));

        var module = new LlvmIrEmitter().Emit(
            SpirvTestAssembly.Path,
            kernel,
            SpirvKernelAbi.WebGpu);

        Assert.Matches(@"switch i32 [^\r\n]+, label %bb\d+ \[", module.Text);
        Assert.Equal(3, module.Text.Split('\n').Count(static line =>
            line.Contains("%shift.count.", StringComparison.Ordinal) &&
            line.Contains(" = and i32 ", StringComparison.Ordinal) &&
            line.Contains(", 31", StringComparison.Ordinal)));
        Assert.Matches(@"shl i32 [^,\r\n]+, %shift\.count\.\d+", module.Text);
        Assert.Matches(@"lshr i32 [^,\r\n]+, %shift\.count\.\d+", module.Text);
        Assert.Matches(@"ashr i32 [^,\r\n]+, %shift\.count\.\d+", module.Text);
        Assert.Matches(@"xor i32 [^,\r\n]+, -1", module.Text);
        Assert.Contains("store i32 zeroinitializer, ptr %local.", module.Text);
        Assert.Contains("-2147483648", module.Text);
        Assert.Matches(@"shl i32 [^,\r\n]+, 8", module.Text);
        Assert.Single(module.Text.Split('\n'), static line =>
            line.StartsWith("declare target(\"spirv.VulkanBuffer\"", StringComparison.Ordinal) &&
            line.Contains("_12_1t", StringComparison.Ordinal));
    }

    [Fact]
    public void EmitLowersWorkgroupBarrier()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(ComputeShaders),
            nameof(ComputeShaders.Synchronize));

        var module = new LlvmIrEmitter().Emit(
            SpirvTestAssembly.Path,
            kernel,
            SpirvKernelAbi.WebGpu);

        Assert.Contains(
            "call void @llvm.spv.group.memory.barrier.with.group.sync()",
            module.Text);
        Assert.Contains(
            "declare void @llvm.spv.group.memory.barrier.with.group.sync()",
            module.Text);
        Assert.Contains(
            "target(\"spirv.VulkanBuffer\", [1 x <4 x i32>], 2, 0)",
            module.Text);
        Assert.Contains("resource.getpointer.p12", module.Text);
        Assert.Contains("load <4 x i32>, ptr addrspace(12)", module.Text);
        Assert.Contains("extractelement <4 x i32>", module.Text);
    }

    [Fact]
    public void EmitLowersWorkgroupMemoryAndAtomics()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(ComputeShaders),
            nameof(ComputeShaders.AtomicWorkgroup));

        var module = new LlvmIrEmitter().Emit(
            SpirvTestAssembly.Path,
            kernel,
            SpirvKernelAbi.WebGpu);

        Assert.Contains("addrspace(3) global [32 x i32]", module.Text);
        Assert.Contains("syncscope(\"workgroup\") monotonic", module.Text);
        Assert.Contains("syncscope(\"device\") monotonic", module.Text);
        Assert.Contains("atomicrmw add", module.Text);
        Assert.Contains("atomicrmw xchg", module.Text);
    }

    [Fact]
    public void EmitLoadsAndStoresVectorBufferElements()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(ComputeShaders),
            nameof(ComputeShaders.CopyVectors));

        var module = new LlvmIrEmitter().Emit(
            SpirvTestAssembly.Path,
            kernel,
            SpirvKernelAbi.WebGpu);

        Assert.Contains("[0 x <4 x float>]", module.Text);
        Assert.Contains("load <4 x float>, ptr addrspace(11)", module.Text);
        Assert.Contains("store <4 x float>", module.Text);
    }

    [Fact]
    public void EmitLowersTextureMipLoadsAndSampling()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(TextureShaders),
            nameof(TextureShaders.SampleAndLoad));

        var module = new LlvmIrEmitter().Emit(
            SpirvTestAssembly.Path,
            kernel,
            SpirvKernelAbi.WebGpu);

        Assert.Contains(
            "call <4 x float> @llvm.spv.resource.load.level.v4f32.tspirv.Image_f32_1_2_0",
            module.Text);
        Assert.Contains(
            "call <4 x float> @llvm.spv.resource.samplelevel.v4f32.tspirv.Image_f32_1_2_0",
            module.Text);
        Assert.Contains(
            "call <4 x float> @llvm.spv.resource.load.level.v4f32.tspirv.Image_f32_1_2_1",
            module.Text);
        Assert.Contains(
            "call <4 x float> @llvm.spv.resource.samplelevel.v4f32.tspirv.Image_f32_1_2_1",
            module.Text);
        Assert.Matches(@"<2 x i32> [^,\r\n]+, i32 1, <2 x i32> zeroinitializer\)", module.Text);
    }
}
