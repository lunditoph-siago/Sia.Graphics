using System.Text;
using System.Text.RegularExpressions;

namespace Sia.Graphics.Wgsl;

public static class WgslConditionalCompiler
{
    private sealed class Scope
    {
        public bool HasTakenBranch;
        public bool IsActive;
        public int Line;
    }

    public static string Compile(
        string source,
        IReadOnlyDictionary<string, string>? shaderDefs,
        List<WgslDiagnostic>? diagnostics = null)
    {
        var directives = WgslDirectiveParser.Parse(source);
        var defs = BuildDefMap(directives, shaderDefs);
        var scopes = new Stack<Scope>();
        var defNames = new HashSet<string>(defs.Keys);
        var sb = new StringBuilder(source.Length);

        var lineNo = 0;
        foreach (var rawLine in source.AsSpan().EnumerateLines()) {
            lineNo++;
            var trimmed = rawLine.TrimStart();
            var isDirective = !trimmed.IsEmpty && trimmed[0] == '#';

            if (isDirective) {
                ProcessDirectiveLine(trimmed, scopes, defs, lineNo, diagnostics);
                sb.AppendLine();
            }
            else {
                var active = scopes.Count == 0 || scopes.All(s => s.IsActive);
                if (active) {
                    var line = rawLine.ToString();
                    if (defNames.Count > 0)
                        line = SubstituteDefs(line, defs);
                    sb.AppendLine(line);
                }
                else {
                    sb.AppendLine();
                }
            }
        }

        // Detect unbalanced #ifdef/#endif
        while (scopes.Count > 0) {
            var scope = scopes.Pop();
            diagnostics?.Add(new WgslDiagnostic(
                WgslDiagnosticSeverity.Warning,
                $"Unclosed #ifdef/#ifndef/#if at line {scope.Line}",
                scope.Line, null));
        }

        return sb.ToString();
    }

    // Collapse multiple consecutive spaces into one
    private static readonly Regex s_multiSpace = new(@"\s+", RegexOptions.CultureInvariant);

    private static string NormalizeSpaces(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0) return trimmed;
        var leading = line[..^trimmed.Length];
        return leading + s_multiSpace.Replace(trimmed, " ");
    }

    private static void ProcessDirectiveLine(
        ReadOnlySpan<char> line,
        Stack<Scope> scopes,
        Dictionary<string, string> defs,
        int lineNo,
        List<WgslDiagnostic>? diagnostics)
    {
        var normalized = NormalizeSpaces(line.ToString());

        if (normalized.StartsWith("#ifdef ")) {
            var name = normalized["#ifdef ".Length..].Trim();
            EnterScope(scopes, defs.ContainsKey(name), lineNo);
        }
        else if (normalized.StartsWith("#ifndef ")) {
            var name = normalized["#ifndef ".Length..].Trim();
            EnterScope(scopes, !defs.ContainsKey(name), lineNo);
        }
        else if (normalized.StartsWith("#if ")) {
            var cond = normalized["#if ".Length..].Trim();
            EnterScope(scopes, EvaluateIf(cond, defs), lineNo);
        }
        else if (normalized.StartsWith("#else ifdef ")) {
            var name = normalized["#else ifdef ".Length..].Trim();
            EnterElseBranch(scopes, defs.ContainsKey(name));
        }
        else if (normalized.StartsWith("#else ifndef ")) {
            var name = normalized["#else ifndef ".Length..].Trim();
            EnterElseBranch(scopes, !defs.ContainsKey(name));
        }
        else if (normalized.StartsWith("#else if ")) {
            var cond = normalized["#else if ".Length..].Trim();
            EnterElseBranch(scopes, EvaluateIf(cond, defs));
        }
        else if (normalized == "#else" || normalized.StartsWith("#else ")) {
            EnterElseBranch(scopes, true);
        }
        else if (normalized.StartsWith("#endif")) {
            if (scopes.Count > 0)
                scopes.Pop();
            else {
                diagnostics?.Add(new WgslDiagnostic(
                    WgslDiagnosticSeverity.Warning,
                    "Unmatched #endif",
                    lineNo, null));
            }
        }
    }

    private static void EnterScope(Stack<Scope> scopes, bool conditionTrue, int line)
    {
        var parentActive = scopes.Count == 0 || scopes.All(s => s.IsActive);
        var branchActive = parentActive && conditionTrue;
        scopes.Push(new Scope { IsActive = branchActive, HasTakenBranch = branchActive, Line = line });
    }

    private static void EnterElseBranch(Stack<Scope> scopes, bool conditionTrue)
    {
        if (scopes.Count == 0)
            return;

        var scope = scopes.Peek();
        var parentActive = scopes.Count <= 1 || scopes.Skip(1).All(s => s.IsActive);
        scope.IsActive = parentActive && !scope.HasTakenBranch && conditionTrue;
        if (scope.IsActive)
            scope.HasTakenBranch = true;
    }

    private static bool EvaluateIf(string condition, Dictionary<string, string> defs)
    {
        var eqIdx = condition.IndexOf("==", StringComparison.Ordinal);
        if (eqIdx > 0) {
            var name = condition[..eqIdx].Trim();
            var expected = condition[(eqIdx + 2)..].Trim();
            return defs.TryGetValue(name, out var actual) && actual == expected;
        }

        var neIdx = condition.IndexOf("!=", StringComparison.Ordinal);
        if (neIdx > 0) {
            var name = condition[..neIdx].Trim();
            var expected = condition[(neIdx + 2)..].Trim();
            return !defs.TryGetValue(name, out var actual) || actual != expected;
        }

        return defs.ContainsKey(condition.Trim());
    }

    private static Dictionary<string, string> BuildDefMap(
        WgslDirectiveInfo directives,
        IReadOnlyDictionary<string, string>? shaderDefs)
    {
        var defs = new Dictionary<string, string>();
        if (shaderDefs != null) {
            foreach (var (k, v) in shaderDefs)
                defs[k] = v;
        }
        foreach (var (k, v) in directives.Defines)
            defs[k] = v;
        return defs;
    }

    private static string SubstituteDefs(string line, Dictionary<string, string> defs)
    {
        // Replace #{NAME} patterns
        foreach (var (name, value) in defs) {
            var braced = $"#{{{name}}}";
            var idx = line.IndexOf(braced, StringComparison.Ordinal);
            while (idx >= 0) {
                line = line[..idx] + value + line[(idx + braced.Length)..];
                idx = line.IndexOf(braced, idx + value.Length, StringComparison.Ordinal);
            }
        }

        // Replace #NAME patterns (only when followed by non-identifier char or end of line)
        foreach (var (name, value) in defs) {
            var pattern = $"#{name}";
            var idx = 0;
            while ((idx = line.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0) {
                var after = idx + pattern.Length;
                if (after < line.Length && IsIdentifierChar(line[after])) {
                    idx = after;
                    continue;
                }
                line = line[..idx] + value + line[after..];
                idx += value.Length;
            }
        }

        return line;
    }

    private static bool IsIdentifierChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_';
}
