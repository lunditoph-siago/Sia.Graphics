using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

public sealed class NativeBuildTask : Microsoft.Build.Utilities.Task
{
    [Required] public ITaskItem[] Sources { get; set; }
    [Required] public string RuntimeIdentifier { get; set; }
    [Required] public string OutputFile { get; set; }
    [Required] public string IntermediateDirectory { get; set; }
    public string Toolchain { get; set; }
    public string Compiler { get; set; }
    public string WindowsSdkRoot { get; set; }
    public ITaskItem[] IncludeDirectories { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] Libraries { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] CompileOptions { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] LinkOptions { get; set; } = Array.Empty<ITaskItem>();
    public string DefinitionFile { get; set; }
    public int MaxParallelism { get; set; } = 2;
    [Output] public int CompiledSources { get; set; }
    [Output] public bool Linked { get; set; }

    public override bool Execute()
    {
        try {
            Toolchain = string.IsNullOrEmpty(Toolchain) ? (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "msvc" : System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX) ? "clang" : "gcc") : Toolchain;
            var libraryPaths = new List<string>();
            var includes = IncludeDirectories.Select(x => Path.GetFullPath(x.ItemSpec)).ToList();
            var environment = new Dictionary<string, string>();
            string linker;
            if (Toolchain == "msvc") {
                if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)) throw new InvalidOperationException("MSVC requires a Windows host and its target SDK.");
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                var vswhere = Path.Combine(programFiles, "Microsoft Visual Studio", "Installer", "vswhere.exe");
                var install = Run(vswhere, new[] { "-latest", "-products", "*", "-property", "installationPath" }, environment).Output.Trim();
                var tools = Directory.GetDirectories(Path.Combine(install, "VC", "Tools", "MSVC")).OrderByDescending(x => Version.Parse(Path.GetFileName(x))).First();
                var arch = RuntimeIdentifier == "win-x86" ? "x86" : RuntimeIdentifier == "win-arm64" ? "arm64" : "x64";
                var host = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
                var bin = Path.Combine(tools, "bin", "Host" + host, arch);
                if (!Directory.Exists(bin)) throw new InvalidOperationException("Missing MSVC host/target tools: " + bin);
                Compiler = string.IsNullOrEmpty(Compiler) ? Path.Combine(bin, "cl.exe") : FindTool(Compiler);
                linker = Path.Combine(bin, "link.exe");
                includes.Add(Path.Combine(tools, "include"));
                libraryPaths.Add(Path.Combine(tools, "lib", arch));
                var sdk = string.IsNullOrEmpty(WindowsSdkRoot) ? Path.Combine(programFiles, "Windows Kits", "10") : WindowsSdkRoot;
                var version = Directory.GetDirectories(Path.Combine(sdk, "Include")).Select(Path.GetFileName).Where(x => Version.TryParse(x, out _)).OrderByDescending(x => Version.Parse(x)).First();
                foreach (var name in new[] { "ucrt", "shared", "um", "winrt", "cppwinrt" }) includes.Add(Path.Combine(sdk, "Include", version, name));
                foreach (var name in new[] { "ucrt", "um" }) libraryPaths.Add(Path.Combine(sdk, "Lib", version, name, arch));
                environment["VSLANG"] = "1033";
                environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            } else {
                if (Toolchain != "gcc" && Toolchain != "clang") throw new InvalidOperationException("Unsupported native toolchain: " + Toolchain);
                Compiler = FindTool(string.IsNullOrEmpty(Compiler) ? (Toolchain == "clang" ? "clang++" : "g++") : Compiler);
                linker = Compiler;
            }
            Directory.CreateDirectory(IntermediateDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(OutputFile)));
            var objects = new string[Sources.Length];
            var failed = 0;
            var compiled = 0;
            Parallel.For(0, Sources.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, MaxParallelism) }, i => {
                var source = Path.GetFullPath(Sources[i].ItemSpec);
                var obj = Path.Combine(IntermediateDirectory, Path.GetFileName(source) + "." + Hash(source).Substring(0, 12) + (Toolchain == "msvc" ? ".obj" : ".o"));
                objects[i] = obj;
                var dependencyFile = obj + ".d";
                var args = new List<string>();
                if (Toolchain == "msvc") {
                    args.AddRange(new[] { "/nologo", "/c", "/std:c++20", "/MD", "/EHsc", "/utf-8", "/sourceDependencies", dependencyFile, "/Fo" + obj, "/Fd" + obj + ".pdb" });
                    args.AddRange(includes.Select(x => "/I" + x));
                } else {
                    args.AddRange(new[] { "-c", "-std=c++20", "-fPIC", "-MMD", "-MF", dependencyFile, "-o", obj });
                    args.AddRange(includes.Select(x => "-I" + x));
                }
                args.AddRange(CompileOptions.Select(x => x.ItemSpec));
                args.Add(source);
                var fingerprint = Hash(Compiler + File.GetLastWriteTimeUtc(Compiler).Ticks + string.Join("\n", args));
                if (Current(obj, fingerprint)) return;
                File.Delete(obj + ".inputs");
                var result = Run(Compiler, args, environment);
                var dependencies = new List<string> { source };
                if (Toolchain == "msvc") {
                    if (!string.IsNullOrWhiteSpace(result.Output)) LogOutput(result.Output);
                    if (result.ExitCode == 0) {
                        using (var stream = File.OpenRead(dependencyFile)) {
                            var data = (MsvcDependencies)new DataContractJsonSerializer(typeof(MsvcDependencies)).ReadObject(stream);
                            if (data?.Data?.Includes == null) throw new InvalidDataException("Missing MSVC header dependency information.");
                            dependencies.AddRange(data.Data.Includes);
                        }
                    }
                } else {
                    if (!string.IsNullOrWhiteSpace(result.Output)) LogOutput(result.Output);
                    if (File.Exists(dependencyFile)) {
                        var content = File.ReadAllText(dependencyFile).Replace("\\\r\n", "").Replace("\\\n", "");
                        var colon = content.IndexOf(':');
                        dependencies.AddRange(Regex.Split(content.Substring(colon + 1).Trim(), @"(?<!\\)\s+").Where(x => x.Length > 0).Select(x => x.Replace("\\ ", " ").Replace("\\\\", "\\")));
                    }
                }
                if (result.ExitCode != 0) { Interlocked.Exchange(ref failed, 1); Log.LogError("Native compiler exited {0}: {1}{2}{3}", result.ExitCode, source, Environment.NewLine, result.Output); return; }
                File.WriteAllLines(obj + ".inputs", new[] { fingerprint }.Concat(dependencies.Distinct().Select(Path.GetFullPath)));
                Interlocked.Increment(ref compiled);
            });
            CompiledSources = compiled;
            if (failed != 0) return false;
            var link = new List<string>();
            if (Toolchain == "msvc") {
                link.AddRange(new[] { "/NOLOGO", "/DLL", "/OUT:" + Path.GetFullPath(OutputFile) });
                link.AddRange(libraryPaths.Select(x => "/LIBPATH:" + x));
                if (!string.IsNullOrEmpty(DefinitionFile)) link.Add("/DEF:" + Path.GetFullPath(DefinitionFile));
                link.AddRange(objects);
                link.AddRange(Libraries.Select(x => x.ItemSpec));
                link.AddRange(new[] { "user32.lib", "onecore_apiset.lib", "dxguid.lib" });
            } else {
                link.Add(System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX) ? "-dynamiclib" : "-shared");
                link.Add(System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX)
                    ? "-Wl,-install_name,@rpath/" + Path.GetFileName(OutputFile)
                    : "-Wl,-soname," + Path.GetFileName(OutputFile));
                link.Add(System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX) ? "-Wl,-rpath,@loader_path" : "-Wl,-rpath,$ORIGIN");
                link.AddRange(objects);
                foreach (var library in Libraries.Select(x => x.ItemSpec)) {
                    if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX)) {
                        link.Add(Path.GetExtension(library) == ".a" ? "-Wl,-force_load," + library : library);
                    }
                    else link.AddRange(new[] { "-Wl,--whole-archive", library, "-Wl,--no-whole-archive" });
                }
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX)) link.AddRange(new[] { "-framework", "Foundation", "-framework", "Metal", "-framework", "QuartzCore", "-framework", "IOSurface", "-framework", "IOKit", "-framework", "CoreFoundation", "-framework", "CoreGraphics", "-framework", "AppKit" });
                else link.AddRange(new[] { "-pthread", "-ldl", "-lrt", "-latomic" });
                link.AddRange(new[] { "-o", Path.GetFullPath(OutputFile) });
            }
            link.AddRange(LinkOptions.Select(x => x.ItemSpec));
            var inputs = objects.Concat(Libraries.Select(x => Path.GetFullPath(x.ItemSpec))).ToList();
            if (!string.IsNullOrEmpty(DefinitionFile)) inputs.Add(Path.GetFullPath(DefinitionFile));
            var signature = Hash(linker + File.GetLastWriteTimeUtc(linker).Ticks + string.Join("\n", link));
            if (!Current(OutputFile, signature) || inputs.Any(x => File.GetLastWriteTimeUtc(x) > File.GetLastWriteTimeUtc(OutputFile))) {
                File.Delete(OutputFile + ".inputs");
                var result = Run(linker, link, environment);
                if (!string.IsNullOrWhiteSpace(result.Output)) LogOutput(result.Output);
                if (result.ExitCode != 0) { Log.LogError("Native linker exited {0}:{1}{2}", result.ExitCode, Environment.NewLine, result.Output); return false; }
                File.WriteAllLines(OutputFile + ".inputs", new[] { signature }.Concat(inputs));
                Linked = true;
            }
            Log.LogMessage(MessageImportance.High, "Native {0}: compiled {1}/{2}, linked {3}: {4}", RuntimeIdentifier, compiled, Sources.Length, Linked, OutputFile);
            return !Log.HasLoggedErrors;
        } catch (Exception error) { Log.LogErrorFromException(error, true); return false; }
    }

    private void LogOutput(string output)
    {
        foreach (var line in output.Split('\n')) {
            if (Regex.IsMatch(line, @"\bwarning(?:\s+[A-Z]+[0-9]+)?:")) Log.LogWarning(line.TrimEnd());
            else if (!string.IsNullOrWhiteSpace(line)) Log.LogMessage(MessageImportance.Normal, line.TrimEnd());
        }
    }

    [DataContract]
    private sealed class MsvcDependencies
    {
        [DataMember] public MsvcIncludes Data { get; set; }
    }

    [DataContract]
    private sealed class MsvcIncludes
    {
        [DataMember] public string[] Includes { get; set; }
    }

    private static bool Current(string output, string signature)
    {
        if (!File.Exists(output) || !File.Exists(output + ".inputs")) return false;
        var lines = File.ReadAllLines(output + ".inputs");
        return lines.Length > 1 && lines[0] == signature && lines.Skip(1).All(x => File.Exists(x) && File.GetLastWriteTimeUtc(x) <= File.GetLastWriteTimeUtc(output));
    }

    private static string Hash(string value)
    {
        using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
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
            if (character == '\\') { slashes++; continue; }
            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            quoted.Append(character);
            slashes = 0;
        }
        quoted.Append('\\', slashes * 2);
        return quoted.Append('"').ToString();
    }

    private static (int ExitCode, string Output) Run(string tool, IEnumerable<string> args, IDictionary<string, string> environment)
    {
        var info = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Arguments = string.Join(" ", args.Select(Quote));
        foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        using (var process = Process.Start(info)) {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }
    }
}
