using System.Diagnostics;

namespace Sia.Spirv.Bootstrap;

internal sealed record DotNetSdk(
    string DotNetPath,
    string RootDirectory,
    string Version,
    string FeatureBand)
{
    public static async Task<DotNetSdk> DiscoverAsync(
        string dotnetPath,
        CancellationToken cancellationToken)
    {
        var version = (await RunAndCaptureAsync(
            dotnetPath, ["--version"], cancellationToken)).Trim();
        var featureBand = (await RunAndCaptureAsync(
            dotnetPath, ["msbuild",
                Path.Combine(AppContext.BaseDirectory, "SdkFeatureBand.targets"),
                "-target:ResolveSiaSdkFeatureBand",
                "-getProperty:SiaSpirvSdkFeatureBand"], cancellationToken)).Trim();
        var installedSdks = await RunAndCaptureAsync(
            dotnetPath, ["--list-sdks"], cancellationToken);
        var sdkDirectory = GetSdkDirectory(installedSdks, version);
        var rootDirectory = Directory.GetParent(sdkDirectory)?.FullName
            ?? throw new InvalidOperationException(
                $"Could not determine the .NET root from '{sdkDirectory}'.");
        return new DotNetSdk(dotnetPath, rootDirectory, version, featureBand);
    }

    internal static string GetSdkDirectory(string installedSdks, string sdkVersion)
    {
        foreach (var line in installedSdks.Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) {
            var prefix = sdkVersion + " [";
            if (!line.StartsWith(prefix, StringComparison.Ordinal) ||
                !line.EndsWith(']')) {
                continue;
            }
            return line[prefix.Length..^1];
        }
        throw new InvalidOperationException(
            $"The selected SDK '{sdkVersion}' was not present in dotnet --list-sdks.");
    }

    private static async Task<string> RunAndCaptureAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName) {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) {
            throw new InvalidOperationException(
                $"'{fileName} {string.Join(' ', arguments)}' failed: {error.Trim()}");
        }
        return output;
    }
}
