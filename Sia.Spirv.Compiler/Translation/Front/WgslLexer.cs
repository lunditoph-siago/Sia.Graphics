using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Sia.Spirv.Compiler.Translation.Front;

internal enum TokenKind { Word, Number, Symbol, TemplateStart, TemplateEnd, End }
internal readonly record struct Token(TokenKind Kind, string Text, SourceSpan Span);

/// <summary>WGSL tokenization and template-list discovery.</summary>
internal static partial class WgslLexer
{
    [GeneratedRegex(@"\G(?:0[xX](?:(?:[0-9a-fA-F]+\.[0-9a-fA-F]*|[0-9a-fA-F]*\.[0-9a-fA-F]+)(?:[pP][+-]?[0-9]+(?:lf|[fh])?)?|[0-9a-fA-F]+[pP][+-]?[0-9]+(?:lf|[fh])?|[0-9a-fA-F]+(?:l[iu]|[iu])?)|(?:[0-9]+\.[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?(?:lf|[fh])?|[0-9]+[eE][+-]?[0-9]+(?:lf|[fh])?|[0-9]+(?:l[iuf]|[iufh])?)", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    public static List<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        var pending = new Stack<(int Index, int Depth)>();
        int position = 0, depth = 0;
        bool possibleTemplate = false;
        void Pop(int level) { while (pending.TryPeek(out var candidate) && candidate.Depth >= level) pending.Pop(); }
        while (position < source.Length)
        {
            char c = source[position];
            if (char.IsWhiteSpace(c)) { position++; continue; }
            if (source.AsSpan(position).StartsWith("//"))
            {
                position += 2;
                while (position < source.Length && source[position] is not ('\r' or '\n' or '\u0085' or '\u2028' or '\u2029')) position++;
                continue;
            }
            if (source.AsSpan(position).StartsWith("/*"))
            {
                int start = position; position += 2; int nesting = 1;
                while (position < source.Length && nesting > 0)
                {
                    if (source.AsSpan(position).StartsWith("/*")) { nesting++; position += 2; }
                    else if (source.AsSpan(position).StartsWith("*/")) { nesting--; position += 2; }
                    else position++;
                }
                if (nesting != 0) throw new ShaderException(DiagnosticStage.WgslParse, "Unterminated block comment.", new(start, position - start));
                continue;
            }
            int begin = position;
            TokenKind kind;
            if (IdentifierStart(source, position, out int width))
            {
                position += width;
                while (position < source.Length && IdentifierContinue(source, position, out width)) position += width;
                kind = TokenKind.Word;
            }
            else if (char.IsAsciiDigit(c) || c == '.' && position + 1 < source.Length && char.IsAsciiDigit(source[position + 1]))
            {
                var match = NumberPattern().Match(source, position);
                if (!match.Success) throw new ShaderException(DiagnosticStage.WgslParse, "Malformed numeric literal.", new(position, 1));
                position += match.Length; kind = TokenKind.Number;
            }
            else if (c == '>' && pending.TryPeek(out var top) && top.Depth == depth)
            {
                position++; kind = TokenKind.TemplateEnd;
            }
            else
            {
                if (!"()[]{}:;,.@+-*/%<>=!&|^~".Contains(c))
                    throw new ShaderException(DiagnosticStage.WgslParse, $"Unexpected character U+{(int)c:X4}.", new(position, 1));
                kind = TokenKind.Symbol; position++;
                if (position + 1 < source.Length && source.AsSpan(begin, 3) is var triple && (triple.SequenceEqual("<<=") || triple.SequenceEqual(">>="))) position = begin + 3;
                else if (position < source.Length && source.Substring(begin, 2) is "->" or "++" or "--" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=" or "==" or "!=" or "<=" or ">=" or "&&" or "||" or "<<" or ">>") position++;
            }
            string text = source[begin..position];
            tokens.Add(new(kind, text, new(begin, position - begin)));
            if (kind == TokenKind.Word) { possibleTemplate = true; continue; }
            if (text == "<" && possibleTemplate) pending.Push((tokens.Count - 1, depth));
            else if (kind == TokenKind.TemplateEnd)
            {
                var candidate = pending.Pop();
                tokens[candidate.Index] = tokens[candidate.Index] with { Kind = TokenKind.TemplateStart };
            }
            else if (text is "(" or "[") depth++;
            else if (text is ")" or "]") { Pop(depth); depth = System.Math.Max(0, depth - 1); }
            else if (text is "=" or ":" or ";" or "{") { pending.Clear(); depth = 0; }
            else if (text is "&&" or "||") Pop(depth);
            possibleTemplate = false;
        }
        tokens.Add(new(TokenKind.End, "", new(source.Length, 0)));
        return tokens;
    }

    private static bool IdentifierStart(string source, int index, out int width)
    {
        if (!Rune.TryGetRuneAt(source, index, out var rune)) { width = 1; return false; }
        width = rune.Utf16SequenceLength;
        return rune.Value == '_' || Rune.GetUnicodeCategory(rune) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber
            || rune.Value is 0x1885 or 0x1886 or 0x2118 or 0x212e or 0x309b or 0x309c;
    }
    private static bool IdentifierContinue(string source, int index, out int width)
    {
        if (IdentifierStart(source, index, out width)) return true;
        if (!Rune.TryGetRuneAt(source, index, out var rune)) return false;
        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation
            || rune.Value is 0xb7 or 0x387 or 0x19da or >= 0x1369 and <= 0x1371;
    }
}
