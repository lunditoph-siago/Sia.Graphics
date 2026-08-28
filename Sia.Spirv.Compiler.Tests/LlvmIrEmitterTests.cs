using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.LLVM;

namespace Sia.Spirv.Compiler.Tests;

public sealed class LlvmIrEmitterTests
{
    [Fact]
    public void EmitPreservesBooleanBranchConditions()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(FullscreenVertexShaders),
            nameof(FullscreenVertexShaders.Vertex));

        var module = new LlvmIrEmitter().Emit(
            SpirvTestAssembly.Path,
            kernel,
            SpirvKernelAbi.WebGpu);

        Assert.DoesNotContain("undef", module.Text);
        Assert.Contains("icmp eq i32", module.Text);
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
    }

    [Theory]
    [InlineData("Sia.Spirv.Compiler.Tests.MathShaders.IntegerAndBooleanVectors", "<2 x i32>", "<4 x i1>")]
    [InlineData("Sia.Spirv.Compiler.Tests.MathShaders.VectorBitcasts", "bitcast <2 x i32>", "bitcast <3 x i32>")]
    [InlineData("Sia.Spirv.Compiler.Tests.MathShaders.VectorHalfConversion", "uitofp i32", "0x3E70000000000000")]
    [InlineData("Sia.Spirv.Compiler.Tests.MathShaders.VectorSelect", "select <4 x i1>", "<4 x float>")]
    [InlineData("Sia.Spirv.Compiler.Tests.MathShaders.Vectors", "<2 x float>", "<4 x float>")]
    [InlineData("Sia.Spirv.Compiler.Tests.MathShaders.SquareMatrices", "%sia.matrix.float2x2", "%sia.matrix.float4x4")]
    [InlineData("Sia.Spirv.Compiler.Tests.MathShaders.RectangularMatrices", "%sia.matrix.float2x3", "%sia.matrix.float4x3")]
    public void EmitSupportsSiaMathVectorAndMatrixTypes(
        string qualifiedName,
        string firstExpectedType,
        string secondExpectedType)
    {
        var result = SpirvTestAssembly.Analyze();
        var kernel = Assert.Single(
            result.Kernels,
            kernel => kernel.QualifiedName == qualifiedName);

        var module = new LlvmIrEmitter().Emit(
            SpirvTestAssembly.Path,
            kernel,
            SpirvKernelAbi.WebGpu);

        Assert.Contains(firstExpectedType, module.Text);
        Assert.Contains(secondExpectedType, module.Text);
        Assert.DoesNotContain("undef", module.Text);
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
    public void EmitInlinesUserHelperFunctions()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(ComputeShaders),
            nameof(ComputeShaders.UseHelpers));

        var module = new LlvmIrEmitter().Emit(
            SpirvTestAssembly.Path,
            kernel,
            SpirvKernelAbi.WebGpu);

        Assert.Contains("fmul float", module.Text);
        Assert.Contains("fadd float", module.Text);
        Assert.DoesNotContain("Square", module.Text);
        Assert.DoesNotContain("AddBias", module.Text);
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
    public void EmitLowersTextureMipLoadsAndSampling()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(TextureShaders),
            nameof(TextureShaders.SampleAndLoad));

        var module = new LlvmIrEmitter().Emit(
            SpirvTestAssembly.Path,
            kernel,
            SpirvKernelAbi.WebGpu);

        Assert.Contains("@llvm.spv.resource.samplelevel.v4f32.tspirv.Image_f32_1_2_0", module.Text);
        Assert.Contains("@llvm.spv.resource.samplelevel.v4f32.tspirv.Image_f32_1_2_1", module.Text);
        Assert.Contains("i32 1, <2 x i32> zeroinitializer", module.Text);
    }

    [Fact]
    public void EmitCopiesStructBuffersUsingDeclaredLayout()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(ComputeShaders),
            nameof(ComputeShaders.CopyStructs));

        var module = new LlvmIrEmitter().Emit(
            SpirvTestAssembly.Path,
            kernel,
            SpirvKernelAbi.WebGpu);

        Assert.Contains("%sia.struct = type { <4 x float>, i32 }", module.Text);
        Assert.Contains("mul i32", module.Text);
        Assert.Contains(", 8", module.Text);
        Assert.Contains("insertvalue %sia.struct", module.Text);
        Assert.Contains("extractvalue %sia.struct", module.Text);
    }
}
