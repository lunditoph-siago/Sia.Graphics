using Sia.Spirv.Compiler;
using Sia.Spirv.Compiler.Diagnostics;

namespace Sia.Spirv.Tests;

public sealed class SpirvFrontendTests
{
    private static readonly string _assemblyPath =
        typeof(SpirvFrontendTests).Assembly.Location;

    [Fact]
    public void DiscoversKernelAndDecodesDefaultWorkgroupDimensions()
    {
        var result = new SpirvFrontend().Analyze(_assemblyPath);

        var kernel = Assert.Single(result.Kernels, static kernel => kernel.Name == "Saxpy");
        Assert.Equal(64u, kernel.WorkgroupSize.X);
        Assert.Equal(1u, kernel.WorkgroupSize.Y);
        Assert.Equal(1u, kernel.WorkgroupSize.Z);
        Assert.NotEmpty(kernel.ControlFlowGraph.Blocks);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Method == kernel.QualifiedName);
    }

    [Fact]
    public void BuildsControlFlowGraphWithBranchSuccessors()
    {
        var result = new SpirvFrontend().Analyze(_assemblyPath);

        var kernel = Assert.Single(
            result.Kernels,
            static kernel => kernel.Name == "ControlFlow");
        Assert.Contains(
            kernel.ControlFlowGraph.Blocks,
            static block => block.Successors.Count == 2);
    }

    [Fact]
    public void RejectsZeroWorkgroupDimension()
    {
        var result = new SpirvFrontend().Analyze(_assemblyPath);

        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Id == SpirvDiagnosticIds.InvalidWorkgroupSize &&
                diagnostic.Method.EndsWith(".InvalidWorkgroup", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Kernels,
            static kernel => kernel.Name == "InvalidWorkgroup");
    }

    [Fact]
    public void RejectsInstanceKernel()
    {
        var result = new SpirvFrontend().Analyze(_assemblyPath);

        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Id == SpirvDiagnosticIds.InvalidKernelSignature &&
                diagnostic.Method.EndsWith(".InstanceKernel", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsManagedAllocationAtCilOffset()
    {
        var result = new SpirvFrontend().Analyze(_assemblyPath);

        var diagnostic = Assert.Single(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Id == SpirvDiagnosticIds.ManagedHeapAllocation &&
                diagnostic.Method.EndsWith(".AllocatesManagedObject", StringComparison.Ordinal));
        Assert.NotNull(diagnostic.IlOffset);
        Assert.Contains("Managed heap allocation", diagnostic.Message, StringComparison.Ordinal);
    }
}
