using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sia.Spirv.Compiler.Legalization;
using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler.Compilation;

internal sealed class ShaderArtifactStore(SpirvFileCompilationRequest request, string? targetName)
{
    public ShaderArtifactPaths GetPaths(SpirvKernel kernel)
    {
        var fileName = SanitizeFileName(kernel.QualifiedName);
        if (targetName is not null) {
            fileName = $"{targetName}--{fileName}";
        }
        var directory = request.OutputDirectory;
        return new(
            Path.Combine(directory, $"{fileName}.ll"),
            Path.Combine(directory, $"{fileName}.raw.ll"),
            Path.Combine(directory, $"{fileName}.spv"),
            Path.Combine(directory, $"{fileName}.wgsl"),
            Path.Combine(directory, $"{fileName}.spv.json"));
    }

    public SpirvArtifact? ReadCached(SpirvKernel kernel, ShaderArtifactPaths paths, string sourceHash)
    {
        var binary = targetName is null ? paths.Spirv : GetCachedBinaryPath(paths.Manifest, paths.Spirv);
        return IsCacheHit(paths.Manifest, binary, paths.Wgsl, paths.Llvm, sourceHash, request.EmitWgsl, request.EmitLlvmIr)
            ? CreateArtifact(kernel, paths, binary, cacheHit: true)
            : null;
    }

    public SpirvArtifact Publish(
        SpirvLegalizationPlan plan,
        ShaderArtifactPaths paths,
        SpirvManifestToolchain versions,
        string sourceHash)
    {
        if (!request.EmitLlvmIr) {
            File.Delete(paths.Llvm);
        }

        var binary = paths.Spirv;
        var sha256 = ComputeFileSha256(binary);
        if (targetName is not null) {
            binary = StoreBinary(binary, request.OutputDirectory, sha256);
        }
        var manifest = ShaderManifestBuilder.Create(
            plan.Kernel,
            request,
            plan,
            versions,
            binary,
            sourceHash,
            targetName,
            Path.GetRelativePath(request.OutputDirectory, binary).Replace('\\', '/'),
            sha256);
        manifest = manifest with { CompilationTargetSha256 = request.Target.Identity };
        File.WriteAllText(paths.Manifest,
            JsonSerializer.Serialize(manifest, ShaderManifestBuilder.JsonOptions) + Environment.NewLine,
            new UTF8Encoding(false));
        return CreateArtifact(plan.Kernel, paths, binary, cacheHit: false);
    }

    private SpirvArtifact CreateArtifact(SpirvKernel kernel, ShaderArtifactPaths paths, string binary, bool cacheHit)
        => new(kernel, binary, request.EmitWgsl ? paths.Wgsl : null, paths.Manifest,
            request.EmitLlvmIr ? paths.Llvm : null, cacheHit);

    private static bool IsCacheHit(
        string manifestPath,
        string spirvPath,
        string wgslPath,
        string llvmPath,
        string sourceHash,
        bool emitWgsl,
        bool emitLlvmIr)
    {
        if (!File.Exists(manifestPath) || !File.Exists(spirvPath) ||
            emitWgsl && !File.Exists(wgslPath) ||
            emitLlvmIr && !File.Exists(llvmPath)) {
            return false;
        }
        try {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            return root.GetProperty("sourceHash").GetString() == sourceHash &&
                (!root.TryGetProperty("spirvSha256", out var hash) ||
                    hash.ValueKind == JsonValueKind.String && hash.GetString() == ComputeFileSha256(spirvPath));
        }
        catch (JsonException) {
            return false;
        }
    }

    public static void WriteArtifactList(string outputDirectory, IReadOnlyList<SpirvArtifact> artifacts)
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

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}
