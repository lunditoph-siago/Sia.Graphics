using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Tool;

internal static class SpirvCommandLine
{
    public static int Run(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] is "-h" or "--help") {
            WriteUsage();
            return args.Count == 0 ? 1 : 0;
        }
        if (args[0] is not ("compile" or "translate" or "validate")) {
            Console.Error.WriteLine($"Unknown command '{args[0]}'.");
            WriteUsage();
            return 1;
        }

        try {
            var values = ParseOptions(args.Skip(1).ToArray());
            if (args[0] is "translate" or "validate") {
                var input = GetRequired(values, "input");
                bool spirv = Path.GetExtension(input).Equals(".spv", StringComparison.OrdinalIgnoreCase);
                if (!spirv && !Path.GetExtension(input).Equals(".wgsl", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Input must be a .spv or .wgsl file.");
                if (args[0] == "validate") {
                    ModuleValidator.Validate(spirv ? SpirvReader.Parse(File.ReadAllBytes(input)) : WgslReader.Parse(File.ReadAllText(input)));
                }
                else {
                    var output = GetRequired(values, "output");
                    if (!Path.GetExtension(output).Equals(spirv ? ".wgsl" : ".spv", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Output must use the other supported shader format.");
                    if (spirv) File.WriteAllText(output, ShaderTranslator.SpirvToWgsl(File.ReadAllBytes(input)));
                    else File.WriteAllBytes(output, ShaderTranslator.WgslToSpirv(File.ReadAllText(input)));
                }
                return 0;
            }
            var assemblyPath = GetRequired(values, "assembly");
            var outputPath = GetRequired(values, "output");
            var passes = values.GetValueOrDefault("passes");
            var passesFile = values.GetValueOrDefault("passes-json");
            if (passes != null && passesFile != null) {
                throw new ArgumentException("Options '--passes' and '--passes-json' cannot be combined.");
            }
            if (passesFile != null) {
                passes = SpirvPassConfiguration.Load(passesFile).LlvmPasses;
            }
            var targetProfileFile = values.GetValueOrDefault("target-profile-json");
            var variantsFile = values.GetValueOrDefault("variants-json");
            if (targetProfileFile is not null && variantsFile is not null) {
                throw new ArgumentException("Options '--target-profile-json' and '--variants-json' cannot be combined.");
            }
            string environment = values.GetValueOrDefault("target") ?? SpirvCompilationTarget.Default.Environment;
            var request = new SpirvFileCompilationRequest(assemblyPath, outputPath) {
                ToolchainDirectory = values.GetValueOrDefault("toolchain"),
                Target = SpirvCompilationTarget.Default with {
                    Environment = environment,
                    Version = environment == "vulkan1.3" ? 0x00010600u : SpirvCompilationTarget.Default.Version,
                    KernelAbi = ParseKernelAbi(values.GetValueOrDefault("abi") ?? "webgpu"),
                    ResourceLimits = targetProfileFile is null
                        ? SpirvTargetProfile.Default : SpirvTargetProfile.Load(targetProfileFile)
                },
                EmitWgsl = values.ContainsKey("emit-wgsl"),
                OptimizationLevel = int.Parse(
                    values.GetValueOrDefault("optimization") ?? "2",
                    System.Globalization.CultureInfo.InvariantCulture),
                LlvmPasses = passes,
                EmitLlvmIr = !values.ContainsKey("no-llvm-ir")
            };
            var compiler = new SpirvCompiler();
            var artifacts = variantsFile is null
                ? compiler.CompileAssembly(request)
                : compiler.CompileVariants(request, SpirvVariantConfiguration.Load(variantsFile).Targets);
            foreach (var artifact in artifacts) {
                var state = artifact.CacheHit ? "cached" : "compiled";
                Console.WriteLine($"SPIR-V {state}: {artifact.Kernel.QualifiedName} -> {artifact.SpirvPath}");
                if (artifact.WgslPath != null) {
                    Console.WriteLine($"WGSL {state}: {artifact.Kernel.QualifiedName} -> {artifact.WgslPath}");
                }
            }
            if (variantsFile is not null) {
                var binaries = artifacts.Select(static artifact => artifact.SpirvPath).Distinct(StringComparer.Ordinal).ToArray();
                Console.WriteLine($"SPIR-V variants: {artifacts.Count} logical artifacts, {binaries.Length} unique binaries, " +
                    $"{binaries.Sum(static path => new FileInfo(path).Length)} bytes.");
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
            InvalidDataException or
            IOException or
            ShaderException or
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
            if (name is "emit-wgsl" or "no-llvm-ir") {
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

    private static SpirvKernelAbi ParseKernelAbi(string value) => value switch {
        "vulkan" => SpirvKernelAbi.Vulkan,
        "webgpu" => SpirvKernelAbi.WebGpu,
        _ => throw new ArgumentException(
            $"SPIR-V kernel ABI '{value}' is not supported. Use 'vulkan' or 'webgpu'.")
    };

    private static void WriteUsage()
    {
        Console.WriteLine("Usage: sia-spirv translate --input <file.spv|file.wgsl> --output <file.wgsl|file.spv>");
        Console.WriteLine("       sia-spirv validate --input <file.spv|file.wgsl>");
        Console.WriteLine(
            "Usage: sia-spirv compile --assembly <path> --output <directory> " +
            "[--toolchain <directory>] [--target vulkan1.2|vulkan1.3] " +
            "[--abi vulkan|webgpu] [--emit-wgsl] " +
            "[--optimization 0..3] [--passes <pipeline>] [--passes-json <path>] " +
            "[--target-profile-json <path> | --variants-json <path>] " +
            "[--no-llvm-ir]");
    }
}
