using System.Runtime.InteropServices;

namespace Sia.Spirv.Compiler.Tests;

internal static class SpirvTestToolchain
{
    private static readonly string _executableSuffix =
        OperatingSystem.IsWindows() ? ".exe" : string.Empty;

    public static string? Directory { get; } = FindDirectory();

    private static string? FindDirectory()
    {
        var candidates = new List<string?> {
            Environment.GetEnvironmentVariable("SIA_SPIRV_TOOLCHAIN")
        };

        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current != null) {
            candidates.Add(Path.Combine(current.FullName, "artifacts", "llvm-toolchain", "bin"));
            candidates.Add(Path.Combine(
                current.FullName,
                "artifacts",
                $"llvm-toolchain-{RuntimeInformation.RuntimeIdentifier}",
                "bin"));
            current = current.Parent;
        }

        var dotNetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (string.IsNullOrWhiteSpace(dotNetRoot)) {
            dotNetRoot = Path.GetDirectoryName(Environment.ProcessPath);
        }
        if (!string.IsNullOrWhiteSpace(dotNetRoot)) {
            var packRoot = Path.Combine(
                dotNetRoot,
                "packs",
                $"Sia.Spirv.Toolchain.{RuntimeInformation.RuntimeIdentifier}");
            if (System.IO.Directory.Exists(packRoot)) {
                candidates.AddRange(System.IO.Directory
                    .EnumerateDirectories(packRoot)
                    .OrderDescending()
                    .Select(versionDirectory => Path.Combine(
                        versionDirectory,
                        "tools",
                        RuntimeInformation.RuntimeIdentifier)));
            }
        }

        return candidates
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(static candidate => Path.GetFullPath(candidate!))
            .FirstOrDefault(IsComplete);
    }

    private static bool IsComplete(string directory) =>
        new[] { "llc", "opt", "spirv-opt", "spirv-val", "naga" }
            .All(tool => File.Exists(Path.Combine(directory, tool + _executableSuffix)));
}

public sealed class SpirvToolchainFactAttribute : FactAttribute
{
    public SpirvToolchainFactAttribute()
    {
        if (SpirvTestToolchain.Directory == null) {
            Skip = "The SPIR-V LLVM, SPIRV-Tools, and Naga toolchain is not installed.";
        }
    }
}
