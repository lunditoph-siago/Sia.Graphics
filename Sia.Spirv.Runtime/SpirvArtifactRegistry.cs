namespace Sia.Spirv.Runtime;

public sealed class SpirvArtifactRegistry
{
    private readonly Dictionary<string, SpirvModuleArtifact> _artifacts =
        new(StringComparer.Ordinal);

    public IReadOnlyCollection<SpirvModuleArtifact> Artifacts => _artifacts.Values;

    public void LoadDirectory(string directory, bool recursive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var path in Directory.EnumerateFiles(directory, "*.spv.json", searchOption)) {
            Register(SpirvArtifactLoader.Load(path));
        }
    }

    public void Register(SpirvModuleArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!_artifacts.TryAdd(artifact.Manifest.SourceMethod, artifact)) {
            throw new InvalidOperationException(
                $"A SPIR-V artifact for '{artifact.Manifest.SourceMethod}' is already registered.");
        }
    }

    public SpirvModuleArtifact Get(string sourceMethod)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMethod);
        return _artifacts.TryGetValue(sourceMethod, out var artifact)
            ? artifact
            : throw new KeyNotFoundException(
                $"A SPIR-V artifact for '{sourceMethod}' is not registered.");
    }
}
