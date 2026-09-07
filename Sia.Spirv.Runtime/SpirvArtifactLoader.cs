using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sia.Spirv.Runtime;

public static class SpirvArtifactLoader
{
    private static readonly JsonSerializerOptions s_JsonOptions = new() {
        PropertyNameCaseInsensitive = true
    };

    public static SpirvModuleArtifact Load(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        manifestPath = Path.GetFullPath(manifestPath);
        var manifest = JsonSerializer.Deserialize<SpirvArtifactManifest>(
            File.ReadAllText(manifestPath),
            s_JsonOptions) ?? throw new InvalidDataException(
                $"'{manifestPath}' does not contain a SPIR-V artifact manifest.");

        const string suffix = ".spv.json";
        if (!manifestPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) {
            throw new ArgumentException(
                $"SPIR-V manifest paths must end with '{suffix}'.",
                nameof(manifestPath));
        }
        var spirvPath = manifestPath[..^suffix.Length] + ".spv";
        if (manifest.SpirvFile is { } reference) {
            spirvPath = ResolveArtifactPath(Path.GetDirectoryName(manifestPath)!, reference);
        }
        var bytecode = File.ReadAllBytes(spirvPath);
        if (bytecode.Length < 20 || bytecode.Length % sizeof(uint) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytecode) != 0x07230203) {
            throw new InvalidDataException($"'{spirvPath}' is not a valid SPIR-V binary module.");
        }
        if (manifest.SpirvSha256 is { } hash &&
            !string.Equals(hash, Convert.ToHexString(SHA256.HashData(bytecode)), StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidDataException($"'{spirvPath}' does not match the manifest SPIR-V SHA.");
        }

        return new SpirvModuleArtifact(
            spirvPath,
            manifestPath,
            bytecode,
            manifest);
    }

    internal static string ResolveArtifactPath(string directory, string reference)
    {
        directory = Path.GetFullPath(directory);
        var path = Path.GetFullPath(Path.Combine(directory, reference));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        if (string.IsNullOrWhiteSpace(reference) || Path.IsPathRooted(reference) || !path.StartsWith(prefix, comparison)) {
            throw new InvalidDataException("The referenced file must be inside the artifact directory.");
        }
        return path;
    }
}
