using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Sia.Spirv.Bootstrap;

internal static class WorkloadBootstrapper
{
    private const string ManifestId = "sia.spirv.workload";
    private const string TargetsResourceName =
        "Sia.Spirv.Bootstrap.WorkloadManifest.targets";

    public static async Task<int> RunAsync(
        BootstrapOptions options,
        CancellationToken cancellationToken)
    {
        ValidateHost();
        var sdk = await DotNetSdk.DiscoverAsync(
            options.DotNetPath, cancellationToken);
        var manifestDirectory = Path.Combine(
            sdk.RootDirectory,
            "sdk-manifests",
            sdk.FeatureBand,
            ManifestId,
            PackageInfo.Version);
        Directory.CreateDirectory(manifestDirectory);
        WriteAtomically(
            Path.Combine(manifestDirectory, "WorkloadManifest.json"),
            CreateManifestJson(PackageInfo.Version));
        WriteAtomically(
            Path.Combine(manifestDirectory, "WorkloadManifest.targets"),
            ReadTargets());

        Console.WriteLine(
            $"Registered spirv-tools {PackageInfo.Version} for SDK {sdk.FeatureBand}.");
        if (!options.InstallWorkload) {
            return 0;
        }

        var startInfo = new ProcessStartInfo(sdk.DotNetPath)
        {
            UseShellExecute = false
        };
        foreach (var argument in new[]
        {
            "workload", "install", "spirv-tools", "--skip-manifest-update"
        }) {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var source in options.Sources) {
            startInfo.ArgumentList.Add("--source");
            startInfo.ArgumentList.Add(source);
        }
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"Could not start '{sdk.DotNetPath}'.");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    internal static string CreateManifestJson(string version)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["version"] = version,
            ["description"] =
                "Sia C# to SPIR-V compiler powered by the LLVM 23 SPIR-V backend.",
            ["workloads"] = new Dictionary<string, object?>
            {
                ["spirv-tools"] = new Dictionary<string, object?>
                {
                    ["description"] =
                        "Sia C# to SPIR-V compiler and LLVM/Khronos validation toolchain",
                    ["packs"] = new[]
                    {
                        "Sia.Spirv.Sdk",
                        "Sia.Spirv.Toolchain"
                    },
                    ["platforms"] = new[]
                    {
                        "win-x64",
                        "linux-x64"
                    }
                }
            },
            ["packs"] = new Dictionary<string, object?>
            {
                ["Sia.Spirv.Sdk"] = new Dictionary<string, object?>
                {
                    ["kind"] = "sdk",
                    ["version"] = version
                },
                ["Sia.Spirv.Toolchain"] = new Dictionary<string, object?>
                {
                    ["kind"] = "sdk",
                    ["version"] = version,
                    ["alias-to"] = new Dictionary<string, string>
                    {
                        ["win-x64"] = "Sia.Spirv.Toolchain.win-x64",
                        ["linux-x64"] = "Sia.Spirv.Toolchain.linux-x64"
                    }
                }
            }
        };
        return JsonSerializer.Serialize(
            manifest,
            new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    private static void ValidateHost()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) {
            throw new PlatformNotSupportedException(
                "The first preview supports Windows and Linux build hosts.");
        }
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) {
            throw new PlatformNotSupportedException(
                "The first preview supports x64 build hosts.");
        }
    }

    private static string ReadTargets()
    {
        using var stream = typeof(WorkloadBootstrapper).Assembly
            .GetManifestResourceStream(TargetsResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{TargetsResourceName}' was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void WriteAtomically(string destination, string contents)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            File.WriteAllText(temporary, contents, new UTF8Encoding(false));
            File.Move(temporary, destination, true);
        }
        finally {
            File.Delete(temporary);
        }
    }
}
