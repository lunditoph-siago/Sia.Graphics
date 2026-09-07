using System.Globalization;

namespace Sia.Graphics.Wgsl;

public readonly record struct WgslValue
{
    public WgslValueKind Kind { get; }
    public bool IsKnown { get; }
    internal ulong Number { get; }
    internal string? Text { get; }

    private WgslValue(WgslValueKind kind, bool isKnown, ulong number, string? text)
    {
        Kind = kind;
        IsKnown = isKnown;
        Number = number;
        Text = text;
    }

    public static WgslValue Boolean(bool value) => new(WgslValueKind.Boolean, true, value ? 1ul : 0ul, null);
    public static WgslValue UInt32(uint value) => new(WgslValueKind.UInt32, true, value, null);
    public static WgslValue UInt64(ulong value) => new(WgslValueKind.UInt64, true, value, null);
    public static WgslValue String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(WgslValueKind.String, true, 0, value);
    }

    public static WgslValue Unknown(WgslValueKind kind)
    {
        if (!Enum.IsDefined(kind)) {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        return new(kind, false, 0, null);
    }

    internal bool IsInteger => Kind is WgslValueKind.UInt32 or WgslValueKind.UInt64;

    public override string ToString() => !IsKnown ? $"Unknown({Kind})" : Kind switch {
        WgslValueKind.Boolean => Number != 0 ? "true" : "false",
        WgslValueKind.UInt32 or WgslValueKind.UInt64 => Number.ToString(CultureInfo.InvariantCulture),
        _ => Text ?? ""
    };

    internal string ToWgsl()
    {
        if (!IsKnown) {
            throw new FormatException("Cannot substitute an unknown value.");
        }
        return Kind switch {
            WgslValueKind.Boolean => Number != 0 ? "true" : "false",
            WgslValueKind.UInt32 or WgslValueKind.UInt64 => Number.ToString(CultureInfo.InvariantCulture),
            _ => throw new FormatException("Strings are only supported in conditions, not WGSL substitutions.")
        };
    }
}
