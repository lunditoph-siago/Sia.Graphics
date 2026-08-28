using Microsoft.CodeAnalysis.Diagnostics;

namespace Sia.WebGPU.Generators;

public sealed class WgpuGenerationOptions : IEquatable<WgpuGenerationOptions>
{
    private const string k_BuildPropertyPrefix = "build_property.";
    private const string k_DefaultBrowserLibraryName = "__Internal_emscripten";
    private const string k_DefaultClassName = "WgpuUnsafe";
    private const bool k_DefaultGenerateUnsafeBindings = true;
    private const string k_DefaultLibraryName = "wgpu_native";
    private const string k_DefaultNamespace = "Sia.WebGPU";

    public WgpuGenerationOptions(
        string ns = k_DefaultNamespace,
        string className = k_DefaultClassName,
        bool generateUnsafeBindings = k_DefaultGenerateUnsafeBindings,
        string libraryName = k_DefaultLibraryName,
        string browserLibraryName = k_DefaultBrowserLibraryName)
    {
        Namespace = ns;
        ClassName = className;
        GenerateUnsafeBindings = generateUnsafeBindings;
        LibraryName = libraryName;
        BrowserLibraryName = browserLibraryName;
    }

    public string Namespace { get; }

    public string ClassName { get; }

    public bool GenerateUnsafeBindings { get; }

    public string LibraryName { get; }

    public string BrowserLibraryName { get; }

    public bool Equals(WgpuGenerationOptions? other) =>
        other is not null &&
        Namespace == other.Namespace &&
        ClassName == other.ClassName &&
        GenerateUnsafeBindings == other.GenerateUnsafeBindings &&
        LibraryName == other.LibraryName &&
        BrowserLibraryName == other.BrowserLibraryName;

    public override bool Equals(object? obj) =>
        obj is WgpuGenerationOptions other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(
            Namespace,
            ClassName,
            GenerateUnsafeBindings,
            LibraryName,
            BrowserLibraryName);

    internal static WgpuGenerationOptions From(AnalyzerConfigOptions options)
        => new(
            ReadString(options, "sia_webgpu_namespace", "SiaWebGpuNamespace")
                ?? k_DefaultNamespace,
            ReadString(options, "sia_webgpu_class_name", "SiaWebGpuClassName")
                ?? k_DefaultClassName,
            ReadBoolean(
                options,
                "sia_webgpu_generate_unsafe_bindings",
                "SiaWebGpuGenerateUnsafeBindings")
                ?? k_DefaultGenerateUnsafeBindings,
            ReadString(options, "sia_webgpu_library_name", "SiaWebGpuLibraryName")
                ?? k_DefaultLibraryName,
            ReadString(
                options,
                "sia_webgpu_browser_library_name",
                "SiaWebGpuBrowserLibraryName")
                ?? k_DefaultBrowserLibraryName);

    private static string? ReadString(
        AnalyzerConfigOptions options,
        string editorConfigKey,
        string buildPropertyName)
    {
        if (TryReadString(options, editorConfigKey, out var value)) {
            return value;
        }

        return TryReadString(options, k_BuildPropertyPrefix + buildPropertyName, out value)
            ? value
            : null;
    }

    private static bool? ReadBoolean(
        AnalyzerConfigOptions options,
        string editorConfigKey,
        string buildPropertyName)
    {
        var value = ReadString(options, editorConfigKey, buildPropertyName);
        return bool.TryParse(value, out var result) ? result : null;
    }

    private static bool TryReadString(
        AnalyzerConfigOptions options,
        string key,
        out string value)
    {
        if (options.TryGetValue(key, out var rawValue) &&
            !string.IsNullOrWhiteSpace(rawValue)) {
            value = rawValue.Trim();
            return true;
        }

        value = string.Empty;
        return false;
    }
}
