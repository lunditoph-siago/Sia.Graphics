using System.Runtime.InteropServices;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.LLVM;

namespace Sia.Spirv.Compiler.Tests.Support;

internal static class SpirvTestToolchain
{
    private static readonly string s_ExecutableSuffix = OperatingSystem.IsWindows() ? ".exe" : "";
    internal const string UnavailableReason = "The SPIR-V LLVM and SPIRV-Tools toolchain is not installed.";
    public static string? Directory { get; } = FindDirectory();

    public static string Disassemble(string spirvPath)
        => TestProcess.Run(Tool("spirv-dis"), spirvPath);

    internal static byte[] Assemble(string source)
    {
        using var directory = new TestDirectory();
        string input = directory.File("input.spvasm"), output = directory.File("output.spv");
        File.WriteAllText(input, source);
        TestProcess.Run(Tool("spirv-as"), "--target-env", "spv1.3", input, "-o", output);
        return File.ReadAllBytes(output);
    }

    internal static void Validate(byte[] binary)
    {
        using var directory = new TestDirectory();
        string input = directory.File("input.spv");
        File.WriteAllBytes(input, binary);
        new LlvmToolchain(Directory ?? throw new InvalidOperationException(UnavailableReason))
            .Validate(input, SpirvCompilationTarget.Default.Environment);
    }

    private static string Tool(string name)
        => Path.Combine(Directory ?? throw new InvalidOperationException(UnavailableReason), name + s_ExecutableSuffix);

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
        new[] { "llc", "opt", "spirv-as", "spirv-dis", "spirv-opt", "spirv-val" }
            .All(tool => File.Exists(Path.Combine(directory, tool + s_ExecutableSuffix)));
}
