using System.Reflection;
using Sia.Graphics.Wgsl;

namespace Sia.Graphics.UI;

public static class UiShaderSource
{
    private const string k_ResourceName = "Sia.Graphics.UI.Render.Shaders.ui_node.wgsl";
    private const string k_CompatibilityVertexResourceName =
        "Sia.Graphics.UI.Render.Shaders.ui_node_compat_vertex.wgsl";

    public static string Load() => LoadResource(k_ResourceName);

    public static string LoadCompatibilityVertex() =>
        LoadResource(k_CompatibilityVertexResourceName);

    private static string LoadResource(string resourceName)
    {
        var assembly = typeof(UiShaderSource).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded WGSL resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();

        var result = WgslPreprocessor.Process(source, null, static (_, _) => null);
        if (result.HasErrors) {
            throw new InvalidOperationException(
                $"Failed to process '{resourceName}': " + string.Join("; ", result.Diagnostics));
        }
        return result.CombinedSource;
    }
}
