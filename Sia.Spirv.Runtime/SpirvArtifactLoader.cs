using System.Buffers.Binary;
using System.Text.Json;

namespace Sia.Spirv.Runtime;

public static class SpirvArtifactLoader
{
    private static readonly JsonSerializerOptions _jsonOptions = new() {
        PropertyNameCaseInsensitive = true
    };

    public static SpirvModuleArtifact Load(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        manifestPath = Path.GetFullPath(manifestPath);
        var manifest = JsonSerializer.Deserialize<SpirvArtifactManifest>(
            File.ReadAllText(manifestPath),
            _jsonOptions) ?? throw new InvalidDataException(
                $"'{manifestPath}' does not contain a SPIR-V artifact manifest.");
        if (manifest.SchemaVersion is not 1 and not 2 and not 3) {
            throw new InvalidDataException(
                $"SPIR-V artifact schema {manifest.SchemaVersion} is not supported.");
        }

        const string suffix = ".spv.json";
        if (!manifestPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) {
            throw new ArgumentException(
                $"SPIR-V manifest paths must end with '{suffix}'.",
                nameof(manifestPath));
        }
        var spirvPath = manifestPath[..^suffix.Length] + ".spv";
        var bytecode = File.ReadAllBytes(spirvPath);
        if (bytecode.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(bytecode) != 0x07230203) {
            throw new InvalidDataException($"'{spirvPath}' is not a valid SPIR-V binary module.");
        }

        return new SpirvModuleArtifact(
            spirvPath,
            manifestPath,
            bytecode,
            manifest);
    }
}
