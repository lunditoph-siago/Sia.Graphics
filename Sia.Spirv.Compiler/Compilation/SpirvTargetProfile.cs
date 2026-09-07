using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sia.Spirv.Compiler.Compilation;

public sealed record SpirvTargetProfile
{
    public static SpirvTargetProfile Default { get; } = new();

    public bool SupportsStorageBuffers { get; init; } = true;

    public bool PreferUniformForBoundedReadOnlyBuffers { get; init; }

    public int MaxStorageBuffersPerShaderStage { get; init; } = int.MaxValue;

    public int MaxStorageBuffersInVertexStage { get; init; } = int.MaxValue;

    public int MaxStorageBuffersInFragmentStage { get; init; } = int.MaxValue;

    public ulong MaxStorageBufferBindingSize { get; init; } = ulong.MaxValue;

    public int MaxUniformBuffersPerShaderStage { get; init; } = int.MaxValue;

    public ulong MaxUniformBufferBindingSize { get; init; } = ulong.MaxValue;

    public static SpirvTargetProfile Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try {
            var profile = JsonSerializer.Deserialize<SpirvTargetProfile>(File.ReadAllText(path),
                new JsonSerializerOptions {
                    PropertyNameCaseInsensitive = true,
                    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
                }) ?? throw new InvalidDataException("The SPIR-V target profile must contain an object.");
            profile.Validate();
            return profile;
        }
        catch (JsonException exception) {
            throw new InvalidDataException($"The SPIR-V target profile '{path}' is not valid JSON.", exception);
        }
    }

    internal void Validate()
    {
        if (MaxStorageBuffersPerShaderStage < 0 || MaxStorageBuffersInVertexStage < 0 ||
            MaxStorageBuffersInFragmentStage < 0 || MaxUniformBuffersPerShaderStage < 0) {
            throw new InvalidDataException("SPIR-V target buffer limits must be non-negative.");
        }
    }

    public int GetStorageBufferLimit(SpirvShaderStage stage)
    {
        var stageLimit = stage switch {
            SpirvShaderStage.Vertex => MaxStorageBuffersInVertexStage,
            SpirvShaderStage.Fragment => MaxStorageBuffersInFragmentStage,
            SpirvShaderStage.Compute => MaxStorageBuffersPerShaderStage,
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };
        return SupportsStorageBuffers ? Math.Min(MaxStorageBuffersPerShaderStage, stageLimit) : 0;
    }
}
