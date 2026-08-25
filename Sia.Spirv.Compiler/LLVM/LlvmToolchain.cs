using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sia.Spirv.Compiler.LLVM;

public sealed class LlvmToolchain
{
    private static readonly string ExecutableSuffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;

    public LlvmToolchain(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
        EnsureToolExists(ToolName("llc"));
        EnsureToolExists(ToolName("opt"));
        EnsureToolExists(ToolName("spirv-val"));
    }

    public string Directory { get; }

    public static LlvmToolchain Locate(string? explicitDirectory = null)
    {
        var candidates = new List<string?> {
            explicitDirectory,
            Environment.GetEnvironmentVariable("SIA_SPIRV_TOOLCHAIN"),
            Path.Combine(AppContext.BaseDirectory, "tools")
        };
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current != null) {
            candidates.Add(Path.Combine(
                current.FullName,
                "artifacts",
                $"llvm-toolchain-{RuntimeInformation.RuntimeIdentifier}",
                "bin"));
            candidates.Add(Path.Combine(current.FullName, "artifacts", "llvm-toolchain", "bin"));
            current = current.Parent;
        }

        foreach (var candidate in candidates.Where(static candidate => !string.IsNullOrWhiteSpace(candidate))) {
            if (File.Exists(Path.Combine(candidate!, ToolName("llc"))) &&
                File.Exists(Path.Combine(candidate!, ToolName("opt"))) &&
                File.Exists(Path.Combine(candidate!, ToolName("spirv-val")))) {
                return new LlvmToolchain(candidate!);
            }
        }

        throw new FileNotFoundException(
            "The Sia SPIR-V LLVM toolchain was not found. Set SIA_SPIRV_TOOLCHAIN or pass ToolchainDirectory.");
    }

    public void Optimize(string inputPath, string outputPath)
    {
        Run(
            ToolName("opt"),
            "-S",
            "-passes=mem2reg,simplifycfg",
            inputPath,
            "-o",
            outputPath);
    }

    public void Compile(
        string inputPath,
        string outputPath,
        int optimizationLevel,
        string targetEnvironment)
    {
        if (optimizationLevel is < 0 or > 3) {
            throw new ArgumentOutOfRangeException(
                nameof(optimizationLevel),
                optimizationLevel,
                "LLVM optimization level must be between zero and three.");
        }
        var triple = targetEnvironment switch {
            "vulkan1.2" => "spirv64-unknown-vulkan1.2",
            "vulkan1.3" => "spirv64-unknown-vulkan1.3",
            _ => throw new ArgumentException(
                $"Target environment '{targetEnvironment}' is not supported.",
                nameof(targetEnvironment))
        };
        Run(
            ToolName("llc"),
            "--filetype=obj",
            $"--mtriple={triple}",
            "-O",
            optimizationLevel.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-o",
            outputPath,
            inputPath);
    }

    public void Validate(string inputPath, string targetEnvironment) =>
        Run(ToolName("spirv-val"), "--target-env", targetEnvironment, inputPath);

    public string GetLlvmVersion()
    {
        var output = Run(ToolName("llc"), "--version");
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .FirstOrDefault(static line => line.StartsWith("LLVM version", StringComparison.Ordinal)) ??
            FirstLine(output);
    }

    public string GetSpirvToolsVersion() => FirstLine(Run(ToolName("spirv-val"), "--version"));

    private string Run(string tool, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo {
            FileName = Path.Combine(Directory, tool),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException(
            $"Failed to start '{startInfo.FileName}'.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) {
            var output = string.Join(
                Environment.NewLine,
                new[] { standardError.Trim(), standardOutput.Trim() }
                    .Where(static value => value.Length != 0));
            throw new InvalidDataException(
                $"{tool} failed with exit code {process.ExitCode}:{Environment.NewLine}{output}");
        }
        return string.Join(
            Environment.NewLine,
            new[] { standardOutput.Trim(), standardError.Trim() }
                .Where(static value => value.Length != 0));
    }

    private void EnsureToolExists(string name)
    {
        var path = Path.Combine(Directory, name);
        if (!File.Exists(path)) {
            throw new FileNotFoundException($"Required SPIR-V tool '{name}' was not found.", path);
        }
    }

    private static string FirstLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

    private static string ToolName(string name) => $"{name}{ExecutableSuffix}";
}
