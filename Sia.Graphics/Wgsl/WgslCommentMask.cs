namespace Sia.Graphics.Wgsl;

internal static class WgslCommentMask
{
    public static string Apply(ReadOnlySpan<char> line, ref int depth)
    {
        var result = line.ToArray();
        var quoted = false;
        for (var index = 0; index < line.Length; index++) {
            var current = line[index];
            var next = index + 1 < line.Length ? line[index + 1] : '\0';
            if (depth > 0) {
                result[index] = ' ';
                if (current == '/' && next == '*') {
                    depth++;
                    result[++index] = ' ';
                }
                else if (current == '*' && next == '/') {
                    depth--;
                    result[++index] = ' ';
                }
                continue;
            }
            if (quoted && current == '\\' && index + 1 < line.Length) {
                index++;
                continue;
            }
            if (current == '"') {
                quoted = !quoted;
            }
            if (quoted) {
                continue;
            }
            if (current == '/' && next == '/') {
                result.AsSpan(index).Fill(' ');
                break;
            }
            if (current == '/' && next == '*') {
                depth++;
                result[index] = ' ';
                result[++index] = ' ';
            }
        }
        return new string(result);
    }
}
