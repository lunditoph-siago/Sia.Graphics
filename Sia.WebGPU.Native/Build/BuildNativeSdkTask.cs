using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System;

public sealed class BuildNativeSdkTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public string RuntimeIdentifier { get; set; }

    [Required]
    public string SourceDirectory { get; set; }

    [Required]
    public string BuildDirectory { get; set; }

    [Required]
    public string SdkDirectory { get; set; }

    public override bool Execute()
    {
        try {
            BuildSdk(BuildDirectory);
            return true;
        }
        catch (Exception error) {
            Log.LogErrorFromException(error, true);
            return false;
        }
    }

    private bool IsWindowsTarget => RuntimeIdentifier.StartsWith("win-", StringComparison.Ordinal);

    private void BuildSdk(string build)
    {
        var args = new List<string> {
            "-S", SourceDirectory, "-B", build, "-DCMAKE_BUILD_TYPE=Release",
            "-DDAWN_FETCH_DEPENDENCIES=ON", "-DDAWN_BUILD_MONOLITHIC_LIBRARY=STATIC",
            "-DDAWN_BUILD_TESTS=OFF", "-DDAWN_BUILD_SAMPLES=OFF", "-DDAWN_USE_GLFW=OFF",
            "-DTINT_BUILD_TESTS=OFF", "-DTINT_BUILD_CMD_TOOLS=OFF", "-DDAWN_ENABLE_INSTALL=ON",
        };
        if (IsWindowsTarget) args.AddRange(new[] { "-A", RuntimeIdentifier == "win-x86" ? "Win32" : "ARM64" });

        Run(Log, "cmake", args.ToArray());
        Run(Log, "cmake", "--build", build, "--config", "Release",
            "--target", "webgpu_dawn", "--parallel", "2");
        CopyTree(Path.Combine(SourceDirectory, "include"), Path.Combine(SdkDirectory, "include"));
        CopyTree(Path.Combine(build, "gen", "include"), Path.Combine(SdkDirectory, "include"));

        var library = IsWindowsTarget ? "webgpu_dawn.lib" : "libwebgpu_dawn.a";
        var source = Directory.GetFiles(build, library, SearchOption.AllDirectories).Single();
        Copy(source, Path.Combine(SdkDirectory, "lib", library));
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

    private static string Run(TaskLoggingHelper log, string command, params string[] args) =>
        Run(log, command, args, false);

    private static string Run(TaskLoggingHelper log, string command, string[] args, bool allowFailure)
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
            if (!allowFailure) log.LogMessage(MessageImportance.High, text);
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
}
