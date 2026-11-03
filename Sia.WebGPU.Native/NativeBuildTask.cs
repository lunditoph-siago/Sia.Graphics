using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

public sealed class NativeBuildTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public ITaskItem[] Sources { get; set; }

    [Required]
    public string RuntimeIdentifier { get; set; }

    [Required]
    public string OutputFile { get; set; }

    [Required]
    public string IntermediateDirectory { get; set; }

    public string Toolchain { get; set; }

    public string Compiler { get; set; }

    public string WindowsSdkRoot { get; set; }

    public ITaskItem[] IncludeDirectories { get; set; } = Array.Empty<ITaskItem>();

    public ITaskItem[] Libraries { get; set; } = Array.Empty<ITaskItem>();

    public ITaskItem[] CompileOptions { get; set; } = Array.Empty<ITaskItem>();

    public ITaskItem[] LinkOptions { get; set; } = Array.Empty<ITaskItem>();

    public string DefinitionFile { get; set; }

    public int MaxParallelism { get; set; } = 2;

    [Output]
    public int CompiledSources { get; set; }

    [Output]
    public bool Linked { get; set; }

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public override bool Execute()
    {
        try {
            var libraryPaths = new List<string>();
            var includes = IncludeDirectories.Select(item => Path.GetFullPath(item.ItemSpec)).ToList();
            var environment = new Dictionary<string, string>();
            var linker = ConfigureToolchain(includes, libraryPaths, environment);

            Directory.CreateDirectory(IntermediateDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(OutputFile)));

            var objects = CompileSources(includes, environment);
            if (objects == null || !Link(objects, linker, libraryPaths, environment)) return false;

            Log.LogMessage(
                MessageImportance.High,
                "Native {0}: compiled {1}/{2}, linked {3}: {4}",
                RuntimeIdentifier, CompiledSources, Sources.Length, Linked, OutputFile);
            return !Log.HasLoggedErrors;
        }
        catch (Exception error) {
            Log.LogErrorFromException(error, true);
            return false;
        }
    }

    private string ConfigureToolchain(
        List<string> includes,
        List<string> libraryPaths,
        IDictionary<string, string> environment)
    {
        if (string.IsNullOrEmpty(Toolchain)) Toolchain = IsWindows ? "msvc" : IsMacOS ? "clang" : "gcc";
        if (Toolchain == "msvc") return ConfigureMsvc(includes, libraryPaths, environment);
        if (Toolchain != "gcc" && Toolchain != "clang")
            throw new InvalidOperationException("Unsupported native toolchain: " + Toolchain);

        Compiler = FindTool(string.IsNullOrEmpty(Compiler) ? (Toolchain == "clang" ? "clang++" : "g++") : Compiler);
        return Compiler;
    }

    private string ConfigureMsvc(
        List<string> includes,
        List<string> libraryPaths,
        IDictionary<string, string> environment)
    {
        if (!IsWindows) throw new InvalidOperationException("MSVC requires a Windows host and its target SDK.");

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var vswhere = Path.Combine(programFiles, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        var install = Run(
            vswhere,
            new[] { "-latest", "-products", "*", "-property", "installationPath" },
            environment).Output.Trim();
        var tools = Directory.GetDirectories(Path.Combine(install, "VC", "Tools", "MSVC"))
            .OrderByDescending(path => Version.Parse(Path.GetFileName(path)))
            .First();
        var arch = RuntimeIdentifier == "win-x86" ? "x86" : RuntimeIdentifier == "win-arm64" ? "arm64" : "x64";
        var host = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var bin = Path.Combine(tools, "bin", "Host" + host, arch);
        if (!Directory.Exists(bin)) throw new InvalidOperationException("Missing MSVC host/target tools: " + bin);

        Compiler = string.IsNullOrEmpty(Compiler) ? Path.Combine(bin, "cl.exe") : FindTool(Compiler);
        includes.Add(Path.Combine(tools, "include"));
        libraryPaths.Add(Path.Combine(tools, "lib", arch));
        AddWindowsSdkPaths(programFiles, arch, includes, libraryPaths);
        environment["VSLANG"] = "1033";
        environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        return Path.Combine(bin, "link.exe");
    }

    private void AddWindowsSdkPaths(
        string programFiles,
        string arch,
        List<string> includes,
        List<string> libraryPaths)
    {
        var sdk = string.IsNullOrEmpty(WindowsSdkRoot)
            ? Path.Combine(programFiles, "Windows Kits", "10")
            : WindowsSdkRoot;
        var version = Directory.GetDirectories(Path.Combine(sdk, "Include"))
            .Select(Path.GetFileName)
            .Where(name => Version.TryParse(name, out _))
            .OrderByDescending(name => Version.Parse(name))
            .First();

        foreach (var name in new[] { "ucrt", "shared", "um", "winrt", "cppwinrt" })
            includes.Add(Path.Combine(sdk, "Include", version, name));
        foreach (var name in new[] { "ucrt", "um" })
            libraryPaths.Add(Path.Combine(sdk, "Lib", version, name, arch));
    }

    private string[] CompileSources(IEnumerable<string> includes, IDictionary<string, string> environment)
    {
        var objects = new string[Sources.Length];
        var failed = 0;
        var compiled = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, MaxParallelism) };

        Parallel.For(0, Sources.Length, options, index => {
            var source = Path.GetFullPath(Sources[index].ItemSpec);
            var extension = Toolchain == "msvc" ? ".obj" : ".o";
            var filename = Path.GetFileName(source) + "." + Hash(source).Substring(0, 12) + extension;
            var obj = Path.Combine(IntermediateDirectory, filename);
            objects[index] = obj;

            var result = CompileSource(source, obj, includes, environment);
            if (!result.Success) Interlocked.Exchange(ref failed, 1);
            if (result.Compiled) Interlocked.Increment(ref compiled);
        });

        CompiledSources = compiled;
        return failed == 0 ? objects : null;
    }

    private (bool Success, bool Compiled) CompileSource(
        string source,
        string obj,
        IEnumerable<string> includes,
        IDictionary<string, string> environment)
    {
        var args = CreateCompileArguments(source, obj, includes);
        var fingerprint = Fingerprint(Compiler, args);
        if (IsCurrent(obj, fingerprint)) return (true, false);

        File.Delete(obj + ".inputs");
        var result = Run(Compiler, args, environment);
        LogOutput(result.Output);

        var dependencies = new List<string> { source };
        if (Toolchain == "msvc") {
            if (result.ExitCode == 0) dependencies.AddRange(ReadMsvcDependencies(obj + ".d"));
        }
        else {
            dependencies.AddRange(ReadMakeDependencies(obj + ".d"));
        }

        if (result.ExitCode != 0) {
            Log.LogError(
                "Native compiler exited {0}: {1}{2}{3}",
                result.ExitCode, source, Environment.NewLine, result.Output);
            return (false, false);
        }

        WriteInputs(obj, fingerprint, dependencies.Distinct().Select(Path.GetFullPath));
        return (true, true);
    }

    private List<string> CreateCompileArguments(string source, string obj, IEnumerable<string> includes)
    {
        var args = new List<string>();
        if (Toolchain == "msvc") {
            args.AddRange(new[] {
                "/nologo", "/c", "/std:c++20", "/MD", "/EHsc", "/utf-8",
                "/sourceDependencies", obj + ".d", "/Fo" + obj, "/Fd" + obj + ".pdb",
            });
            args.AddRange(includes.Select(path => "/I" + path));
        }
        else {
            args.AddRange(new[] { "-c", "-std=c++20", "-fPIC", "-MMD", "-MF", obj + ".d", "-o", obj });
            args.AddRange(includes.Select(path => "-I" + path));
        }

        args.AddRange(CompileOptions.Select(item => item.ItemSpec));
        args.Add(source);
        return args;
    }

    private static string[] ReadMsvcDependencies(string path)
    {
        using (var stream = File.OpenRead(path)) {
            var serializer = new DataContractJsonSerializer(typeof(MsvcDependencies));
            var data = (MsvcDependencies)serializer.ReadObject(stream);
            if (data?.Data?.Includes == null)
                throw new InvalidDataException("Missing MSVC header dependency information.");
            return data.Data.Includes;
        }
    }

    private static IEnumerable<string> ReadMakeDependencies(string path)
    {
        if (!File.Exists(path)) return Array.Empty<string>();

        var content = File.ReadAllText(path).Replace("\\\r\n", "").Replace("\\\n", "");
        var colon = content.IndexOf(':');
        return Regex.Split(content.Substring(colon + 1).Trim(), @"(?<!\\)\s+")
            .Where(value => value.Length > 0)
            .Select(value => value.Replace("\\ ", " ").Replace("\\\\", "\\"));
    }

    private bool Link(
        string[] objects,
        string linker,
        IEnumerable<string> libraryPaths,
        IDictionary<string, string> environment)
    {
        var args = CreateLinkArguments(objects, libraryPaths);
        var inputs = objects.Concat(Libraries.Select(item => Path.GetFullPath(item.ItemSpec))).ToList();
        if (!string.IsNullOrEmpty(DefinitionFile)) inputs.Add(Path.GetFullPath(DefinitionFile));

        var signature = Fingerprint(linker, args);
        if (IsCurrent(OutputFile, signature) &&
            inputs.All(path => File.GetLastWriteTimeUtc(path) <= File.GetLastWriteTimeUtc(OutputFile))) return true;

        File.Delete(OutputFile + ".inputs");
        var result = Run(linker, args, environment);
        LogOutput(result.Output);
        if (result.ExitCode != 0) {
            Log.LogError("Native linker exited {0}:{1}{2}", result.ExitCode, Environment.NewLine, result.Output);
            return false;
        }

        WriteInputs(OutputFile, signature, inputs);
        Linked = true;
        return true;
    }

    private List<string> CreateLinkArguments(string[] objects, IEnumerable<string> libraryPaths)
    {
        var args = new List<string>();
        if (Toolchain == "msvc") {
            args.AddRange(new[] { "/NOLOGO", "/DLL", "/OUT:" + Path.GetFullPath(OutputFile) });
            args.AddRange(libraryPaths.Select(path => "/LIBPATH:" + path));
            if (!string.IsNullOrEmpty(DefinitionFile)) args.Add("/DEF:" + Path.GetFullPath(DefinitionFile));
            args.AddRange(objects);
            args.AddRange(Libraries.Select(item => item.ItemSpec));
            args.AddRange(new[] { "user32.lib", "onecore_apiset.lib", "dxguid.lib" });
        }
        else {
            args.Add(IsMacOS ? "-dynamiclib" : "-shared");
            args.Add(IsMacOS
                ? "-Wl,-install_name,@rpath/" + Path.GetFileName(OutputFile)
                : "-Wl,-soname," + Path.GetFileName(OutputFile));
            args.Add(IsMacOS ? "-Wl,-rpath,@loader_path" : "-Wl,-rpath,$ORIGIN");
            args.AddRange(objects);
            foreach (var library in Libraries.Select(item => item.ItemSpec)) {
                if (IsMacOS) {
                    args.Add(Path.GetExtension(library) == ".a" ? "-Wl,-force_load," + library : library);
                }
                else {
                    args.AddRange(new[] { "-Wl,--whole-archive", library, "-Wl,--no-whole-archive" });
                }
            }

            if (IsMacOS) {
                args.AddRange(new[] {
                    "-framework", "Foundation", "-framework", "Metal", "-framework", "QuartzCore",
                    "-framework", "IOSurface", "-framework", "IOKit", "-framework", "CoreFoundation",
                    "-framework", "CoreGraphics", "-framework", "AppKit",
                });
            }
            else {
                args.AddRange(new[] { "-pthread", "-ldl", "-lrt", "-latomic" });
            }
            args.AddRange(new[] { "-o", Path.GetFullPath(OutputFile) });
        }

        args.AddRange(LinkOptions.Select(item => item.ItemSpec));
        return args;
    }

    private void LogOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return;

        foreach (var line in output.Split('\n')) {
            if (Regex.IsMatch(line, @"\bwarning(?:\s+[A-Z]+[0-9]+)?:")) {
                Log.LogWarning(line.TrimEnd());
            }
            else if (!string.IsNullOrWhiteSpace(line)) {
                Log.LogMessage(MessageImportance.Normal, line.TrimEnd());
            }
        }
    }

    private static bool IsCurrent(string output, string signature)
    {
        if (!File.Exists(output) || !File.Exists(output + ".inputs")) return false;

        var lines = File.ReadAllLines(output + ".inputs");
        return lines.Length > 1 && lines[0] == signature && lines.Skip(1).All(path =>
            File.Exists(path) && File.GetLastWriteTimeUtc(path) <= File.GetLastWriteTimeUtc(output));
    }

    private static string Fingerprint(string tool, IEnumerable<string> args) =>
        Hash(tool + File.GetLastWriteTimeUtc(tool).Ticks + string.Join("\n", args));

    private static void WriteInputs(string output, string signature, IEnumerable<string> inputs) =>
        File.WriteAllLines(output + ".inputs", new[] { signature }.Concat(inputs));

    private static string Hash(string value)
    {
        using (var hash = SHA256.Create()) {
            var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(value));
            return BitConverter.ToString(bytes).Replace("-", "");
        }
    }

    private static string FindTool(string name)
    {
        if (File.Exists(name)) return Path.GetFullPath(name);

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return Path.GetFullPath(path);
        }

        throw new FileNotFoundException("Native compiler not found: " + name);
    }

    private static string Quote(string value)
    {
        var quoted = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value) {
            if (character == '\\') {
                slashes++;
                continue;
            }

            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            quoted.Append(character);
            slashes = 0;
        }

        quoted.Append('\\', slashes * 2);
        return quoted.Append('"').ToString();
    }

    private static (int ExitCode, string Output) Run(
        string tool,
        IEnumerable<string> args,
        IDictionary<string, string> environment)
    {
        var info = new ProcessStartInfo(tool) {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = string.Join(" ", args.Select(Quote)),
        };
        foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;

        using (var process = Process.Start(info)) {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }
    }

    [DataContract]
    private sealed class MsvcDependencies
    {
        [DataMember]
        public MsvcIncludes Data { get; set; }
    }

    [DataContract]
    private sealed class MsvcIncludes
    {
        [DataMember]
        public string[] Includes { get; set; }
    }
}
