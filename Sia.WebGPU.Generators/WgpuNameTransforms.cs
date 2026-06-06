namespace Sia.WebGPU.Generators;

internal static class WgpuNameTransforms
{
    public static string NormalizeEnumName(string name) =>
        name.EndsWith("Flags", StringComparison.Ordinal) ? name.Substring(0, name.Length - 5) : name;

    public static string NormalizeStructName(string name) =>
        name.Replace("Impl", string.Empty);

    public static string NormalizeEnumValueName(string name, string prefix)
    {
        var valueName = name.Replace(prefix, string.Empty);
        return char.IsNumber(valueName[0]) ? "_" + valueName : valueName;
    }

    public static string ToPascalCase(string name) =>
        string.IsNullOrEmpty(name) ? name : $"{char.ToUpperInvariant(name[0])}{name.Substring(1)}";
}
