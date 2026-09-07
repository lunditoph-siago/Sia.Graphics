using System.Globalization;
using System.Text;

namespace Sia.Graphics.Wgsl;

internal sealed class WgslConditionParser
{
    private readonly string _source;
    private readonly IReadOnlyDictionary<string, WgslValue> _symbols;
    private int _position;
    private int _depth;

    private WgslConditionParser(string source, IReadOnlyDictionary<string, WgslValue> symbols)
    {
        _source = source;
        _symbols = symbols;
    }

    public static WgslValue Parse(string source, IReadOnlyDictionary<string, WgslValue> symbols)
    {
        var parser = new WgslConditionParser(source, symbols);
        var result = parser.ParseOr(true);
        parser.SkipWhitespace();
        if (parser._position != source.Length) {
            throw parser.Error("Unexpected token");
        }
        return result;
    }

    public static bool Evaluate(string source, IReadOnlyDictionary<string, WgslValue> symbols)
    {
        var value = Parse(source, symbols);
        RequireBoolean(value);
        RequireKnown(value);
        return value.Number != 0;
    }

    private WgslValue ParseOr(bool evaluate)
    {
        var left = ParseAnd(evaluate);
        while (Take("||")) {
            RequireBoolean(left);
            var leftTrue = evaluate && ReadBoolean(left);
            var right = ParseAnd(evaluate && !leftTrue);
            RequireBoolean(right);
            left = WgslValue.Boolean(evaluate && (leftTrue || ReadBoolean(right)));
        }
        return left;
    }

    private WgslValue ParseAnd(bool evaluate)
    {
        var left = ParseEquality(evaluate);
        while (Take("&&")) {
            RequireBoolean(left);
            var leftTrue = evaluate && ReadBoolean(left);
            var right = ParseEquality(evaluate && leftTrue);
            RequireBoolean(right);
            left = WgslValue.Boolean(evaluate && leftTrue && ReadBoolean(right));
        }
        return left;
    }

    private WgslValue ParseEquality(bool evaluate)
    {
        var left = ParseRelation(evaluate);
        while (true) {
            var op = Take("==") ? "==" : Take("!=") ? "!=" : null;
            if (op is null) {
                return left;
            }
            var right = ParseRelation(evaluate);
            RequireComparable(left, right);
            if (evaluate) {
                RequireKnown(left);
                RequireKnown(right);
            }
            var equal = left.Kind == WgslValueKind.String
                ? string.Equals(left.Text, right.Text, StringComparison.Ordinal)
                : left.Number == right.Number;
            left = WgslValue.Boolean(evaluate && (op == "==" ? equal : !equal));
        }
    }

    private WgslValue ParseRelation(bool evaluate)
    {
        var left = ParseUnary(evaluate);
        while (true) {
            var op = Take("<=") ? "<=" : Take(">=") ? ">=" : Take("<") ? "<" : Take(">") ? ">" : null;
            if (op is null) {
                return left;
            }
            var right = ParseUnary(evaluate);
            if (!left.IsInteger || !right.IsInteger) {
                throw Error("Ordering comparisons require integers");
            }
            if (evaluate) {
                RequireKnown(left);
                RequireKnown(right);
            }
            left = WgslValue.Boolean(evaluate && (op switch {
                "<=" => left.Number <= right.Number,
                ">=" => left.Number >= right.Number,
                "<" => left.Number < right.Number,
                _ => left.Number > right.Number
            }));
        }
    }

    private WgslValue ParseUnary(bool evaluate)
    {
        if (++_depth > 128) {
            throw Error("Condition nesting exceeds 128 levels");
        }
        try {
            return ParsePrimary(evaluate);
        }
        finally {
            _depth--;
        }
    }

