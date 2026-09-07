using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Sia.Graphics.Wgsl;

public sealed class WgslCompilationContext
{
    public static WgslCompilationContext Empty { get; } = new([]);

    public IReadOnlyDictionary<string, WgslValue> Symbols { get; }
    public string Fingerprint { get; }

    public WgslCompilationContext(IEnumerable<KeyValuePair<string, WgslValue>> symbols, string targetIdentity = "")
    {
        ArgumentNullException.ThrowIfNull(symbols);
        var values = new Dictionary<string, WgslValue>(StringComparer.Ordinal);
        foreach (var (name, value) in symbols) {
            if (!IsIdentifier(name)) {
                throw new ArgumentException($"Invalid shader symbol '{name}'.", nameof(symbols));
            }
            values.Add(name, value);
        }
        Symbols = values.ToFrozenDictionary(StringComparer.Ordinal);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) {
            writer.Write("sia.wgsl.context.v1");
            writer.Write(targetIdentity);
            foreach (var (name, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
                writer.Write(name);
                writer.Write((int)value.Kind);
                writer.Write(value.IsKnown);
                writer.Write(value.Number);
                writer.Write(value.Text ?? "");
            }
        }
        Fingerprint = Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    internal static bool IsIdentifier(string name) =>
        !string.IsNullOrEmpty(name) &&
        (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    internal static WgslCompilationContext FromDefinitions(IReadOnlyDictionary<string, string>? definitions)
    {
        if (definitions is null || definitions.Count == 0) {
            return Empty;
        }
        var symbols = new Dictionary<string, WgslValue>(StringComparer.Ordinal);
        foreach (var (name, text) in definitions) {
            ArgumentNullException.ThrowIfNull(text);
            var value = text.Length == 0 ? WgslValue.Boolean(true)
                : bool.TryParse(text, out var boolean) ? WgslValue.Boolean(boolean)
                : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var integer) ? WgslValue.UInt64(integer)
                : WgslValue.String(text);
            symbols.Add(name, value);
        }
        return new WgslCompilationContext(symbols);
    }

    internal static bool IsReserved(string name) =>
        name.StartsWith("CAP_", StringComparison.Ordinal) ||
        name.StartsWith("WGSL_", StringComparison.Ordinal) ||
        name.StartsWith("LIMIT_", StringComparison.Ordinal) ||
        name.StartsWith("RULE_", StringComparison.Ordinal) ||
        name.StartsWith("PLAN_", StringComparison.Ordinal);
}
