using System.Reflection;
using Sia.Graphics.Wgsl;

namespace Sia.Graphics.Scene;

public static class SceneShaderSource
{
    private const string ResourcePrefix = "Sia.Graphics.Scene.Render.Shaders.";

    private static readonly string[] ModuleResourceNames = [
        ResourcePrefix + "scene_common.wgsl",
        ResourcePrefix + "clustered_forward.wgsl",
        ResourcePrefix + "pbr_lighting.wgsl",
        ResourcePrefix + "shadows.wgsl",
        ResourcePrefix + "ibl.wgsl",
    ];

    public static string LoadDepthPrepass() => Load(ResourcePrefix + "depth_prepass.wgsl");

    public static string LoadForwardPbr() => Load(ResourcePrefix + "forward_pbr.wgsl");

    public static string LoadClusterLightCulling() => Load(ResourcePrefix + "cluster_light_culling.wgsl");

    public static string LoadShadowDepth() => Load(ResourcePrefix + "shadow_depth.wgsl");

    public static string LoadIblPrefilterSpecular() => Load(ResourcePrefix + "ibl_prefilter_specular.wgsl");

    public static string LoadIblBrdfLut() => Load(ResourcePrefix + "ibl_brdf_lut.wgsl");

    private static string Load(string entryResourceName)
    {
        var registry = BuildModuleRegistry();
        var entrySource = ReadResource(entryResourceName);
        var result = WgslPreprocessor.Process(
            entrySource, null, (importPath, _) =>
                registry.TryGetValue(importPath, out var source) ? source : null);
        if (result.HasErrors) {
            throw new InvalidOperationException(
                $"Failed to process '{entryResourceName}': " + string.Join("; ", result.Diagnostics));
        }
        return result.CombinedSource;
    }

    private static Dictionary<string, string> BuildModuleRegistry()
    {
        var registry = new Dictionary<string, string>();
        foreach (var resourceName in ModuleResourceNames) {
            var content = ReadResource(resourceName);
            var directives = WgslDirectiveParser.Parse(content);
            if (directives.ImportPath is { } importPath) {
                registry[importPath] = content;
            }
        }
        return registry;
    }

    private static string ReadResource(string resourceName)
    {
        var assembly = typeof(SceneShaderSource).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded WGSL resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
