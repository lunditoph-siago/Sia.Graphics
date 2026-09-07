namespace Sia.Spirv.Runtime;

public sealed class SpirvArtifactRegistry
{
    private readonly Dictionary<(string SourceMethod, string? TargetName), SpirvModuleArtifact> _artifacts = [];

    public IReadOnlyCollection<SpirvModuleArtifact> Artifacts => _artifacts.Values;

    public void LoadDirectory(string directory, bool recursive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var fileListPath = Path.Combine(directory, "spirv-artifacts.txt");
        var manifests = File.Exists(fileListPath)
            ? File.ReadLines(fileListPath).Where(static path => path.EndsWith(".spv.json", StringComparison.Ordinal))
                .Select(path => SpirvArtifactLoader.ResolveArtifactPath(directory, path))
            : Directory.EnumerateFiles(directory, "*.spv.json", searchOption);
        foreach (var path in manifests) {
            Register(SpirvArtifactLoader.Load(path));
        }
    }

    public void Register(SpirvModuleArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!_artifacts.TryAdd((artifact.Manifest.SourceMethod, artifact.Manifest.TargetName), artifact)) {
            throw new InvalidOperationException(
                $"A SPIR-V artifact for '{artifact.Manifest.SourceMethod}' and target '{artifact.Manifest.TargetName}' is already registered.");
        }
    }

    public SpirvModuleArtifact Get(string sourceMethod)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMethod);
        var variants = GetVariants(sourceMethod);
        return variants.Count switch {
            1 => variants[0],
            0 => throw new KeyNotFoundException($"A SPIR-V artifact for '{sourceMethod}' is not registered."),
            _ => throw new InvalidOperationException($"Multiple SPIR-V variants exist for '{sourceMethod}'; specify a target name.")
        };
    }

    public SpirvModuleArtifact Get(string sourceMethod, string targetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMethod);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        return _artifacts.TryGetValue((sourceMethod, targetName), out var artifact)
            ? artifact
            : throw new KeyNotFoundException(
                $"A SPIR-V artifact for '{sourceMethod}' and target '{targetName}' is not registered.");
    }

    public IReadOnlyList<SpirvModuleArtifact> GetVariants(string sourceMethod)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMethod);
        return _artifacts.Values.Where(artifact => artifact.Manifest.SourceMethod == sourceMethod)
            .OrderBy(static artifact => artifact.Manifest.TargetName, StringComparer.Ordinal).ToArray();
    }
}
