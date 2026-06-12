namespace Sia.WebGPU.Generators;

internal static class WgpuNameTransforms
{
    private const string _constantPrefix = "WGPU_";

    public static string NormalizeEnumName(string name) =>
        name.EndsWith("Flags", StringComparison.Ordinal)
            ? name.Substring(0, name.Length - 5)
            : name;

    public static string NormalizeStructName(string name) =>
        name.EndsWith("Impl", StringComparison.Ordinal)
            ? name.Substring(0, name.Length - 4)
            : name;

    public static string NormalizeEnumValueName(string name, string prefix)
    {
        var valueName = name.StartsWith(prefix, StringComparison.Ordinal)
            ? name.Substring(prefix.Length)
            : name;

        if (valueName.Length == 0) {
            throw new InvalidOperationException(
                $"Enum value '{name}' becomes empty after removing prefix '{prefix}'.");
        }

        return char.IsDigit(valueName[0]) ? "_" + valueName : valueName;
    }

    public static string ToPascalCase(string name) =>
        string.IsNullOrEmpty(name) ? name : $"{char.ToUpperInvariant(name[0])}{name.Substring(1)}";

    public static string NormalizeConstantName(string name)
    {
        if (!name.StartsWith(_constantPrefix, StringComparison.Ordinal)) {
            throw new ArgumentException($"'{name}' is not a WebGPU constant name.", nameof(name));
        }

        var tokens = name
            .Substring(_constantPrefix.Length)
            .Split(['_'], StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0) {
            throw new ArgumentException($"'{name}' does not contain a constant name.", nameof(name));
        }

        return string.Concat(tokens.Select(NormalizeConstantToken));
    }

    private static string NormalizeConstantToken(string token)
    {
        string normalized;
        if (token == "STRLEN") {
            normalized = "StrLen";
        }
        else if (token.Any(char.IsDigit)) {
            normalized = token;
        }
        else {
            normalized = char.ToUpperInvariant(token[0]) + token.Substring(1).ToLowerInvariant();
        }

        return char.IsDigit(normalized[0]) ? "_" + normalized : normalized;
    }
}
