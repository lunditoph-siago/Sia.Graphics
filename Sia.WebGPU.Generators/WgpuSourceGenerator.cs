using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Sia.WebGPU.Generators;

[Generator]
public sealed class WgpuSourceGenerator : IIncrementalGenerator
{
    private const string _embeddedHeaderResourceName = "Sia.WebGPU.Generators.webgpu.h";
    private const int _fileLockRetryCount = 600;
    private const int _fileLockRetryDelayMilliseconds = 50;
    private const int _rtldNow = 2;

    private static readonly object _nativeDependencyLock = new();
    private static int _nativeDependenciesLoaded;
    private static int _resolverRegistered;

    private static readonly DiagnosticDescriptor _missingHeader = new(
        id: "SIAWGPU001",
        title: "Embedded WebGPU header missing",
        messageFormat: "The embedded webgpu.h resource was not found in the generator assembly",
        category: "Sia.WebGPU.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor _generationFailed = new(
        id: "SIAWGPU003",
        title: "WebGPU binding generation failed",
        messageFormat: "{0}",
        category: "Sia.WebGPU.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        if (Interlocked.Exchange(ref _resolverRegistered, 1) == 0) {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveClangSharpInterop;
        }

        var optionsProvider = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) => WgpuGenerationOptions.From(provider.GlobalOptions));
        context.RegisterSourceOutput(optionsProvider, static (context, options) => {
            var headerSource = TryGetEmbeddedHeader();
            if (string.IsNullOrWhiteSpace(headerSource)) {
                context.ReportDiagnostic(Diagnostic.Create(_missingHeader, Location.None));
                return;
            }

            try {
                var platform = GetNativePlatform();
                var nativeDirectory = GetNativeDependencyDirectory(platform);
                Directory.CreateDirectory(nativeDirectory);

                using var clangLock = AcquireExclusiveFileLock(
                    Path.Combine(nativeDirectory, ".clang.lock"));
                EnsureNativeDependencies(platform, nativeDirectory);
                var header = ClangWgpuHeaderParser.Parse(headerSource!);
                AddGeneratedSources(context, header, options);
            }
            catch (Exception ex) {
                context.ReportDiagnostic(Diagnostic.Create(_generationFailed, Location.None, ex.ToString()));
            }
        });
    }

    private static Assembly? ResolveClangSharpInterop(object? _, ResolveEventArgs args)
    {
        var assemblyName = new AssemblyName(args.Name);
        if (assemblyName.Name != "ClangSharp.Interop") {
            return null;
        }

        using var stream = typeof(WgpuSourceGenerator).Assembly
            .GetManifestResourceStream("Sia.WebGPU.Generators.ClangSharp.Interop.dll");
        if (stream is null) {
            return null;
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Assembly.Load(memory.ToArray());
    }

    private static void AddGeneratedSources(
        SourceProductionContext context,
        WgpuHeader header,
        WgpuGenerationOptions options)
    {
        var renderer = new WgpuCodeRenderer(header, options);

        if (header.Constants.Length != 0) {
            AddSource(
                context,
                "Constants/WgpuConstants.g.cs",
                renderer.RenderConstants());
        }

        foreach (var @enum in header.Enums) {
            AddSource(
                context,
                CreateHintName("Enums", @enum.Name),
                renderer.RenderEnum(@enum));
        }

        foreach (var handle in header.Handles) {
            AddSource(
                context,
                CreateHintName("Structs/Handles", handle.Name),
                renderer.RenderHandle(handle));
        }

        foreach (var @struct in header.Structs) {
            AddSource(
                context,
                CreateHintName("Structs", @struct.Name),
                renderer.RenderStruct(@struct));
        }

        if (options.GenerateUnsafeBindings) {
            foreach (var callback in header.Callbacks) {
                AddSource(
                    context,
                    CreateHintName("Callbacks", callback.Name),
                    renderer.RenderCallback(callback));
            }

            foreach (var function in header.Functions) {
                AddSource(
                    context,
                    CreateHintName("Functions", function.Name),
                    renderer.RenderFunction(function, options.ClassName));
            }
        }
    }

    private static void AddSource(
        SourceProductionContext context,
        string hintName,
        string source) =>
        context.AddSource(hintName, SourceText.From(source, Encoding.UTF8));

    private static string CreateHintName(string directory, string name) =>
        $"{directory}/{name}.g.cs";

    private static string? TryGetEmbeddedHeader()
    {
        using var stream = typeof(WgpuSourceGenerator).Assembly
            .GetManifestResourceStream(_embeddedHeaderResourceName);
        if (stream is null) {
            return null;
        }
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private readonly struct NativePlatform(string rid, string extension)
    {
        public string Rid { get; } = rid;
        public string Extension { get; } = extension;

        public string ResourceSuffix => Rid + Extension;
    }

    private static NativePlatform GetNativePlatform()
    {
        var arch = RuntimeInformation.ProcessArchitecture;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && arch == Architecture.X64) {
            return new NativePlatform("win-x64", ".dll");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && arch == Architecture.X64) {
            return new NativePlatform("linux-x64", ".so");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && arch == Architecture.X64) {
            return new NativePlatform("osx-x64", ".dylib");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && arch == Architecture.Arm64) {
            return new NativePlatform("osx-arm64", ".dylib");
        }

        throw new PlatformNotSupportedException(
            $"Unsupported generator host platform. " +
            $"Supported platforms: win-x64, linux-x64, osx-x64, osx-arm64. " +
            $"Current platform: OS={RuntimeInformation.OSDescription}, Architecture={arch}.");
    }

    private static string GetNativeDependencyDirectory(NativePlatform platform)
    {
        var assembly = typeof(WgpuSourceGenerator).Assembly;
        return Path.Combine(
            Path.GetTempPath(),
            "Sia.WebGPU.Generators",
            assembly.ManifestModule.ModuleVersionId.ToString("N"),
            Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            platform.Rid);
    }

    private static void EnsureNativeDependencies(
        NativePlatform platform,
        string directory)
    {
        if (_nativeDependenciesLoaded != 0) {
            return;
        }

        lock (_nativeDependencyLock) {
            if (_nativeDependenciesLoaded != 0) {
                return;
            }

            ExtractNativeDependencies(platform, directory);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                if (!Windows.SetDllDirectory(directory)) {
                    throw new InvalidOperationException(
                        $"Failed to set DLL directory '{directory}'. Win32Error={Marshal.GetLastWin32Error()}");
                }
            }

            LoadNativeDependency(Path.Combine(directory, "libclang" + platform.Extension));
            LoadNativeDependency(Path.Combine(directory, "libClangSharp" + platform.Extension));

            _nativeDependenciesLoaded = 1;
        }
    }

    private static void ExtractNativeDependencies(
        NativePlatform platform,
        string directory)
    {
        using var extractionLock = AcquireExclusiveFileLock(
            Path.Combine(directory, ".extraction.lock"));
        ExtractNativeDependency(
            $"Sia.WebGPU.Generators.Native.libclang.{platform.ResourceSuffix}",
            Path.Combine(directory, $"libclang{platform.Extension}"));
        ExtractNativeDependency(
            $"Sia.WebGPU.Generators.Native.libClangSharp.{platform.ResourceSuffix}",
            Path.Combine(directory, $"libClangSharp{platform.Extension}"));
    }

    private static void ExtractNativeDependency(string resourceName, string destinationPath)
    {
        var assembly = typeof(WgpuSourceGenerator).Assembly;
        using var resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded native dependency '{resourceName}' was not found.");

        if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length == resource.Length) {
            return;
        }

        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try {
            using (var file = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None)) {
                resource.CopyTo(file);
                file.Flush();
            }

            if (File.Exists(destinationPath)) {
                File.Delete(destinationPath);
            }

            File.Move(temporaryPath, destinationPath);
        }
        finally {
            if (File.Exists(temporaryPath)) {
                File.Delete(temporaryPath);
            }
        }
    }

    private static FileStream AcquireExclusiveFileLock(string path)
    {
        for (var attempt = 0; attempt < _fileLockRetryCount; attempt++) {
            try {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException) when (attempt < _fileLockRetryCount - 1) {
                Thread.Sleep(_fileLockRetryDelayMilliseconds);
            }
        }

        throw new IOException($"Timed out waiting for exclusive file lock '{path}'.");
    }

    private static void LoadNativeDependency(string path)
    {
        var handle =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Windows.LoadLibrary(path) :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? MacOS.Dlopen(path, _rtldNow) :
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? Linux.Load(path, _rtldNow) :
            throw new PlatformNotSupportedException(RuntimeInformation.OSDescription);

        if (handle == IntPtr.Zero) {
            throw new InvalidOperationException($"Failed to load native dependency '{path}'.");
        }
    }

    private static class Windows
    {
        [DllImport(
            "kernel32",
            EntryPoint = "SetDllDirectoryW",
            ExactSpelling = true,
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        public static extern bool SetDllDirectory(string path);

        [DllImport(
            "kernel32",
            EntryPoint = "LoadLibraryW",
            ExactSpelling = true,
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        public static extern IntPtr LoadLibrary(string fileName);
    }

    private static class MacOS
    {
        [DllImport("libSystem.dylib", EntryPoint = "dlopen", ExactSpelling = true)]
        public static extern IntPtr Dlopen(string path, int flags);
    }

    private static class Linux
    {
        public static IntPtr Load(string path, int flags)
        {
            try {
                return DlopenLibdlSo2(path, flags);
            }
            catch (DllNotFoundException) {
                return DlopenLibdl(path, flags);
            }
        }

        [DllImport("libdl.so.2", EntryPoint = "dlopen", ExactSpelling = true)]
        private static extern IntPtr DlopenLibdlSo2(string path, int flags);

        [DllImport("libdl", EntryPoint = "dlopen", ExactSpelling = true)]
        private static extern IntPtr DlopenLibdl(string path, int flags);
    }
}