    private WgslValue ParsePrimary(bool evaluate)
    {
        if (Take("!")) {
            var value = ParseUnary(evaluate);
            RequireBoolean(value);
            return WgslValue.Boolean(evaluate && !ReadBoolean(value));
        }
        if (Take("(")) {
            var value = ParseOr(evaluate);
            Expect(")");
            return value;
        }
        SkipWhitespace();
        if (_position < _source.Length && _source[_position] == '"') {
            return WgslValue.String(ReadString());
        }
        if (_position < _source.Length && char.IsAsciiDigit(_source[_position])) {
            var start = _position;
            while (_position < _source.Length && char.IsAsciiDigit(_source[_position])) {
                _position++;
            }
            if (!ulong.TryParse(_source.AsSpan(start, _position - start), NumberStyles.None,
                CultureInfo.InvariantCulture, out var number)) {
                throw Error("Integer exceeds UInt64 range");
            }
            return WgslValue.UInt64(number);
        }
        var name = ReadIdentifier();
        if (name is "true" or "false") {
            return WgslValue.Boolean(name == "true");
        }
        if (name is "defined" or "known" or "supported") {
            Expect("(");
            var argument = ReadIdentifier();
            Expect(")");
            if (name == "defined") {
                return WgslValue.Boolean(_symbols.ContainsKey(argument));
            }
            var value = Resolve(argument);
            if (name == "supported") {
                RequireBoolean(value);
                if (!argument.StartsWith("CAP_", StringComparison.Ordinal) &&
                    !argument.StartsWith("WGSL_", StringComparison.Ordinal)) {
                    throw Error("supported() requires a CAP_ or WGSL_ symbol");
                }
            }
            return WgslValue.Boolean(value.IsKnown && (name == "known" || value.Number != 0));
        }
        return Resolve(name);
    }

    private WgslValue Resolve(string name) => _symbols.TryGetValue(name, out var value)
        ? value : throw Error($"Undefined symbol '{name}'");

    private string ReadIdentifier()
    {
        SkipWhitespace();
        var start = _position;
        if (_position < _source.Length && (char.IsAsciiLetter(_source[_position]) || _source[_position] == '_')) {
            _position++;
            while (_position < _source.Length && (char.IsAsciiLetterOrDigit(_source[_position]) || _source[_position] == '_')) {
                _position++;
            }
        }
        if (_position == start) {
            throw Error("Expected identifier or value");
        }
        return _source[start.._position];
    }

    private string ReadString()
    {
        _position++;
        var result = new StringBuilder();
        while (_position < _source.Length) {
            var c = _source[_position++];
            if (c == '"') {
                return result.ToString();
            }
            if (c == '\\') {
                if (_position == _source.Length || _source[_position] is not ('"' or '\\')) {
                    throw Error("Only quote and backslash escapes are supported");
                }
                c = _source[_position++];
            }
            result.Append(c);
        }
        throw Error("Unterminated string");
    }

    private bool Take(string token)
    {
        SkipWhitespace();
        if (!_source.AsSpan(_position).StartsWith(token, StringComparison.Ordinal)) {
            return false;
        }
        _position += token.Length;
        return true;
    }

    private void Expect(string token)
    {
        if (!Take(token)) {
            throw Error($"Expected '{token}'");
        }
    }

    private void SkipWhitespace()
    {
        while (_position < _source.Length && char.IsWhiteSpace(_source[_position])) {
            _position++;
        }
    }

    private FormatException Error(string message) => new($"{message} at expression column {_position + 1}.");

    private static void RequireComparable(WgslValue left, WgslValue right)
    {
        if (left.Kind != right.Kind && !(left.IsInteger && right.IsInteger)) {
            throw new FormatException("Cannot compare values of different types.");
        }
    }

    private static void RequireBoolean(WgslValue value)
    {
        if (value.Kind != WgslValueKind.Boolean) {
            throw new FormatException("A condition must be boolean; compare integer flags explicitly with zero.");
        }
    }

    private static void RequireKnown(WgslValue value)
    {
        if (!value.IsKnown) {
            throw new FormatException("Cannot read an unknown value; use known() or supported() to guard it.");
        }
    }

    private static bool ReadBoolean(WgslValue value)
    {
        RequireKnown(value);
        return value.Number != 0;
    }
}
