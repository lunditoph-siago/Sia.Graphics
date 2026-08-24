using System.Diagnostics;

namespace Sia.Spirv.Compiler.LLVM;

public sealed class LlvmToolchain
{
    public LlvmToolchain(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
        EnsureToolExists("llc.exe");
        EnsureToolExists("opt.exe");
        EnsureToolExists("spirv-val.exe");
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
            candidates.Add(Path.Combine(current.FullName, "artifacts", "llvm-toolchain", "bin"));
            current = current.Parent;
        }

        foreach (var candidate in candidates.Where(static candidate => !string.IsNullOrWhiteSpace(candidate))) {
            if (File.Exists(Path.Combine(candidate!, "llc.exe")) &&
                File.Exists(Path.Combine(candidate!, "opt.exe")) &&
                File.Exists(Path.Combine(candidate!, "spirv-val.exe"))) {
                return new LlvmToolchain(candidate!);
            }
        }

        throw new FileNotFoundException(
            "The Sia SPIR-V LLVM toolchain was not found. Set SIA_SPIRV_TOOLCHAIN or pass ToolchainDirectory.");
    }

    public void Optimize(string inputPath, string outputPath)
    {
        Run(
            "opt.exe",
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
            "llc.exe",
            "--filetype=obj",
            $"--mtriple={triple}",
            "-O",
            optimizationLevel.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-o",
            outputPath,
            inputPath);
    }

    public void Validate(string inputPath, string targetEnvironment) =>
        Run("spirv-val.exe", "--target-env", targetEnvironment, inputPath);

    public string GetLlvmVersion()
    {
        var output = Run("llc.exe", "--version");
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .FirstOrDefault(static line => line.StartsWith("LLVM version", StringComparison.Ordinal)) ??
            FirstLine(output);
    }

    public string GetSpirvToolsVersion() => FirstLine(Run("spirv-val.exe", "--version"));

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
}
