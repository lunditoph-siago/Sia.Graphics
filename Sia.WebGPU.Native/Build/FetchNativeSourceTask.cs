using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System;

public sealed class FetchNativeSourceTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public string Revision { get; set; }

    [Required]
    public string SourceDirectory { get; set; }

    public override bool Execute()
    {
        try {
            if (!Regex.IsMatch(Revision, "^[a-f0-9]{40}$")) throw new InvalidDataException("Invalid source revision.");
            SourceDirectory = Path.GetFullPath(SourceDirectory);
            Directory.CreateDirectory(SourceDirectory);
            if (!Directory.Exists(Path.Combine(SourceDirectory, ".git")))
                Run(Log, "git", "-C", SourceDirectory, "init", "--quiet");
            var current = Run(Log, "git",
                new[] { "-C", SourceDirectory, "rev-parse", "--verify", "HEAD" }, true).Trim();
            if (current == Revision) return true;
            Run(Log, "git", "-C", SourceDirectory, "fetch", "--depth=1",
                "https://github.com/google/dawn.git", Revision);
            Run(Log, "git", "-C", SourceDirectory, "checkout", "--detach", Revision);
            return true;
        }
        catch (Exception error) {
            Log.LogErrorFromException(error, true);
            return false;
        }
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
