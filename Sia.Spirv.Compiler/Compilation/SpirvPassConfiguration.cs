using System.Text.Json;

namespace Sia.Spirv.Compiler.Compilation;

public sealed record SpirvPassConfiguration
{
    private static readonly JsonSerializerOptions s_JsonOptions = new() {
        PropertyNameCaseInsensitive = true
    };

    public string LlvmPasses { get; init; } = string.Empty;

    public static SpirvPassConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) {
            throw new FileNotFoundException(
                $"The SPIR-V pass configuration was not found at '{fullPath}'.",
                fullPath);
        }

        SpirvPassConfiguration? configuration;
        try {
            configuration = JsonSerializer.Deserialize<SpirvPassConfiguration>(
                File.ReadAllText(fullPath),
                s_JsonOptions);
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                $"The SPIR-V pass configuration '{fullPath}' is not valid JSON.",
                exception);
        }
        if (configuration == null || string.IsNullOrWhiteSpace(configuration.LlvmPasses)) {
            throw new InvalidDataException(
                $"The SPIR-V pass configuration '{fullPath}' must define a non-empty 'llvmPasses' value.");
        }

        return configuration with { LlvmPasses = configuration.LlvmPasses.Trim() };
    }
}
