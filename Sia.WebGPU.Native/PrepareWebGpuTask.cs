using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;

public sealed class PrepareWebGpuTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public string HeaderFile { get; set; }

    [Required]
    public string ArtifactsDirectory { get; set; }

    public string RuntimeIdentifier { get; set; }

    public string OutputDirectory { get; set; }

    [Output]
    public string ReleaseTag { get; set; }

    [Output]
    public string Revision { get; set; }

    [Output]
    public string HeaderRoot { get; set; }

    [Output]
    public string SdkRoot { get; set; }

    [Output]
    public string SourceRoot { get; set; }

    private bool IsWindowsTarget => RuntimeIdentifier.StartsWith("win-", StringComparison.Ordinal);

    public override bool Execute()
    {
        try {
            HeaderFile = Path.GetFullPath(HeaderFile);
            ArtifactsDirectory = Path.GetFullPath(ArtifactsDirectory);
            Directory.CreateDirectory(ArtifactsDirectory);

            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) }) {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Sia.WebGPU");
                var release = FindRelease(client);
                ReleaseTag = release.Tag;
                var headerAsset = release.Assets.Single(asset =>
                    Regex.IsMatch(asset.Name, @"^dawn-headers-[a-f0-9]{40}\.tar\.gz$"));
                Revision = Regex.Match(headerAsset.Name, "[a-f0-9]{40}").Value;

                var root = Path.Combine(ArtifactsDirectory, release.Tag);
                Directory.CreateDirectory(root);
                HeaderRoot = Path.Combine(root, "headers");
                PrepareHeaders(client, root, headerAsset);
                if (string.IsNullOrEmpty(RuntimeIdentifier)) return true;

                SdkRoot = Path.Combine(root, "native-" + RuntimeIdentifier);
                SourceRoot = Path.Combine(root, "source");
                PrepareSdk(client, root, release);
                PrepareRuntime(client, root);
                Log.LogMessage(MessageImportance.High, "WebGPU SDK {0}: {1}", release.Tag, SdkRoot);
            }

            return true;
        }
        catch (Exception error) {
            Log.LogErrorFromException(error, true);
            return false;
        }
    }

    private void PrepareHeaders(HttpClient client, string root, AssetInfo asset)
    {
        if (!IsHeaderComplete(HeaderRoot)) {
            var archive = DownloadAsset(client, root, asset);
            InstallArchive(archive, HeaderRoot, IsHeaderComplete, "Incomplete native header archive.");
        }

        var header = Path.Combine(HeaderRoot, "include", "dawn", "webgpu.h");
        if (!File.Exists(HeaderFile) || !File.ReadAllBytes(header).SequenceEqual(File.ReadAllBytes(HeaderFile)))
            Copy(header, HeaderFile);
    }

    private void PrepareSdk(HttpClient client, string root, Release release)
    {
        var runners = new Dictionary<string, string> {
            ["win-x64"] = "windows-latest",
            ["linux-x64"] = "ubuntu-latest",
            ["osx-x64"] = "macos-15-intel",
            ["osx-arm64"] = "macos-latest",
        };

        if (IsWindowsTarget || !runners.ContainsKey(RuntimeIdentifier)) PrepareSource();
        if (runners.TryGetValue(RuntimeIdentifier, out var runner)) {
            if (!IsSdkComplete(SdkRoot)) {
                var name = "Dawn-" + Revision + "-" + runner + "-Release.tar.gz";
                var archive = DownloadAsset(client, root, release.Assets.Single(asset => asset.Name == name));
                InstallArchive(archive, SdkRoot, IsSdkComplete, "Incomplete native SDK archive: " + name);
            }
        }
        else {
            BuildSdk(Path.Combine(root, "build-" + RuntimeIdentifier));
        }
    }

    private void PrepareSource()
    {
        Directory.CreateDirectory(SourceRoot);
        if (!Directory.Exists(Path.Combine(SourceRoot, ".git"))) Run("git", "-C", SourceRoot, "init", "--quiet");

        var current = Run("git", new[] { "-C", SourceRoot, "rev-parse", "--verify", "HEAD" }, true).Trim();
        if (current == Revision) return;

        Run("git", "-C", SourceRoot, "fetch", "--depth=1", "https://github.com/google/dawn.git", Revision);
        Run("git", "-C", SourceRoot, "checkout", "--detach", Revision);
    }

    private void BuildSdk(string build)
    {
        var args = new List<string> {
            "-S", SourceRoot, "-B", build, "-DCMAKE_BUILD_TYPE=Release",
            "-DDAWN_FETCH_DEPENDENCIES=ON", "-DDAWN_BUILD_MONOLITHIC_LIBRARY=STATIC",
            "-DDAWN_BUILD_TESTS=OFF", "-DDAWN_BUILD_SAMPLES=OFF", "-DDAWN_USE_GLFW=OFF",
            "-DTINT_BUILD_TESTS=OFF", "-DTINT_BUILD_CMD_TOOLS=OFF", "-DDAWN_ENABLE_INSTALL=ON",
        };
        if (IsWindowsTarget) args.AddRange(new[] { "-A", RuntimeIdentifier == "win-x86" ? "Win32" : "ARM64" });

        Run("cmake", args.ToArray());
        Run("cmake", "--build", build, "--config", "Release", "--target", "webgpu_dawn", "--parallel", "2");
        CopyTree(Path.Combine(SourceRoot, "include"), Path.Combine(SdkRoot, "include"));
        CopyTree(Path.Combine(build, "gen", "include"), Path.Combine(SdkRoot, "include"));

        var library = IsWindowsTarget ? "webgpu_dawn.lib" : "libwebgpu_dawn.a";
        var source = Directory.GetFiles(build, library, SearchOption.AllDirectories).Single();
        Copy(source, Path.Combine(SdkRoot, "lib", library));
    }

    private void PrepareRuntime(HttpClient client, string root)
    {
        Directory.CreateDirectory(OutputDirectory);
        if (!IsWindowsTarget) return;

        var arch = RuntimeIdentifier.Substring(4);
        var redist = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Windows Kits", "10", "Redist", "D3D", arch);
        foreach (var name in new[] { "dxcompiler.dll", "dxil.dll" })
            Copy(Path.Combine(redist, name), Path.Combine(OutputDirectory, name));

        var product = FileVersionInfo.GetVersionInfo(Path.Combine(redist, "dxcompiler.dll")).ProductVersion;
        var compiler = Regex.Match(product, @"\(([a-f0-9]{7,40})\)");
        if (!compiler.Success) throw new InvalidDataException("Cannot identify DXC revision.");

        var revision = compiler.Groups[1].Value;
        var license = Path.Combine(root, "LICENSE.dxc-" + revision);
        if (!File.Exists(license)) {
            var url = "https://raw.githubusercontent.com/microsoft/DirectXShaderCompiler/" + revision + "/LICENSE.TXT";
            Download(client, url, license);
        }

        Copy(license, Path.Combine(OutputDirectory, "LICENSE.dxc"));
        Write(Path.Combine(OutputDirectory, "dxc-version.txt"), product);
    }

    private Release FindRelease(HttpClient client)
    {
        if (!string.IsNullOrEmpty(ReleaseTag)) {
            if (!Regex.IsMatch(ReleaseTag, @"^v\d{8}\.\d{6}$"))
                throw new InvalidDataException("Invalid native release tag.");

            var path = Path.Combine(ArtifactsDirectory, ReleaseTag + ".json");
            try {
                var cached = ReadRelease(path);
                if (MatchesRelease(cached) && cached.Tag == ReleaseTag) return cached;
            }
            catch (SerializationException) {
                Log.LogMessage(MessageImportance.Normal, "Ignoring invalid release cache: {0}", path);
            }
            catch (IOException) {
                Log.LogMessage(MessageImportance.Normal, "Cannot read release cache: {0}", path);
            }
        }

        var endpoint = string.IsNullOrEmpty(ReleaseTag) ? "latest" : "tags/" + Uri.EscapeDataString(ReleaseTag);
        var url = "https://api.github.com/repos/google/dawn/releases/" + endpoint;
        using (var stream = client.GetStreamAsync(url).GetAwaiter().GetResult()) {
            var serializer = new DataContractJsonSerializer(typeof(Release));
            var release = (Release)serializer.ReadObject(stream);
            if (!MatchesRelease(release) || (!string.IsNullOrEmpty(ReleaseTag) && release.Tag != ReleaseTag))
                throw new InvalidDataException("Invalid native release metadata.");

            CacheRelease(release);
            return release;
        }
    }

    private void CacheRelease(Release release)
    {
        var path = Path.Combine(ArtifactsDirectory, release.Tag + ".json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var file = File.Create(temporary)) {
                var serializer = new DataContractJsonSerializer(typeof(Release));
                serializer.WriteObject(file, release);
            }

            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static Release ReadRelease(string path)
    {
        using (var stream = File.OpenRead(path)) {
            var serializer = new DataContractJsonSerializer(typeof(Release));
            return (Release)serializer.ReadObject(stream);
        }
    }

    private static bool MatchesRelease(Release release) =>
        release != null && Regex.IsMatch(release.Tag ?? "", @"^v\d{8}\.\d{6}$") && release.Assets != null &&
        release.Assets.Count(asset =>
            asset != null && Regex.IsMatch(asset.Name ?? "", @"^dawn-headers-[a-f0-9]{40}\.tar\.gz$")) == 1;

    private static bool IsHeaderComplete(string directory) =>
        new[] { "webgpu/webgpu.h", "dawn/webgpu.h", "dawn/dawn_version.h" }
            .All(name => IsNonEmptyFile(Path.Combine(directory, "include", name)));

    private bool IsSdkComplete(string directory)
    {
        var library = IsWindowsTarget ? "webgpu_dawn.lib" : "libwebgpu_dawn.a";
        return IsNonEmptyFile(Path.Combine(directory, "include", "dawn", "webgpu.h")) &&
            IsNonEmptyFile(Path.Combine(directory, "include", "dawn", "dawn_version.h")) &&
            Directory.GetDirectories(directory, "lib*").Any(path => IsNonEmptyFile(Path.Combine(path, library)));
    }

    private static bool IsNonEmptyFile(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    private static string DownloadAsset(HttpClient client, string root, AssetInfo asset)
    {
        if (Path.GetFileName(asset.Name) != asset.Name || !Regex.IsMatch(asset.Digest ?? "", "^sha256:[a-f0-9]{64}$"))
            throw new InvalidDataException("Invalid release asset metadata.");

        var path = Path.Combine(root, asset.Name);
        var expected = asset.Digest.Substring(7);
        if (File.Exists(path) && Hash(path) == expected) return path;

        Download(client, asset.Url, path, expected);
        return path;
    }

    private static string Hash(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var sha = SHA256.Create()) {
            var bytes = sha.ComputeHash(stream);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }
    }

    private static void Download(HttpClient client, string url, string path, string expected = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var input = client.GetStreamAsync(url).GetAwaiter().GetResult())
            using (var output = File.Create(temporary)) input.CopyTo(output);

            if (expected != null && Hash(temporary) != expected)
                throw new InvalidDataException("Checksum mismatch: " + url);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }
        finally {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void InstallArchive(
        string archive,
        string destination,
        Func<string, bool> isComplete,
        string error)
    {
        var extracted = Extract(archive, Path.GetDirectoryName(destination));
        try {
            if (!isComplete(extracted)) throw new InvalidDataException(error);
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            Directory.Move(extracted, destination);
        }
        finally {
            Directory.Delete(Path.GetDirectoryName(extracted), true);
        }
    }

    private static string Extract(string archive, string root)
    {
        var directory = Path.Combine(root, "extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            using (var file = File.OpenRead(archive))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress)) {
                var tar = Type.GetType("System.Formats.Tar.TarFile, System.Formats.Tar", true);
                var method = tar.GetMethod(
                    "ExtractToDirectory", new[] { typeof(Stream), typeof(string), typeof(bool) });
                method.Invoke(null, new object[] { gzip, directory, true });
            }

            if (Directory.GetFiles(directory).Length != 0) throw new InvalidDataException("Unexpected archive layout.");
            return Directory.GetDirectories(directory).Single();
        }
        catch {
            Directory.Delete(directory, true);
            throw;
        }
    }

    private static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        File.Copy(source, destination, true);
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source)) Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(source))
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static void Write(string path, string value)
    {
        if (!File.Exists(path) || File.ReadAllText(path) != value) File.WriteAllText(path, value);
    }

    private string Run(string command, params string[] args) => Run(command, args, false);

    private string Run(string command, string[] args, bool allowFailure)
    {
        var start = new ProcessStartInfo(command) {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = string.Join(" ", args.Select(Quote)),
        };
        using (var process = Process.Start(start)) {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();

            var text = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0 && !allowFailure)
                throw new InvalidOperationException(command + " exited " + process.ExitCode + ": " + text);
            if (!allowFailure) Log.LogMessage(MessageImportance.High, text);
            return process.ExitCode == 0 ? text : "";
        }
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

    [DataContract]
    private sealed class Release
    {
        [DataMember(Name = "tag_name")]
        public string Tag { get; set; }

        [DataMember(Name = "assets")]
        public AssetInfo[] Assets { get; set; }
    }

    [DataContract]
    private sealed class AssetInfo
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "digest")]
        public string Digest { get; set; }

        [DataMember(Name = "browser_download_url")]
        public string Url { get; set; }
    }
}
