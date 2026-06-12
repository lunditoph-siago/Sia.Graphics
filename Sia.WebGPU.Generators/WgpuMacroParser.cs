using System.Text.RegularExpressions;

namespace Sia.WebGPU.Generators;

internal static class WgpuMacroParser
{
    private const string _expressionGroup = "expression";
    private const string _fieldNameGroup = "fieldName";
    private const string _macroNameGroup = "macroName";
    private const string _structNameGroup = "structName";
    private const string _valueGroup = "value";

    private static readonly Regex _constantRegex = new(
        @"^#define\s+(?<macroName>WGPU_[A-Z0-9_]+)\s+\((?<expression>[^\r\n]+)\)\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex _uintConstantRegex = new(
        @"^UINT32_C\((?<value>\d+)\)$",
        RegexOptions.CultureInvariant);
    private static readonly Regex _initializerStartRegex = new(
        @"^\s*#define\s+(?<macroName>WGPU_[A-Z0-9_]+_INIT)\s+" +
        @"_wgpu_MAKE_INIT_STRUCT\((?<structName>WGPU[A-Za-z0-9_]+),\s*\{\s*\\\s*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex _nestedInitializerStartRegex = new(
        @"^/\*\.(?<fieldName>\w+)=\*/" +
        @"_wgpu_MAKE_INIT_STRUCT\((?<structName>WGPU[A-Za-z0-9_]+),\s*\{\s*\\\s*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex _initializerFieldRegex = new(
        @"^/\*\.(?<fieldName>\w+)=\*/(?<expression>.+?)\s+_wgpu_COMMA\s*\\\s*$",
        RegexOptions.CultureInvariant);

    public static WgpuConstant[] ParseConstants(string source) =>
        _constantRegex
            .Matches(source)
            .Cast<Match>()
            .Select(static match => TryCreateConstant(
                match.Groups[_macroNameGroup].Value,
                match.Groups[_expressionGroup].Value))
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
            var match = _initializerStartRegex.Match(lines[index]);
            if (!match.Success) {
                index++;
                continue;
            }

            var macroName = match.Groups[_macroNameGroup].Value;
            var structName = match.Groups[_structNameGroup].Value;
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

            var nestedMatch = _nestedInitializerStartRegex.Match(line);
            if (nestedMatch.Success) {
                var fieldName = nestedMatch.Groups[_fieldNameGroup].Value;
                var structName = nestedMatch.Groups[_structNameGroup].Value;
                index++;
                var nestedFields = ParseInitializerFields(lines, ref index);
                fields.Add(new WgpuInitializerField(
                    fieldName,
                    new WgpuNestedInitializerValue(structName, nestedFields)));
                continue;
            }

            var fieldMatch = _initializerFieldRegex.Match(line);
            if (fieldMatch.Success) {
                fields.Add(new WgpuInitializerField(
                    fieldMatch.Groups[_fieldNameGroup].Value,
                    new WgpuScalarInitializerValue(
                        fieldMatch.Groups[_expressionGroup].Value.Trim())));
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

        var uintConstant = _uintConstantRegex.Match(expression);
        if (uintConstant.Success) {
            return CreateConstant(
                nativeName,
                "uint",
                uintConstant.Groups[_valueGroup].Value + "u",
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
