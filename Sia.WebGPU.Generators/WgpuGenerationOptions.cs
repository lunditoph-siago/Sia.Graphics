using Microsoft.CodeAnalysis.Diagnostics;

namespace Sia.WebGPU.Generators;

public sealed class WgpuGenerationOptions : IEquatable<WgpuGenerationOptions>
{
    private const string _buildPropertyPrefix = "build_property.";
    private const string _defaultBrowserLibraryName = "__Internal_emscripten";
    private const string _defaultClassName = "WgpuUnsafe";
    private const bool _defaultGenerateUnsafeBindings = true;
    private const string _defaultLibraryName = "wgpu_native";
    private const string _defaultNamespace = "Sia.WebGPU";

    public WgpuGenerationOptions(
        string ns = _defaultNamespace,
        string className = _defaultClassName,
        bool generateUnsafeBindings = _defaultGenerateUnsafeBindings,
        string libraryName = _defaultLibraryName,
        string browserLibraryName = _defaultBrowserLibraryName)
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
                ?? _defaultNamespace,
            ReadString(options, "sia_webgpu_class_name", "SiaWebGpuClassName")
                ?? _defaultClassName,
            ReadBoolean(
                options,
                "sia_webgpu_generate_unsafe_bindings",
                "SiaWebGpuGenerateUnsafeBindings")
                ?? _defaultGenerateUnsafeBindings,
            ReadString(options, "sia_webgpu_library_name", "SiaWebGpuLibraryName")
                ?? _defaultLibraryName,
            ReadString(
                options,
                "sia_webgpu_browser_library_name",
                "SiaWebGpuBrowserLibraryName")
                ?? _defaultBrowserLibraryName);

    private static string? ReadString(
        AnalyzerConfigOptions options,
        string editorConfigKey,
        string buildPropertyName)
    {
        if (TryReadString(options, editorConfigKey, out var value)) {
            return value;
        }

        return TryReadString(options, _buildPropertyPrefix + buildPropertyName, out value)
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
