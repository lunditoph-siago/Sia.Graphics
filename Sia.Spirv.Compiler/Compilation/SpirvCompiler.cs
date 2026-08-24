using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sia.Spirv.Compiler.Diagnostics;
using Sia.Spirv.Compiler.LLVM;
using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler.Compilation;

public sealed class SpirvCompiler
{
    private static readonly JsonSerializerOptions _jsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public IReadOnlyList<SpirvArtifact> CompileAssembly(
        string assemblyPath,
        string outputDirectory,
        SpirvCompilationOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        options ??= new SpirvCompilationOptions();

        var frontend = new SpirvFrontend().Analyze(assemblyPath);
        var errors = frontend.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == SpirvDiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length != 0) {
            throw new SpirvCompilationException(errors);
        }
        if (frontend.Kernels.Count == 0) {
            return [];
        }

        var toolchain = LlvmToolchain.Locate(options.ToolchainDirectory);
        var llvmVersion = toolchain.GetLlvmVersion();
        var spirvToolsVersion = toolchain.GetSpirvToolsVersion();
        var assemblyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath)));
        Directory.CreateDirectory(outputDirectory);

        var artifacts = new List<SpirvArtifact>(frontend.Kernels.Count);
        foreach (var kernel in frontend.Kernels) {
            var fileName = SanitizeFileName(kernel.QualifiedName);
            var llvmPath = Path.Combine(outputDirectory, $"{fileName}.ll");
            var rawLlvmPath = Path.Combine(outputDirectory, $"{fileName}.raw.ll");
            var spirvPath = Path.Combine(outputDirectory, $"{fileName}.spv");
            var manifestPath = Path.Combine(outputDirectory, $"{fileName}.spv.json");
            var sourceHash = ComputeSourceHash(
                assemblyHash,
                kernel,
                options,
                llvmVersion,
                spirvToolsVersion);
            if (IsCacheHit(manifestPath, spirvPath, llvmPath, sourceHash, options.EmitLlvmIr)) {
                artifacts.Add(new SpirvArtifact(
                    kernel,
                    spirvPath,
                    manifestPath,
                    options.EmitLlvmIr ? llvmPath : null,
                    true));
                continue;
            }

            var module = new LlvmIrEmitter().Emit(assemblyPath, kernel);
            File.WriteAllText(rawLlvmPath, module.Text, new UTF8Encoding(false));
            try {
                toolchain.Optimize(rawLlvmPath, llvmPath);
                toolchain.Compile(
                    llvmPath,
                    spirvPath,
                    options.OptimizationLevel,
                    options.TargetEnvironment);
                toolchain.Validate(spirvPath, options.TargetEnvironment);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException) {
                throw new SpirvCompilationException(
                    $"Failed to compile '{kernel.QualifiedName}':{Environment.NewLine}{exception.Message}");
            }
            finally {
                File.Delete(rawLlvmPath);
            }

            if (!options.EmitLlvmIr) {
                File.Delete(llvmPath);
            }
            var manifest = CreateManifest(
                kernel,
                options,
                llvmVersion,
                spirvToolsVersion,
                spirvPath,
                sourceHash);
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(manifest, _jsonOptions) + Environment.NewLine,
                new UTF8Encoding(false));
            artifacts.Add(new SpirvArtifact(
                kernel,
                spirvPath,
                manifestPath,
                options.EmitLlvmIr ? llvmPath : null,
                false));
        }
        return artifacts;
    }

    private static SpirvArtifactManifest CreateManifest(
        SpirvKernel kernel,
        SpirvCompilationOptions options,
        string llvmVersion,
        string spirvToolsVersion,
        string spirvPath,
        string sourceHash)
    {
        var resources = new List<SpirvManifestResource>();
        var pushConstants = new List<SpirvManifestPushConstant>();
        var binding = 0;
        var offset = 0;
        foreach (var parameter in kernel.Parameters) {
            if (parameter.Kind == SpirvKernelParameterKind.StorageBuffer) {
                resources.Add(new SpirvManifestResource(
                    parameter.Name,
                    "storage-buffer",
                    "read-write",
                    GetScalarName(parameter.ScalarType),
                    0,
                    binding++));
            }
            else {
                pushConstants.Add(new SpirvManifestPushConstant(
                    parameter.Name,
                    GetScalarName(parameter.ScalarType),
                    offset,
                    4));
                offset += 4;
            }
        }
        return new SpirvArtifactManifest(
            1,
            kernel.Name,
            kernel.QualifiedName,
            kernel.MetadataToken,
            new SpirvManifestWorkgroupSize(
                kernel.WorkgroupSize.X,
                kernel.WorkgroupSize.Y,
                kernel.WorkgroupSize.Z),
            options.TargetEnvironment,
            ReadSpirvVersion(spirvPath),
            resources,
            pushConstants,
            new SpirvManifestToolchain(llvmVersion, spirvToolsVersion),
            sourceHash);
    }

    private static bool IsCacheHit(
        string manifestPath,
        string spirvPath,
        string llvmPath,
        string sourceHash,
        bool emitLlvmIr)
    {
        if (!File.Exists(manifestPath) || !File.Exists(spirvPath) ||
            emitLlvmIr && !File.Exists(llvmPath)) {
            return false;
        }
        try {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return document.RootElement.GetProperty("sourceHash").GetString() == sourceHash;
        }
        catch (JsonException) {
            return false;
        }
    }

    private static string ComputeSourceHash(
        string assemblyHash,
        SpirvKernel kernel,
        SpirvCompilationOptions options,
        string llvmVersion,
        string spirvToolsVersion)
    {
        var compilerVersion = typeof(SpirvCompiler).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
        var input = string.Join(
            '|',
            assemblyHash,
            kernel.MetadataToken,
            compilerVersion,
            options.TargetEnvironment,
            options.OptimizationLevel,
            options.EmitLlvmIr,
            llvmVersion,
            spirvToolsVersion);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private static string ReadSpirvVersion(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        stream.ReadExactly(header);
        var magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic != 0x07230203) {
            throw new InvalidDataException($"'{path}' does not contain a SPIR-V module.");
        }
        var version = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        return $"{(version >> 16) & 0xff}.{(version >> 8) & 0xff}";
    }

    private static string GetScalarName(SpirvScalarType type) => type switch {
        SpirvScalarType.Int32 => "int32",
        SpirvScalarType.UInt32 => "uint32",
        SpirvScalarType.Float32 => "float32",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}
