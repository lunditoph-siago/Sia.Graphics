using Sia.Spirv;
using Sia.Spirv.Compiler.Legalization;
using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler.Tests.IL;

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
    public void AnalyzePreservesExplicitRasterInputAndReturnAbi()
    {
        var vertex = SpirvTestAssembly.GetKernel(
            typeof(FullscreenVertexShaders),
            nameof(FullscreenVertexShaders.Vertex));
        var vertexInput = Assert.IsType<SpirvStageIoLayout>(
            Assert.Single(vertex.Parameters).StageIoLayout);
        var vertexOutput = Assert.IsType<SpirvStageIoLayout>(vertex.ReturnLayout);

        Assert.Equal(SpirvKernelParameterKind.StageInput, vertex.Parameters[0].Kind);
        Assert.Collection(
            vertexInput.Fields,
            field => Assert.Equal(SpirvStageIoKind.VertexIndex, field.Kind),
            field => Assert.Equal(SpirvStageIoKind.InstanceIndex, field.Kind));
        Assert.Collection(
            vertexOutput.Fields,
            field => Assert.Equal(SpirvStageIoKind.Position, field.Kind),
            field => {
                Assert.Equal(SpirvStageIoKind.Location, field.Kind);
                Assert.Equal(0u, field.Location);
                Assert.Equal(SpirvScalarType.Float32x2, field.Type);
                Assert.Equal(InterpolationMode.Linear, field.Interpolation);
                Assert.Equal(InterpolationSampling.Centroid, field.Sampling);
            },
            field => {
                Assert.Equal(SpirvStageIoKind.Location, field.Kind);
                Assert.Equal(1u, field.Location);
                Assert.Equal(SpirvScalarType.UInt32, field.Type);
                Assert.Equal(InterpolationMode.Flat, field.Interpolation);
            });

        var fragment = SpirvTestAssembly.GetKernel(
            typeof(ExplicitFragmentShaders),
            nameof(ExplicitFragmentShaders.Fragment));
        var fragmentInput = Assert.IsType<SpirvStageIoLayout>(
            Assert.Single(fragment.Parameters).StageIoLayout);
        var fragmentOutput = Assert.IsType<SpirvStageIoLayout>(fragment.ReturnLayout);

        Assert.Contains(fragmentInput.Fields,
            field => field.Kind == SpirvStageIoKind.FragmentPosition);
        Assert.Contains(fragmentInput.Fields,
            field => field.Kind == SpirvStageIoKind.FrontFacing &&
                field.Type == SpirvScalarType.Boolean);
        Assert.Contains(fragmentOutput.Fields,
            field => field.Kind == SpirvStageIoKind.Location);
        Assert.Contains(fragmentOutput.Fields,
            field => field.Kind == SpirvStageIoKind.FragmentDepth &&
                field.Type == SpirvScalarType.Float32);
    }
}
