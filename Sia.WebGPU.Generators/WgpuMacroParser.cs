using System.Text.RegularExpressions;

namespace Sia.WebGPU.Generators;

internal static class WgpuMacroParser
{
    private const string k_ExpressionGroup = "expression";
    private const string k_FieldNameGroup = "fieldName";
    private const string k_MacroNameGroup = "macroName";
    private const string k_StructNameGroup = "structName";
    private const string k_ValueGroup = "value";

    private static readonly Regex s_ConstantRegex = new(
        @"^#define\s+(?<macroName>WGPU_[A-Z0-9_]+)\s+\((?<expression>[^\r\n]+)\)\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex s_UIntConstantRegex = new(
        @"^UINT32_C\((?<value>\d+)\)$",
        RegexOptions.CultureInvariant);
    private static readonly Regex s_InitializerStartRegex = new(
        @"^\s*#define\s+(?<macroName>WGPU_[A-Z0-9_]+_INIT)\s+" +
        @"_wgpu_MAKE_INIT_STRUCT\((?<structName>WGPU[A-Za-z0-9_]+),\s*\{\s*\\\s*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex s_NestedInitializerStartRegex = new(
        @"^/\*\.(?<fieldName>\w+)=\*/" +
        @"_wgpu_MAKE_INIT_STRUCT\((?<structName>WGPU[A-Za-z0-9_]+),\s*\{\s*\\\s*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex s_InitializerFieldRegex = new(
        @"^/\*\.(?<fieldName>\w+)=\*/(?<expression>.+?)\s+_wgpu_COMMA\s*\\\s*$",
        RegexOptions.CultureInvariant);

    public static WgpuConstant[] ParseConstants(string source) =>
        s_ConstantRegex
            .Matches(source)
            .Cast<Match>()
            .Select(static match => TryCreateConstant(
                match.Groups[k_MacroNameGroup].Value,
                match.Groups[k_ExpressionGroup].Value))
            .Where(static constant => constant is not null)
            .Select(static constant => constant!)
            .OrderBy(static constant => constant.Name, StringComparer.Ordinal)
            .ToArray();

    public static WgpuStructInitializer[] ParseInitializers(string source)
    {
        var lines = source
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n');
        var initializers = new List<WgpuStructInitializer>();

        for (var index = 0; index < lines.Length;) {
            var match = s_InitializerStartRegex.Match(lines[index]);
            if (!match.Success) {
                index++;
                continue;
            }

            var macroName = match.Groups[k_MacroNameGroup].Value;
            var structName = match.Groups[k_StructNameGroup].Value;
            index++;
            var fields = ParseInitializerFields(lines, ref index);
            initializers.Add(new WgpuStructInitializer(macroName, structName, fields));
        }

        return initializers
            .OrderBy(static initializer => initializer.StructName, StringComparer.Ordinal)
            .ToArray();
    }

    private static WgpuInitializerField[] ParseInitializerFields(string[] lines, ref int index)
    {
        var fields = new List<WgpuInitializerField>();

        while (index < lines.Length) {
            var line = lines[index].Trim();

            if (line.StartsWith("})", StringComparison.Ordinal)) {
                index++;
                return fields.ToArray();
            }

            var nestedMatch = s_NestedInitializerStartRegex.Match(line);
            if (nestedMatch.Success) {
                var fieldName = nestedMatch.Groups[k_FieldNameGroup].Value;
                var structName = nestedMatch.Groups[k_StructNameGroup].Value;
                index++;
                var nestedFields = ParseInitializerFields(lines, ref index);
                fields.Add(new WgpuInitializerField(
                    fieldName,
                    new WgpuNestedInitializerValue(structName, nestedFields)));
                continue;
            }

            var fieldMatch = s_InitializerFieldRegex.Match(line);
            if (fieldMatch.Success) {
                fields.Add(new WgpuInitializerField(
                    fieldMatch.Groups[k_FieldNameGroup].Value,
                    new WgpuScalarInitializerValue(
                        fieldMatch.Groups[k_ExpressionGroup].Value.Trim())));
                index++;
                continue;
            }

            throw new InvalidOperationException(
                $"Unsupported initializer macro syntax at line {index + 1}: {lines[index]}");
        }

        throw new InvalidOperationException("Unterminated WebGPU initializer macro.");
    }

    private static WgpuConstant? TryCreateConstant(string nativeName, string rawExpression)
    {
        var expression = rawExpression.Trim();

        var uintConstant = s_UIntConstantRegex.Match(expression);
        if (uintConstant.Success) {
            return CreateConstant(
                nativeName,
                "uint",
                uintConstant.Groups[k_ValueGroup].Value + "u",
                isCompileTimeConstant: true);
        }

        return expression switch {
            "UINT32_MAX" => CreateConstant(nativeName, "uint", "uint.MaxValue", true),
            "UINT64_MAX" => CreateConstant(nativeName, "ulong", "ulong.MaxValue", true),
            "SIZE_MAX" => CreateConstant(nativeName, "nuint", "nuint.MaxValue", false),
            "NAN" => CreateConstant(nativeName, "float", "float.NaN", true),
            _ => null,
        };
    }

    private static WgpuConstant CreateConstant(
        string nativeName,
        string type,
        string value,
        bool isCompileTimeConstant) =>
        new(
            nativeName,
            WgpuNameTransforms.NormalizeConstantName(nativeName),
            type,
            value,
            isCompileTimeConstant);
}
