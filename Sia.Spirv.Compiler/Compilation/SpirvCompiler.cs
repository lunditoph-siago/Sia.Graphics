namespace Sia.Spirv.Compiler.Compilation;

public sealed partial class SpirvCompiler
{
    public IReadOnlyList<SpirvArtifact> CompileAssembly(SpirvFileCompilationRequest request)
    {
        var artifacts = OfflineShaderCompiler.Compile(request, null, LlvmShaderBackend.Open);
        ShaderArtifactStore.WriteArtifactList(request.OutputDirectory, artifacts);
        return artifacts;
    }

    public IReadOnlyList<SpirvArtifact> CompileVariants(
        SpirvFileCompilationRequest request,
        IReadOnlyDictionary<string, SpirvTargetProfile> targets)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        request.Target.Validate(offline: true, wgsl: request.EmitWgsl);
        SpirvVariantConfiguration.ValidateTargets(targets);
        var artifacts = new List<SpirvArtifact>();
        foreach (var (name, profile) in targets.OrderBy(static target => target.Key, StringComparer.Ordinal)) {
            artifacts.AddRange(OfflineShaderCompiler.Compile(
                request with { Target = request.Target with { ResourceLimits = profile } }, name, LlvmShaderBackend.Open));
        }
        ShaderArtifactStore.WriteArtifactList(request.OutputDirectory, artifacts);
        return artifacts;
    }
}
