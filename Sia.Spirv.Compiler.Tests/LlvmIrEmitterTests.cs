using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Legalization;
using Sia.Spirv.Compiler.LLVM;

namespace Sia.Spirv.Compiler.Tests;

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

    [Theory]
    [InlineData("Sia.Spirv.Compiler.Tests.MathShaders.IntegerAndBooleanVectors", "add <2 x i32>", "mul <3 x i32>")]
    [InlineData("Sia.Spirv.Compiler.Tests.MathShaders.VectorBitcasts", "bitcast <2 x i32>", "bitcast <3 x i32>")]
    [InlineData("Sia.Spirv.Compiler.Tests.MathShaders.VectorHalfConversion", "uitofp i32", "0x3E70000000000000")]
    [InlineData("Sia.Spirv.Compiler.Tests.MathShaders.VectorSelect", "select <4 x i1>", "<4 x float>")]
    [InlineData("Sia.Spirv.Compiler.Tests.MathShaders.Vectors", "call float @llvm.sin.f32", "call float @llvm.pow.f32")]
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

    [Fact]
    public void EmitCopiesStructBuffersUsingPhysicalLayoutType()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(ComputeShaders),
            nameof(ComputeShaders.CopyStructs));

        var module = new LlvmIrEmitter().Emit(
            SpirvTestAssembly.Path,
            kernel,
            SpirvKernelAbi.WebGpu);

        Assert.Contains("%sia.struct = type { <4 x float>, i32 }", module.Text);
        Assert.Contains(
            "%sia.struct.storage = type <{ <4 x float>, i32, [3 x i32] }>",
            module.Text);
        Assert.Contains(
            "getelementptr inbounds %sia.struct.storage, ptr addrspace(11)",
            module.Text);
        Assert.Contains("load <4 x float>, ptr addrspace(11)", module.Text);
        Assert.Contains("store <4 x float>", module.Text);
        Assert.DoesNotContain("mul i32", module.Text);
        Assert.DoesNotContain("bitcast i32", module.Text);
    }

    [Fact]
    public void EmitAddsPaddingOnlyToThePhysicalStruct()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(ComputeShaders),
            nameof(ComputeShaders.CopyAlignedStructs));

        var module = new LlvmIrEmitter().Emit(
            SpirvTestAssembly.Path,
            kernel,
            SpirvKernelAbi.WebGpu);

        Assert.Contains("%sia.struct = type { i32, <3 x float> }", module.Text);
        Assert.Contains(
            "%sia.struct.storage = type <{ i32, [3 x i32], <3 x float>, [1 x i32] }>",
            module.Text);
        Assert.Matches(
            @"getelementptr inbounds %sia\.struct\.storage, ptr addrspace\(11\) [^,]+, i32 0, i32 2",
            module.Text);
    }

    [Fact]
    public void EmitConsumesStorageToUniformLegalizationPlan()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(ComputeShaders),
            nameof(ComputeShaders.CopyBoundedStructs));
        var target = SpirvTargetProfile.Default with {
            PreferUniformForBoundedReadOnlyBuffers = true
        };
        var plan = new SpirvLegalizationPlanner().Resolve(kernel, target);

        var module = new LlvmIrEmitter().Emit(
            SpirvTestAssembly.Path,
            plan.Kernel,
            SpirvKernelAbi.WebGpu);

        Assert.Contains("%sia.struct.uniform = type <{ <3 x float>, i32 }>", module.Text);
        Assert.Contains("%sia.struct.storage = type <{ <3 x float>, i32 }>", module.Text);
        Assert.Contains(
            "target(\"spirv.VulkanBuffer\", [4 x %sia.struct.uniform], 2, 0)",
            module.Text);
        Assert.Contains("resource.getpointer.p12", module.Text);
        Assert.Contains("load <3 x float>, ptr addrspace(12)", module.Text);
        Assert.Contains(
            "getelementptr inbounds %sia.struct.uniform, ptr addrspace(12)",
            module.Text);
        Assert.Contains(
            "getelementptr inbounds %sia.struct.storage, ptr addrspace(11)",
            module.Text);
    }
}
