using Sia.Spirv;
using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler.Tests;

public sealed class SpirvFrontendTests
{
    [Fact]
    public void AnalyzeDiscoversComputeVertexAndFragmentShaders()
    {
        var result = SpirvTestAssembly.Analyze();

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Kernels, kernel => kernel.Stage == SpirvShaderStage.Compute);
        Assert.Contains(result.Kernels, kernel => kernel.Stage == SpirvShaderStage.Vertex);
        Assert.Contains(result.Kernels, kernel => kernel.Stage == SpirvShaderStage.Fragment);
    }

    [Fact]
    public void AnalyzePreservesComputeWorkgroupAndParameterAbi()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(ComputeShaders),
            nameof(ComputeShaders.Synchronize));

        Assert.Equal(new SpirvWorkgroupSize(8, 4, 2), kernel.WorkgroupSize);
        Assert.Collection(
            kernel.Parameters,
            parameter => {
                Assert.Equal("values", parameter.Name);
                Assert.Equal(SpirvKernelParameterKind.StorageBuffer, parameter.Kind);
                Assert.Equal(SpirvScalarType.Float32, parameter.ScalarType);
            },
            parameter => {
                Assert.Equal("count", parameter.Name);
                Assert.Equal(SpirvKernelParameterKind.PushConstant, parameter.Kind);
                Assert.Equal(SpirvScalarType.UInt32, parameter.ScalarType);
            });
    }

    [Fact]
    public void AnalyzePreservesVectorBufferElementTypes()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(ComputeShaders),
            nameof(ComputeShaders.CopyVectors));

        Assert.Collection(
            kernel.Parameters,
            parameter => {
                Assert.Equal(SpirvKernelParameterKind.ReadOnlyStorageBuffer, parameter.Kind);
                Assert.Equal(SpirvScalarType.Float32x4, parameter.ScalarType);
            },
            parameter => {
                Assert.Equal(SpirvKernelParameterKind.StorageBuffer, parameter.Kind);
                Assert.Equal(SpirvScalarType.Float32x4, parameter.ScalarType);
            });
    }

    [Fact]
    public void AnalyzeTreatsWorkgroupMemoryAsNonBindingMemory()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(ComputeShaders),
            nameof(ComputeShaders.AtomicWorkgroup));
        var parameter = Assert.Single(
            kernel.Parameters,
            parameter => parameter.Kind == SpirvKernelParameterKind.WorkgroupMemory);

        Assert.Equal(SpirvScalarType.UInt32, parameter.ScalarType);
        Assert.False(parameter.IsResource);
    }

    [Fact]
    public void AnalyzeComputesDeterministicStructBufferLayout()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(ComputeShaders),
            nameof(ComputeShaders.CopyStructs));
        var layout = Assert.IsType<SpirvStructLayout>(kernel.Parameters[0].StructLayout);

        Assert.Equal(16, layout.Alignment);
        Assert.Equal(32, layout.Size);
        Assert.Equal(32, layout.ArrayStride);
        Assert.Collection(
            layout.Fields,
            field => {
                Assert.Equal("Position", field.Name);
                Assert.Equal(SpirvScalarType.Float32x4, field.Type);
                Assert.Equal(0, field.Offset);
            },
            field => {
                Assert.Equal("Id", field.Name);
                Assert.Equal(SpirvScalarType.UInt32, field.Type);
                Assert.Equal(16, field.Offset);
            });
    }
}
