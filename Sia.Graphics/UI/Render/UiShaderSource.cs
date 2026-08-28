using System.Reflection;
using Sia.Graphics.Wgsl;

namespace Sia.Graphics.UI;

public static class UiShaderSource
{
    private const string k_ResourceName = "Sia.Graphics.UI.Render.Shaders.ui_node.wgsl";

    public static string Load()
    {
        var assembly = typeof(UiShaderSource).Assembly;
        using var stream = assembly.GetManifestResourceStream(k_ResourceName)
            ?? throw new InvalidOperationException($"Embedded WGSL resource '{k_ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();

        var result = WgslPreprocessor.Process(source, null, static (_, _) => null);
        if (result.HasErrors) {
            throw new InvalidOperationException(
                "Failed to process ui_node.wgsl: " + string.Join("; ", result.Diagnostics));
        }
        return result.CombinedSource;
    }
}
