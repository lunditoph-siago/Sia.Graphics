using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sia.Spirv.Compiler.Compilation;

public sealed record SpirvVariantConfiguration
{
    public Dictionary<string, SpirvTargetProfile> Targets { get; init; } = new(StringComparer.Ordinal);

    public static SpirvVariantConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try {
            var configuration = JsonSerializer.Deserialize<SpirvVariantConfiguration>(File.ReadAllText(path),
                new JsonSerializerOptions {
                    PropertyNameCaseInsensitive = true,
                    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
                }) ?? throw new InvalidDataException("The SPIR-V variant configuration must contain an object.");
            ValidateTargets(configuration.Targets);
            return configuration;
        }
        catch (JsonException exception) {
            throw new InvalidDataException($"The SPIR-V variant configuration '{path}' is not valid JSON.", exception);
        }
    }

    internal static void ValidateTargets(IReadOnlyDictionary<string, SpirvTargetProfile> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0) {
            throw new ArgumentException("At least one SPIR-V target is required.", nameof(targets));
        }
        foreach (var (name, profile) in targets) {
            if (string.IsNullOrEmpty(name) || !char.IsAsciiLetterLower(name[0]) ||
                name.Any(static character => !char.IsAsciiLetterLower(character) &&
                    !char.IsAsciiDigit(character) && character != '-') || name.Contains("--", StringComparison.Ordinal) ||
                name.EndsWith('-')) {
                throw new ArgumentException($"SPIR-V target '{name}' must be a lowercase kebab name.", nameof(targets));
            }
            if (profile is null) {
                throw new ArgumentException($"SPIR-V target '{name}' must contain a profile.", nameof(targets));
            }
            profile.Validate();
        }
    }
}
