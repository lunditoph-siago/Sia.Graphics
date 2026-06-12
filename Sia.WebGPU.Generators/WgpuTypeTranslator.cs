namespace Sia.WebGPU.Generators;

internal sealed class WgpuTypeTranslator
{
    private readonly Dictionary<string, WgpuCallback> _callbacks;
    private readonly HashSet<string> _flagTypes;
    private readonly HashSet<string> _handleTypes;

    public WgpuTypeTranslator(WgpuHeader header)
    {
        _callbacks = header.Callbacks.ToDictionary(
            static callback => callback.Name,
            StringComparer.Ordinal);
        _flagTypes = new HashSet<string>(
            header.Enums.Where(static value => value.IsFlags).Select(static value => value.Name),
            StringComparer.Ordinal);
        _handleTypes = new HashSet<string>(
            header.Handles.Select(static handle => handle.Name),
            StringComparer.Ordinal);
    }

    public string Translate(string type)
    {
        var normalizedType = NormalizeCType(type);
        var pointerDepth = CountPointerDepth(normalizedType);
        var baseType = normalizedType.TrimEnd('*');

        if (_callbacks.TryGetValue(baseType, out var callback)) {
            var translatedCallback = TranslateCallback(callback);
            return pointerDepth == 0
                ? translatedCallback
                : translatedCallback + new string('*', pointerDepth);
        }

        var translatedBaseType = TranslateBaseType(baseType);

        if (_handleTypes.Contains(baseType)) {
            pointerDepth++;
        }

        return pointerDepth == 0
            ? translatedBaseType
            : translatedBaseType + new string('*', pointerDepth);
    }

    public static string NormalizeCType(string type) =>
        type.Replace("struct ", string.Empty)
            .Replace(" const", string.Empty)
            .Replace("const ", string.Empty)
            .Replace(" *", "*")
            .Replace("* ", "*")
            .Trim();

    private string TranslateCallback(WgpuCallback callback)
    {
        var signature = callback.Parameters
            .Select(parameter => Translate(parameter.Type))
            .Append(Translate(callback.ReturnType));

        return $"delegate* unmanaged[Cdecl]<{string.Join(", ", signature)}>";
    }

    private string TranslateBaseType(string type) =>
        NormalizeTypeName(type switch {
            "void" => "void",
            "int" => "int",
            "int32_t" => "int",
            "uint8_t" => "byte",
            "uint16_t" => "ushort",
            "uint32_t" => "uint",
            "uint64_t" => "ulong",
            "size_t" => "nuint",
            "float" => "float",
            "double" => "double",
            "WGPUBool" => "uint",
            "WGPUFlags" => "ulong",
            "char" => "byte",
            _ when _flagTypes.Contains(type) => type,
            _ => type,
        });

    private static int CountPointerDepth(string type)
    {
        var count = 0;
        for (var index = type.Length - 1; index >= 0 && type[index] == '*'; index--) {
            count++;
        }

        return count;
    }

    private static string NormalizeTypeName(string name) =>
        name.Replace(" ", string.Empty)
            .Replace("const", string.Empty);
}
