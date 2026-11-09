using Sia.Spirv.Compiler.Compilation;

namespace Sia.Spirv.Compiler.Tests.Support;

// xUnit runs this class's tests serially. Both artifact sets are immutable after
// creation and each configuration compiles once; disposal owns all output files.
public sealed class OfflineCompilationFixture : IDisposable
{
    private readonly TestDirectory directory = new();
    private readonly Lazy<IReadOnlyList<SpirvArtifact>> artifacts;
    private readonly Lazy<IReadOnlyList<SpirvArtifact>> uniformArtifacts;

    public OfflineCompilationFixture()
    {
        artifacts = new(() => Compile(uniform: false));
        uniformArtifacts = new(() => Compile(uniform: true));
    }

    internal IReadOnlyList<SpirvArtifact> Artifacts => artifacts.Value;
    internal IReadOnlyList<SpirvArtifact> UniformArtifacts => uniformArtifacts.Value;

    private IReadOnlyList<SpirvArtifact> Compile(bool uniform)
    {
        var target = uniform
            ? SpirvCompilationTarget.Default with { ResourceLimits = SpirvTargetProfile.Default with {
                PreferUniformForBoundedReadOnlyBuffers = true
            } }
            : SpirvCompilationTarget.Default with { KernelAbi = SpirvKernelAbi.WebGpu };
        return new SpirvCompiler().CompileAssembly(new(SpirvTestAssembly.Path, directory.File(uniform ? "uniform" : "default")) {
            ToolchainDirectory = SpirvTestToolchain.Directory,
            Target = target,
            EmitWgsl = true,
            EmitLlvmIr = true,
            OptimizationLevel = 3
        });
    }

    public void Dispose() => directory.Dispose();
}
