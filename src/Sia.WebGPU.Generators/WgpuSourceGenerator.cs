using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Sia.WebGPU.Generators;

[Generator]
public sealed class WgpuSourceGenerator : IIncrementalGenerator
{
    private static readonly object _nativeDependencyLock = new();
    private static int _nativeDependenciesLoaded;
    private static int _resolverRegistered;

    private const string _embeddedHeaderResourceName = "Sia.WebGPU.Generators.webgpu.h";
    private const int _rtldNow = 2;

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

    private static Assembly? ResolveClangSharpInterop(object sender, ResolveEventArgs args)
    {
        var assemblyName = new AssemblyName(args.Name);
        if (assemblyName.Name != "ClangSharp.Interop") {
            return null;
        }
        using var stream = typeof(WgpuSourceGenerator).Assembly
            .GetManifestResourceStream("Sia.WebGPU.Generators.ClangSharp.Interop.dll");
        if (stream == null) {
            return null;
        }
        var bytes = new byte[stream.Length];
        var offset = 0;
        while (offset < bytes.Length) {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) {
                throw new EndOfStreamException();
            }
            offset += read;
        }
        return Assembly.Load(bytes);
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        if (Interlocked.Exchange(ref _resolverRegistered, 1) == 0) {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveClangSharpInterop;
        }

        var generationTrigger = context.AnalyzerConfigOptionsProvider
            .Select(static (_, _) => true);
        context.RegisterSourceOutput(
            generationTrigger,
            static (context, _) => {
                var headerSource = TryGetEmbeddedHeader();
                if (string.IsNullOrWhiteSpace(headerSource)) {
                    context.ReportDiagnostic(Diagnostic.Create(_missingHeader, Location.None));
                    return;
                }

                try {
                    EnsureNativeDependencyPath();
                    var header = ClangWgpuHeaderParser.Parse(headerSource!);
                    AddGeneratedSources(context, header, new WgpuGenerationOptions());
                }
                catch (Exception ex) {
                    context.ReportDiagnostic(Diagnostic.Create(_generationFailed, Location.None, ex.ToString()));
                }
            });
    }

    private static void AddGeneratedSources(SourceProductionContext context, WgpuHeader header, WgpuGenerationOptions options)
    {
        var renderer = new WgpuCodeRenderer(header, options);

        foreach (var @enum in header.Enums) {
            context.AddSource(
                CreateHintName("Enums", @enum.Name),
                SourceText.From(renderer.RenderEnum(@enum), System.Text.Encoding.UTF8));
        }

        foreach (var handle in header.Handles) {
            context.AddSource(
                CreateHintName("Structs/Handles", handle.Name),
                SourceText.From(renderer.RenderHandle(handle), System.Text.Encoding.UTF8));
        }

        foreach (var @struct in header.Structs) {
            context.AddSource(
                CreateHintName("Structs", @struct.Name),
                SourceText.From(renderer.RenderStruct(@struct), System.Text.Encoding.UTF8));
        }

        if (options.GenerateUnsafeBindings) {
            foreach (var callback in header.Callbacks) {
                context.AddSource(
                    CreateHintName("Callbacks", callback.Name),
                    SourceText.From(renderer.RenderCallback(callback), System.Text.Encoding.UTF8));
            }

            foreach (var function in header.Functions) {
                context.AddSource(
                    CreateHintName("Functions", function.Name),
                    SourceText.From(renderer.RenderFunction(function, options.ClassName), System.Text.Encoding.UTF8));
            }
        }
    }

    private static string CreateHintName(string directory, string name) =>
        $"{directory}/{name}.g.cs";

    private static string? TryGetEmbeddedHeader()
    {
        using var stream = typeof(WgpuSourceGenerator).Assembly
            .GetManifestResourceStream(_embeddedHeaderResourceName);
        if (stream is null) {
            return null;
        }
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private readonly struct NativePlatform(string rid, string extension)
    {
        public string Rid { get; } = rid;
        public string Extension { get; } = extension;

        public string Suffix => Rid + Extension;
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

    private static void EnsureNativeDependencyPath()
    {
        if (_nativeDependenciesLoaded != 0) {
            return;
        }

        lock (_nativeDependencyLock) {
            if (_nativeDependenciesLoaded != 0) {
                return;
            }

            var platform = GetNativePlatform();
            var directory = ExtractNativeDependencies(platform.Suffix, platform.Extension);

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

    private static string ExtractNativeDependencies(string suffix, string ext)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Sia.WebGPU.Generators",
            Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);

        ExtractNativeDependency($"Sia.WebGPU.Generators.Native.libclang.{suffix}", Path.Combine(directory, $"libclang{ext}"));
        ExtractNativeDependency($"Sia.WebGPU.Generators.Native.libClangSharp.{suffix}", Path.Combine(directory, $"libClangSharp{ext}"));

        return directory;
    }

    private static void ExtractNativeDependency(string resourceName, string destinationPath)
    {
        var assembly = typeof(WgpuSourceGenerator).Assembly;
        using var resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded native dependency '{resourceName}' was not found.");

        if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length == resource.Length) {
            return;
        }

        using var file = File.Create(destinationPath);
        resource.CopyTo(file);
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
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetDllDirectory(string lpPathName);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibrary(string lpFileName);
    }

    private static class MacOS
    {
        [DllImport("libSystem.dylib", EntryPoint = "dlopen")]
        public static extern IntPtr Dlopen(string path, int flags);
    }

    private static class Linux
    {
        public static IntPtr Load(string path, int flags)
        {
            try {
                return _dlopenLibdlSo2(path, flags);
            }
            catch (DllNotFoundException) {
                return _dlopenLibdl(path, flags);
            }
        }

        [DllImport("libdl.so.2", EntryPoint = "dlopen")]
        private static extern IntPtr _dlopenLibdlSo2(string path, int flags);

        [DllImport("libdl", EntryPoint = "dlopen")]
        private static extern IntPtr _dlopenLibdl(string path, int flags);
    }
}
