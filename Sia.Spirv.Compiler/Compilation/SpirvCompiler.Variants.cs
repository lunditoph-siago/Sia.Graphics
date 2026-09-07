using System.Security.Cryptography;
using System.Text.Json;

namespace Sia.Spirv.Compiler.Compilation;

public sealed partial class SpirvCompiler
{
    public IReadOnlyList<SpirvArtifact> CompileVariants(
        string assemblyPath,
        string outputDirectory,
        IReadOnlyDictionary<string, SpirvTargetProfile> targets,
        SpirvCompilationOptions? options = null)
    {
        SpirvVariantConfiguration.ValidateTargets(targets);
        options ??= new SpirvCompilationOptions();
        var artifacts = new List<SpirvArtifact>();
        foreach (var (name, profile) in targets.OrderBy(static target => target.Key, StringComparer.Ordinal)) {
            artifacts.AddRange(CompileAssemblyCore(
                assemblyPath, outputDirectory, options with { TargetProfile = profile }, name));
        }
        WriteArtifactList(outputDirectory, artifacts);
        return artifacts;
    }

    private static void WriteArtifactList(string outputDirectory, IReadOnlyList<SpirvArtifact> artifacts)
    {
        Directory.CreateDirectory(outputDirectory);
        var files = artifacts.SelectMany(static artifact => new[] {
            artifact.SpirvPath, artifact.ManifestPath, artifact.WgslPath, artifact.LlvmIrPath
        }).Where(static path => path is not null)
            .Select(path => Path.GetRelativePath(outputDirectory, path!).Replace('\\', '/'))
            .Append("spirv-artifacts.txt")
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var fileListPath = Path.Combine(outputDirectory, "spirv-artifacts.txt");
        File.WriteAllText(fileListPath + ".tmp", string.Join('\n', files) + "\n");
        File.Move(fileListPath + ".tmp", fileListPath, true);
    }

    private static string StoreBinary(string path, string outputDirectory, string sha256)
    {
        var directory = Path.Combine(outputDirectory, "objects");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"{sha256}.spv");
        if (File.Exists(destination) && ComputeFileSha256(destination) == sha256) {
            File.Delete(path);
        }
        else {
            File.Move(path, destination, true);
        }
        return destination;
    }

    private static string GetCachedBinaryPath(string manifestPath, string fallbackPath)
    {
        if (!File.Exists(manifestPath)) {
            return fallbackPath;
        }
        try {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (document.RootElement.TryGetProperty("spirvSha256", out var value) &&
                value.ValueKind == JsonValueKind.String &&
                value.GetString() is { Length: 64 } hash && hash.All(char.IsAsciiHexDigit)) {
                return Path.Combine(Path.GetDirectoryName(manifestPath)!, "objects", $"{hash}.spv");
            }
        }
        catch (JsonException) {
        }
        return fallbackPath;
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
