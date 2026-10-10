using System.Collections.Immutable;
using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Compilation;

/// <summary>Immutable shader target data shared by memory/file requests and managed writers.
/// Null feature/stage sets explicitly allow every feature implemented by the selected backend;
/// callers constrain them to device requirements. Policy identifiers compare ordinally and
/// numeric identifiers by value, independently of host set comparers. This value does not describe the host runtime.</summary>
public sealed record SpirvCompilationTarget
{
    public static SpirvCompilationTarget Default { get; } = new();
    public string Environment { get; init; } = "vulkan1.2";
    public uint Version { get; init; } = 0x00010500;
    public SpirvKernelAbi KernelAbi { get; init; } = SpirvKernelAbi.WebGpu;
    public SpirvTargetProfile ResourceLimits { get; init; } = SpirvTargetProfile.Default;
    public ImmutableHashSet<uint>? AllowedCapabilities { get; init; }
    public ImmutableHashSet<string>? AllowedExtensions { get; init; }
    public ImmutableHashSet<string>? AllowedWgslEnables { get; init; }
    public ImmutableHashSet<ShaderStage>? AllowedStages { get; init; }

    internal void Validate(bool offline = false, bool wgsl = false)
    {
        ArgumentNullException.ThrowIfNull(ResourceLimits);
        ResourceLimits.Validate();
        if (!Enum.IsDefined(KernelAbi)) throw new ArgumentOutOfRangeException(nameof(KernelAbi));
        if (Environment is not ("vulkan1.2" or "vulkan1.3" or "universal"))
            throw new ArgumentException("Unsupported shader target environment: " + Environment, nameof(Environment));
        if (Version is not (0x00010300 or 0x00010400 or 0x00010500 or 0x00010600))
            throw new ArgumentOutOfRangeException(nameof(Version), "Managed output supports SPIR-V 1.3 through 1.6.");
        if (Environment == "vulkan1.2" && Version > 0x00010500)
            throw new ArgumentException("Vulkan 1.2 cannot select SPIR-V newer than 1.5.", nameof(Version));
        if (offline && Environment == "universal")
            throw new ArgumentException("The offline LLVM backend requires a Vulkan target environment.", nameof(Environment));
        if (wgsl && KernelAbi != SpirvKernelAbi.WebGpu)
            throw new ArgumentException("WGSL output requires the WebGPU kernel ABI.", nameof(KernelAbi));
        if (AllowedExtensions?.Any(e => string.IsNullOrWhiteSpace(e) || !e.StartsWith("SPV_", StringComparison.Ordinal) || e.Any(char.IsControl)) == true)
            throw new ArgumentException("SPIR-V extension names must be explicit SPV_ identifiers.", nameof(AllowedExtensions));
        if (AllowedStages?.Any(stage => !Enum.IsDefined(stage)) == true)
            throw new ArgumentOutOfRangeException(nameof(AllowedStages));
        if (AllowedWgslEnables?.Any(e => string.IsNullOrWhiteSpace(e) || e.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) == true)
            throw new ArgumentException("WGSL enables must be explicit identifiers.", nameof(AllowedWgslEnables));
    }

    /// <summary>Deterministic identity including every target dimension and distinction between unrestricted/empty sets.</summary>
    public string Identity {
        get {
            Validate();
            var bytes = new ArrayBufferWriter<byte>();
            // Fixed schema and Utf8JsonWriter keep identity independent of culture, reflection,
            // serializer defaults and AOT trimming. Null and empty policy sets remain distinct.
            using (var json = new Utf8JsonWriter(bytes)) {
                json.WriteStartObject(); json.WriteNumber("schema", 1); json.WriteString("environment", Environment);
                json.WriteNumber("version", Version); json.WriteNumber("abi", (int)KernelAbi);
                var limits = ResourceLimits;
                json.WriteBoolean("storage", limits.SupportsStorageBuffers); json.WriteBoolean("preferUniform", limits.PreferUniformForBoundedReadOnlyBuffers);
                json.WriteNumber("storageBindings", limits.MaxStorageBuffersPerShaderStage); json.WriteNumber("vertexStorageBindings", limits.MaxStorageBuffersInVertexStage);
                json.WriteNumber("fragmentStorageBindings", limits.MaxStorageBuffersInFragmentStage); json.WriteNumber("storageSize", limits.MaxStorageBufferBindingSize);
                json.WriteNumber("uniformBindings", limits.MaxUniformBuffersPerShaderStage); json.WriteNumber("uniformSize", limits.MaxUniformBufferBindingSize);
                void Set<T>(string name, IEnumerable<T>? values, Action<T> write) {
                    if (values is null) { json.WriteNull(name); return; }
                    json.WriteStartArray(name); foreach (var value in values) write(value); json.WriteEndArray();
                }
                Set("capabilities", AllowedCapabilities?.Order(), value => json.WriteNumberValue(value));
                Set("extensions", AllowedExtensions?.Order(StringComparer.Ordinal), value => json.WriteStringValue(value));
                Set("wgslEnables", AllowedWgslEnables?.Order(StringComparer.Ordinal), value => json.WriteStringValue(value));
                Set("stages", AllowedStages?.Order(), value => json.WriteNumberValue((int)value));
                json.WriteEndObject();
            }
            return Convert.ToHexString(SHA256.HashData(bytes.WrittenSpan));
        }
    }

    internal static SpirvCompilationTarget FromLegacy(SpirvCompilationOptions options) => new() {
        Environment = options.TargetEnvironment,
        Version = options.TargetEnvironment == "vulkan1.3" ? 0x00010600u : 0x00010500u,
        KernelAbi = options.KernelAbi, ResourceLimits = options.TargetProfile
    };
}
