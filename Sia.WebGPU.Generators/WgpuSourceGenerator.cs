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
    private const string k_EmbeddedHeaderResourceName = "Sia.WebGPU.Generators.webgpu.h";
    private const int k_FileLockRetryCount = 600;
    private const int k_FileLockRetryDelayMilliseconds = 50;
    private const int k_RtldNow = 2;

    private static readonly object s_NativeDependencyLock = new();
    private static int s_NativeDependenciesLoaded;
    private static int s_ResolverRegistered;
    private static IntPtr s_LibClangHandle;
    private static IntPtr s_LibClangSharpHandle;

    private static readonly DiagnosticDescriptor s_MissingHeader = new(
        id: "SIAWGPU001",
        title: "Embedded WebGPU header missing",
        messageFormat: "The embedded webgpu.h resource was not found in the generator assembly",
        category: "Sia.WebGPU.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_GenerationFailed = new(
        id: "SIAWGPU003",
        title: "WebGPU binding generation failed",
        messageFormat: "{0}",
        category: "Sia.WebGPU.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        if (Interlocked.Exchange(ref s_ResolverRegistered, 1) == 0) {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveClangSharpInterop;
        }

        var optionsProvider = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) => WgpuGenerationOptions.From(provider.GlobalOptions));
        context.RegisterSourceOutput(optionsProvider, static (context, options) => {
            var headerSource = TryGetEmbeddedHeader();
            if (string.IsNullOrWhiteSpace(headerSource)) {
                context.ReportDiagnostic(Diagnostic.Create(s_MissingHeader, Location.None));
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
                context.ReportDiagnostic(Diagnostic.Create(s_GenerationFailed, Location.None, ex.ToString()));
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
        var assembly = Assembly.Load(memory.ToArray());
        RegisterNativeDependencyResolver(assembly);
        return assembly;
    }

    private static void RegisterNativeDependencyResolver(Assembly assembly)
    {
        var runtimeAssembly = typeof(Marshal).Assembly;
        var nativeLibraryType = runtimeAssembly.GetType(
            "System.Runtime.InteropServices.NativeLibrary");
        var resolverType = runtimeAssembly.GetType(
            "System.Runtime.InteropServices.DllImportResolver");
        if (nativeLibraryType is null || resolverType is null) {
            return;
        }

        var resolverMethod = typeof(WgpuSourceGenerator).GetMethod(
            nameof(ResolveNativeDependency),
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(ResolveNativeDependency));
        var resolver = Delegate.CreateDelegate(resolverType, resolverMethod);
        var setResolverMethod = nativeLibraryType.GetMethod(
            "SetDllImportResolver",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(Assembly), resolverType],
            modifiers: null)
            ?? throw new MissingMethodException(nativeLibraryType.FullName, "SetDllImportResolver");
        setResolverMethod.Invoke(null, [assembly, resolver]);
    }

    private static IntPtr ResolveNativeDependency(
        string libraryName,
        Assembly _,
        DllImportSearchPath? __) =>
        libraryName switch {
            "libclang" => s_LibClangHandle,
            "libClangSharp" => s_LibClangSharpHandle,
            _ => IntPtr.Zero
        };

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
            .GetManifestResourceStream(k_EmbeddedHeaderResourceName);
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
        if (s_NativeDependenciesLoaded != 0) {
            return;
        }

        lock (s_NativeDependencyLock) {
            if (s_NativeDependenciesLoaded != 0) {
                return;
            }

            ExtractNativeDependencies(platform, directory);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                if (!Windows.SetDllDirectory(directory)) {
                    throw new InvalidOperationException(
                        $"Failed to set DLL directory '{directory}'. Win32Error={Marshal.GetLastWin32Error()}");
                }
            }

            s_LibClangHandle = LoadNativeDependency(
                Path.Combine(directory, "libclang" + platform.Extension));
            s_LibClangSharpHandle = LoadNativeDependency(
                Path.Combine(directory, "libClangSharp" + platform.Extension));

            s_NativeDependenciesLoaded = 1;
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
        for (var attempt = 0; attempt < k_FileLockRetryCount; attempt++) {
            try {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException) when (attempt < k_FileLockRetryCount - 1) {
                Thread.Sleep(k_FileLockRetryDelayMilliseconds);
            }
        }

        throw new IOException($"Timed out waiting for exclusive file lock '{path}'.");
    }

    private static IntPtr LoadNativeDependency(string path)
    {
        var handle =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Windows.LoadLibrary(path) :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? MacOS.Dlopen(path, k_RtldNow) :
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? Linux.Load(path, k_RtldNow) :
            throw new PlatformNotSupportedException(RuntimeInformation.OSDescription);

        if (handle == IntPtr.Zero) {
            throw new InvalidOperationException($"Failed to load native dependency '{path}'.");
        }

        return handle;
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
