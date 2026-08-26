namespace Sia.Graphics.Wgsl;

internal sealed class WgslFileSystemImportResolver
{
    private readonly string _rootDir;
    private readonly string _entryPath;
    private readonly Dictionary<string, string?> _cache = [];
    private readonly Dictionary<string, string> _pathToModuleName = [];
    private Dictionary<string, string>? _moduleRegistry;

    public WgslFileSystemImportResolver(string rootDir, string entryPath)
    {
        _rootDir = rootDir;
        _entryPath = entryPath;
        _pathToModuleName[entryPath] = "main";
    }

    public string? Resolve(string importPath, string importerName)
    {
        if (_cache.TryGetValue(importPath, out var cached))
            return cached;

        string? source;

        if (importPath.StartsWith('"') && importPath.EndsWith('"')) {
            // Quoted file path — resolve relative to the importer's actual file location
            var relativePath = importPath[1..^1];
            var baseDir = GetImporterDirectory(importerName);

            var resolvedPath = Path.GetFullPath(Path.Combine(baseDir, relativePath));
            source = File.Exists(resolvedPath) ? File.ReadAllText(resolvedPath) : null;

            _cache[importPath] = source;
            if (source != null) {
                _cache[resolvedPath] = source;
                _pathToModuleName[resolvedPath] = resolvedPath;
            }
        }
        else {
            // Module path like "dumb::pbr_types" — find file with matching #define_import_path
            _moduleRegistry ??= BuildModuleRegistry();

            if (_moduleRegistry.TryGetValue(importPath, out var modulePath)) {
                source = File.ReadAllText(modulePath);
                _cache[importPath] = source;
                _cache[modulePath] = source;
            }
            else {
                source = null;
                _cache[importPath] = null;
            }
        }

        return source;
    }

    private string GetImporterDirectory(string importerName)
    {
        // If the importer name is the entry path, use that
        if (importerName == "main")
            return Path.GetDirectoryName(_entryPath) ?? _rootDir;

        // If importer name is a known file path, use its directory
        if (File.Exists(importerName))
            return Path.GetDirectoryName(Path.GetFullPath(importerName)) ?? _rootDir;

        // Look up in module registry for the file path
        _moduleRegistry ??= BuildModuleRegistry();
        foreach (var (name, path) in _moduleRegistry) {
            if (name == importerName)
                return Path.GetDirectoryName(path) ?? _rootDir;
        }

        // Fallback: use root directory
        return _rootDir;
    }

    private Dictionary<string, string> BuildModuleRegistry()
    {
        var registry = new Dictionary<string, string>();
        try {
            foreach (var wgslFile in Directory.EnumerateFiles(_rootDir, "*.wgsl", SearchOption.AllDirectories)) {
                try {
                    var content = File.ReadAllText(wgslFile);
                    var directives = WgslDirectiveParser.Parse(content);
                    if (directives.ImportPath != null)
                        registry[directives.ImportPath] = wgslFile;
                }
                catch {
                    // Skip files that can't be read
                }
            }
        }
        catch {
            // Directory scan failed — empty registry
        }
        return registry;
    }
}
