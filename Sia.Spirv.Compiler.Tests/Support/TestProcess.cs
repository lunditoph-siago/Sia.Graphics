using System.Diagnostics;

namespace Sia.Spirv.Compiler.Tests.Support;

internal static class TestProcess
{
    internal static string Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Failed to start '{executable}'.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000)) {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException($"'{executable}' exceeded the 30 second test timeout.");
        }

        string output = stdout.GetAwaiter().GetResult();
        string error = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0) {
            throw new InvalidOperationException(
                $"'{executable}' exited {process.ExitCode}: {string.Join(' ', arguments)}" +
                $"{Environment.NewLine}{error}{output}");
        }
        return output;
    }
}
