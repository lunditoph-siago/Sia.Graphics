using Sia.Spirv.Compiler.Compilation;

namespace Sia.Spirv.Tool;

internal static class SpirvCommandLine
{
    public static int Run(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] is "-h" or "--help") {
            WriteUsage();
            return args.Count == 0 ? 1 : 0;
        }
        if (args[0] != "compile") {
            Console.Error.WriteLine($"Unknown command '{args[0]}'.");
            WriteUsage();
            return 1;
        }

        try {
            var values = ParseOptions(args.Skip(1).ToArray());
            var assemblyPath = GetRequired(values, "assembly");
            var outputPath = GetRequired(values, "output");
            var options = new SpirvCompilationOptions {
                ToolchainDirectory = values.GetValueOrDefault("toolchain"),
                TargetEnvironment = values.GetValueOrDefault("target") ?? "vulkan1.2",
                OptimizationLevel = int.Parse(
                    values.GetValueOrDefault("optimization") ?? "2",
                    System.Globalization.CultureInfo.InvariantCulture),
                EmitLlvmIr = !values.ContainsKey("no-llvm-ir")
            };
            var artifacts = new SpirvCompiler().CompileAssembly(
                assemblyPath,
                outputPath,
                options);
            foreach (var artifact in artifacts) {
                var state = artifact.CacheHit ? "cached" : "compiled";
                Console.WriteLine($"SPIR-V {state}: {artifact.Kernel.QualifiedName} -> {artifact.SpirvPath}");
            }
            return 0;
        }
        catch (SpirvCompilationException exception) when (exception.Diagnostics.Count != 0) {
            foreach (var diagnostic in exception.Diagnostics) {
                var offset = diagnostic.IlOffset is int value ? $" IL_{value:x4}" : string.Empty;
                Console.Error.WriteLine(
                    $"{diagnostic.Method}{offset}: {diagnostic.Severity.ToString().ToLowerInvariant()} " +
                    $"{diagnostic.Id}: {diagnostic.Message}");
            }
            return 1;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            FileNotFoundException or
            IOException or
            SpirvCompilationException) {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static Dictionary<string, string?> ParseOptions(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index++) {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal)) {
                throw new ArgumentException($"Unexpected argument '{argument}'.");
            }
            var name = argument[2..];
            if (name == "no-llvm-ir") {
                values[name] = null;
                continue;
            }
            if (++index >= args.Count) {
                throw new ArgumentException($"Option '--{name}' requires a value.");
            }
            values[name] = args[index];
        }
        return values;
    }

    private static string GetRequired(
        IReadOnlyDictionary<string, string?> values,
        string name)
    {
        if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException($"Option '--{name}' is required.");
        }
        return value;
    }

    private static void WriteUsage()
    {
        Console.WriteLine(
            "Usage: sia-spirv compile --assembly <path> --output <directory> " +
            "[--toolchain <directory>] [--target vulkan1.2|vulkan1.3] " +
            "[--optimization 0..3] [--no-llvm-ir]");
    }
}
