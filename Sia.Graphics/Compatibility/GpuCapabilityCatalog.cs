using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;
using Sia.Graphics.Wgsl;

namespace Sia.Graphics.Compatibility;

public sealed class GpuCapabilityCatalog
{
    public static GpuCapabilityCatalog Standard { get; } = LoadStandard();

    public IReadOnlyDictionary<string, GpuCapabilityDefinition> Definitions { get; }
    public string Fingerprint { get; }

    private GpuCapabilityCatalog(Dictionary<string, GpuCapabilityDefinition> definitions, string fingerprint)
    {
        Definitions = definitions.ToFrozenDictionary(StringComparer.Ordinal);
        Fingerprint = fingerprint;
    }

    public WgslCompilationContext CreateCompilationContext(
        IReadOnlyDictionary<string, WgslValue>? capabilityValues = null,
        IReadOnlyDictionary<string, WgslValue>? planValues = null)
    {
        var symbols = new Dictionary<string, WgslValue>(StringComparer.Ordinal);
        foreach (var definition in Definitions.Values) {
            if (definition.ShaderSymbol is { } symbol) {
                symbols.Add(symbol, WgslValue.Unknown(definition.ValueKind));
            }
        }
        if (capabilityValues is not null) {
            foreach (var (id, value) in capabilityValues) {
                if (!Definitions.TryGetValue(id, out var definition)) {
                    throw new ArgumentException($"Unknown capability '{id}'.", nameof(capabilityValues));
                }
                if (definition.ShaderSymbol is null) {
                    throw new ArgumentException($"Capability '{id}' is not a shader capability.", nameof(capabilityValues));
                }
                if (definition.ValueKind != value.Kind) {
                    throw new ArgumentException($"Capability '{id}' requires {definition.ValueKind}.", nameof(capabilityValues));
                }
                symbols[definition.ShaderSymbol] = value;
            }
        }
        if (planValues is not null) {
            foreach (var (name, value) in planValues) {
                if (!name.StartsWith("PLAN_", StringComparison.Ordinal)) {
                    throw new ArgumentException("Plan symbols must start with PLAN_.", nameof(planValues));
                }
                symbols.Add(name, value);
            }
        }
        return new WgslCompilationContext(symbols, Fingerprint);
    }

    private static GpuCapabilityCatalog LoadStandard()
    {
        using var stream = typeof(GpuCapabilityCatalog).Assembly.GetManifestResourceStream(
            "Sia.Graphics.Compatibility.Catalog.gpu-capabilities.json")
            ?? throw new InvalidOperationException("The standard GPU capability catalog is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        using var document = JsonDocument.Parse(bytes);
        var definitions = new Dictionary<string, GpuCapabilityDefinition>(StringComparer.Ordinal);
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in document.RootElement.GetProperty("definitions").EnumerateArray()) {
            var definition = new GpuCapabilityDefinition(
                element.GetProperty("id").GetString()!,
                Enum.Parse<GpuCapabilityKind>(element.GetProperty("kind").GetString()!),
                Enum.Parse<WgslValueKind>(element.GetProperty("valueKind").GetString()!),
                element.GetProperty("shaderSymbol").GetString(),
                element.GetProperty("cMember").GetString(),
                Enum.Parse<GpuLimitComparison>(element.GetProperty("comparison").GetString()!));
            definitions.Add(definition.Id, definition);
            if (definition.ShaderSymbol is { } symbol &&
                (!WgslCompilationContext.IsIdentifier(symbol) || !symbols.Add(symbol))) {
                throw new InvalidOperationException($"Invalid or duplicate shader symbol '{symbol}'.");
            }
        }
        return new GpuCapabilityCatalog(definitions, Convert.ToHexString(SHA256.HashData(bytes)));
    }
}
