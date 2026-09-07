using System.Text;
using System.Text.RegularExpressions;

namespace Sia.Graphics.Wgsl;

public static class WgslConditionalCompiler
{
    private sealed class Scope
    {
        public bool ParentActive;
        public bool HasTakenBranch;
        public bool HasElse;
        public bool IsActive;
        public int Line;
    }

    private static readonly Regex s_Substitution = new(@"#\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant);

    public static string Compile(
        string source,
        WgslCompilationContext? context,
        List<WgslDiagnostic>? diagnostics = null)
    {
        var errors = diagnostics ?? [];
        var result = CompileCore(source, context, errors, false, null);
        if (diagnostics is null && errors.Any(d => d.Severity == WgslDiagnosticSeverity.Error)) {
            throw new FormatException(string.Join("\n", errors));
        }
        return result;
    }

    internal static string CompileModule(
        string source,
        WgslCompilationContext? context,
        List<WgslDiagnostic> diagnostics,
        string moduleName) => CompileCore(source, context, diagnostics, true, moduleName);

    private static string CompileCore(
        string source,
        WgslCompilationContext? context,
        List<WgslDiagnostic> diagnostics,
        bool preserveImports,
        string? moduleName)
    {
        ArgumentNullException.ThrowIfNull(source);
        context ??= WgslCompilationContext.Empty;
        var symbols = new Dictionary<string, WgslValue>(context.Symbols, StringComparer.Ordinal);
        var scopes = new Stack<Scope>();
        var output = new StringBuilder(source.Length);
        var lineNumber = 0;
        var commentDepth = 0;

        foreach (var rawLine in source.AsSpan().EnumerateLines()) {
            lineNumber++;
            var line = WgslCommentMask.Apply(rawLine, ref commentDepth);
            var trimmed = line.TrimStart();
            var active = scopes.Count == 0 || scopes.Peek().IsActive;
            try {
                if (trimmed.StartsWith('#') && !trimmed.StartsWith("#{", StringComparison.Ordinal)) {
                    var end = 1;
                    while (end < trimmed.Length && (char.IsAsciiLetter(trimmed[end]) || trimmed[end] == '_')) {
                        end++;
                    }
                    var directive = trimmed[1..end];
                    var argument = trimmed[end..].Trim();
                    switch (directive) {
                        case "if":
                        case "ifdef":
                        case "ifndef": {
                                var scope = new Scope { ParentActive = active, Line = lineNumber };
                                scopes.Push(scope);
                                if (active) {
                                    scope.IsActive = directive == "if"
                                        ? WgslConditionParser.Evaluate(argument, symbols)
                                        : IsDefined(argument, symbols) == (directive == "ifdef");
                                    scope.HasTakenBranch = scope.IsActive;
                                }
                                break;
                            }
                        case "elif": {
                                var scope = GetScope(scopes, directive);
                                if (scope.HasElse) {
                                    throw new FormatException("#elif cannot follow #else.");
                                }
                                scope.IsActive = false;
                                if (scope.ParentActive && !scope.HasTakenBranch) {
                                    scope.IsActive = WgslConditionParser.Evaluate(argument, symbols);
                                    scope.HasTakenBranch = scope.IsActive;
                                }
                                break;
                            }
                        case "else": {
                                var scope = GetScope(scopes, directive);
                                if (argument.Length != 0 || scope.HasElse) {
                                    throw new FormatException("Expected a single #else without arguments; use #elif for a condition.");
                                }
                                scope.HasElse = true;
                                scope.IsActive = scope.ParentActive && !scope.HasTakenBranch;
                                scope.HasTakenBranch |= scope.IsActive;
                                break;
                            }
                        case "endif":
                            if (argument.Length != 0) {
                                throw new FormatException("#endif does not take arguments.");
                            }
                            GetScope(scopes, directive);
                            scopes.Pop();
                            break;
                        case "define":
                            if (active) {
                                Define(argument, context, symbols);
                            }
                            break;
                        case "import":
                        case "define_import_path":
                            if (active && preserveImports) {
                                if (argument.Length == 0) {
                                    throw new FormatException($"#{directive} requires an argument.");
                                }
                                output.Append('#').Append(directive).Append(' ').Append(argument);
                            }
                            break;
                        default:
                            throw new FormatException($"Unknown directive '#{directive}'.");
                    }
                }
                else if (active) {
                    output.Append(Substitute(line, symbols));
                }
            }
            catch (FormatException error) {
                diagnostics.Add(new(WgslDiagnosticSeverity.Error, error.Message, lineNumber, moduleName));
            }
            output.Append('\n');
        }

        foreach (var scope in scopes) {
            diagnostics.Add(new(WgslDiagnosticSeverity.Error, "Unclosed conditional directive.", scope.Line, moduleName));
        }
        if (commentDepth != 0) {
            diagnostics.Add(new(WgslDiagnosticSeverity.Error, "Unclosed block comment.", lineNumber, moduleName));
        }
        return output.ToString();
    }

    private static Scope GetScope(Stack<Scope> scopes, string directive) =>
        scopes.TryPeek(out var scope) ? scope : throw new FormatException($"Unmatched #{directive}.");

    private static bool IsDefined(string name, Dictionary<string, WgslValue> symbols)
    {
        if (!WgslCompilationContext.IsIdentifier(name)) {
            throw new FormatException("Expected a symbol name.");
        }
        return symbols.ContainsKey(name);
    }

    private static void Define(
        string argument,
        WgslCompilationContext context,
        Dictionary<string, WgslValue> symbols)
    {
        var end = 0;
        while (end < argument.Length && !char.IsWhiteSpace(argument[end])) {
            end++;
        }
        var name = argument[..end];
        if (!WgslCompilationContext.IsIdentifier(name)) {
            throw new FormatException("#define requires a valid symbol name.");
        }
        if (WgslCompilationContext.IsReserved(name) || context.Symbols.ContainsKey(name)) {
            throw new FormatException($"Cannot redefine read-only symbol '{name}'.");
        }
        var expression = argument[end..].Trim();
        symbols[name] = expression.Length == 0
            ? WgslValue.Boolean(true)
            : WgslConditionParser.Parse(expression, symbols);
    }

    private static string Substitute(string line, Dictionary<string, WgslValue> symbols)
    {
        var result = s_Substitution.Replace(line, match => {
            var name = match.Groups[1].Value;
            if (!symbols.TryGetValue(name, out var value)) {
                throw new FormatException($"Undefined substitution '{name}'.");
            }
            return value.ToWgsl();
        });
        if (result.Contains('#')) {
            throw new FormatException("Unresolved substitution; use #{NAME} with a declared boolean or integer symbol.");
        }
        return result;
    }
}
