using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sia.Spirv.Compiler.Diagnostics;
using Sia.Spirv.Compiler.Legalization;
using Sia.Spirv.Compiler.LLVM;
using Sia.Spirv.Compiler.Model;
using Sia.Spirv.Runtime;

namespace Sia.Spirv.Compiler.Compilation;

public sealed partial class SpirvCompiler
{
    private static readonly JsonSerializerOptions s_JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public IReadOnlyList<SpirvArtifact> CompileAssembly(SpirvFileCompilationRequest request)
    {
        var artifacts = CompileAssemblyCore(request, null);
        WriteArtifactList(request.OutputDirectory, artifacts); return artifacts;
    }

    private static IReadOnlyList<SpirvArtifact> CompileAssemblyCore(SpirvFileCompilationRequest request, string? targetName)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        var target = request.Target;
        target.Validate(offline: true, wgsl: request.EmitWgsl);
        var assemblyPath = request.AssemblyPath; var outputDirectory = request.OutputDirectory;
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

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
        foreach (var kernel in frontend.Kernels)
            Translation.Legalization.ShaderTargetValidator.ValidateStage(target, kernel.Stage.ToString());

        var toolchain = LlvmToolchain.Locate(request.ToolchainDirectory);
        var llvmVersion = toolchain.GetLlvmVersion();
        var spirvToolsVersion = toolchain.GetSpirvToolsVersion();
        var translatorVersion = request.EmitWgsl ? GetShaderTranslatorVersion() : null;
        var translatorSha256 = request.EmitWgsl ? GetShaderTranslatorSha256() : null;
        var assemblyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath)));
        Directory.CreateDirectory(outputDirectory);

        var artifacts = new List<SpirvArtifact>(frontend.Kernels.Count);
        foreach (var sourceKernel in frontend.Kernels) {
            var legalizationPlan = new SpirvLegalizationPlanner().Resolve(
                sourceKernel,
                request.Target.ResourceLimits,
                request.Target.KernelAbi);
            var kernel = legalizationPlan.Kernel;
            var fileName = SanitizeFileName(kernel.QualifiedName);
            if (targetName is not null) {
                fileName = $"{targetName}--{fileName}";
            }
            var llvmPath = Path.Combine(outputDirectory, $"{fileName}.ll");
            var rawLlvmPath = Path.Combine(outputDirectory, $"{fileName}.raw.ll");
            var spirvPath = Path.Combine(outputDirectory, $"{fileName}.spv");
            var wgslPath = Path.Combine(outputDirectory, $"{fileName}.wgsl");
            var manifestPath = Path.Combine(outputDirectory, $"{fileName}.spv.json");
            if (!request.EmitWgsl) {
                File.Delete(wgslPath);
            }
            var sourceHash = ComputeSourceHash(
                assemblyHash,
                kernel,
                request,
                legalizationPlan,
                llvmVersion,
                spirvToolsVersion,
                translatorVersion,
                translatorSha256,
                target.Identity);
            var cachedSpirvPath = targetName is null ? spirvPath : GetCachedBinaryPath(manifestPath, spirvPath);
            if (IsCacheHit(
                manifestPath,
                cachedSpirvPath,
                wgslPath,
                llvmPath,
                sourceHash,
                request.EmitWgsl,
                request.EmitLlvmIr)) {
                artifacts.Add(new SpirvArtifact(
                    kernel,
                    cachedSpirvPath,
                    request.EmitWgsl ? wgslPath : null,
                    manifestPath,
                    request.EmitLlvmIr ? llvmPath : null,
                    true));
                continue;
            }
            if (request.EmitWgsl) {
                File.Delete(wgslPath);
            }

            var module = new LlvmIrEmitter().Emit(assemblyPath, kernel, request.Target.KernelAbi);
            File.WriteAllText(rawLlvmPath, module.Text, new UTF8Encoding(false));
            try {
                toolchain.Optimize(
                    rawLlvmPath,
                    llvmPath,
                    request.OptimizationLevel,
                    request.LlvmPasses);
                toolchain.Compile(
                    llvmPath,
                    spirvPath,
                    request.OptimizationLevel,
                    target,
                    kernel.Stage);
                toolchain.Validate(spirvPath, request.Target.Environment);
                if (request.Target.KernelAbi == SpirvKernelAbi.WebGpu) {
                    toolchain.OptimizeForWebGpu(spirvPath);
                }
                SpirvResourceAccessLowering.Rewrite(spirvPath, kernel);
                Translation.Legalization.ShaderTargetValidator.ValidateBinary(
                    Translation.Spirv.SpirvBinary.Parse(File.ReadAllBytes(spirvPath)), target);
                toolchain.Validate(spirvPath, request.Target.Environment);
                if (request.EmitWgsl) {
                    ConvertToWgsl(spirvPath, wgslPath, target);
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException) {
                throw new SpirvCompilationException(
                    $"Failed to compile '{kernel.QualifiedName}':{Environment.NewLine}{exception.Message}");
            }
            finally {
                File.Delete(rawLlvmPath);
            }

            if (!request.EmitLlvmIr) {
                File.Delete(llvmPath);
            }
            var spirvSha256 = ComputeFileSha256(spirvPath);
            if (targetName is not null) {
                spirvPath = StoreBinary(spirvPath, outputDirectory, spirvSha256);
            }
            var manifest = CreateManifest(
                kernel,
                request,
                legalizationPlan,
                llvmVersion,
                spirvToolsVersion,
                translatorVersion,
                translatorSha256,
                spirvPath,
                sourceHash,
                targetName,
                Path.GetRelativePath(outputDirectory, spirvPath).Replace('\\', '/'),
                spirvSha256);
            manifest = manifest with { CompilationTargetSha256 = target.Identity };
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(manifest, s_JsonOptions) + Environment.NewLine,
                new UTF8Encoding(false));
            artifacts.Add(new SpirvArtifact(
                kernel,
                spirvPath,
                request.EmitWgsl ? wgslPath : null,
                manifestPath,
                request.EmitLlvmIr ? llvmPath : null,
                false));
        }
        return artifacts;
    }

    private static SpirvArtifactManifest CreateManifest(
        SpirvKernel kernel,
        SpirvFileCompilationRequest request,
        SpirvLegalizationPlan legalizationPlan,
        string llvmVersion,
        string spirvToolsVersion,
        string? translatorVersion,
        string? translatorSha256,
        string spirvPath,
        string sourceHash,
        string? targetName,
        string spirvFile,
        string spirvSha256)
    {
        var resources = new List<SpirvManifestResource>();
        var pushConstants = new List<SpirvManifestPushConstant>();
        var binding = 0;
        var offset = 0;
        foreach (var parameter in kernel.Parameters) {
            if (parameter.Kind is SpirvKernelParameterKind.WorkgroupMemory or
                SpirvKernelParameterKind.StageInput) {
                continue;
            }
            if (parameter.Kind == SpirvKernelParameterKind.SampledTexture2D) {
                resources.Add(new SpirvManifestResource(
                    parameter.Name,
                    "sampled-texture-2d",
                    "read-only",
                    "float32",
                    0,
                    binding++,
                    0,
                    0,
                    0));
            }
            else if (parameter.Kind == SpirvKernelParameterKind.SampledTexture2DArray) {
                resources.Add(new SpirvManifestResource(
                    parameter.Name,
                    "sampled-texture-2d-array",
                    "read-only",
                    "float32",
                    0,
                    binding++,
                    0,
                    0,
                    0));
            }
            else if (parameter.Kind == SpirvKernelParameterKind.Sampler) {
                resources.Add(new SpirvManifestResource(
                    parameter.Name,
                    "sampler",
                    "read-only",
                    "float32",
                    0,
                    binding++,
                    0,
                    0,
                    0));
            }
            else if (parameter.Kind is SpirvKernelParameterKind.UniformBuffer or
                    SpirvKernelParameterKind.ReadOnlyStorageBuffer or
                    SpirvKernelParameterKind.StorageBuffer) {
                var layout = parameter.PhysicalLayout;
                resources.Add(new SpirvManifestResource(
                    parameter.Name,
                    parameter.Kind == SpirvKernelParameterKind.UniformBuffer
                        ? "uniform-buffer"
                        : "storage-buffer",
                    parameter.Kind is SpirvKernelParameterKind.ReadOnlyStorageBuffer or
                        SpirvKernelParameterKind.UniformBuffer
                        ? "read-only"
                        : "read-write",
                    layout?.LogicalType.Name ?? SpirvTypeLayout.GetName(parameter.ScalarType),
                    0,
                    binding++,
                    layout?.Alignment ?? SpirvTypeLayout.GetAlignment(parameter.ScalarType),
                    layout?.Size ?? SpirvTypeLayout.GetSize(parameter.ScalarType),
                    layout?.ArrayStride ?? SpirvTypeLayout.GetArrayStride(parameter.ScalarType),
                    layout == null ? null : CreateManifestFields(layout),
                    parameter.BufferLength));
            }
            else {
                pushConstants.Add(new SpirvManifestPushConstant(
                    parameter.Name,
                    SpirvTypeLayout.GetName(parameter.ScalarType),
                    offset,
                    4));
                offset += 4;
            }
        }
        if (request.Target.KernelAbi == SpirvKernelAbi.WebGpu && pushConstants.Count != 0) {
            var parameterVectorCount = (pushConstants.Count + 3) / 4;
            resources.Add(new SpirvManifestResource(
                "sia.parameters",
                "uniform-buffer",
                "read-only",
                "uint32x4",
                0,
                binding,
                16,
                parameterVectorCount * 16,
                16,
                ElementCount: parameterVectorCount));
        }
        var stageInputs = kernel.Parameters
            .Where(static parameter => parameter.Kind == SpirvKernelParameterKind.StageInput)
            .SelectMany(static parameter => parameter.StageIoLayout!.Fields)
            .Select(CreateManifestStageIo)
            .ToArray();
        var stageOutputs = kernel.ReturnLayout?.Fields
            .Select(CreateManifestStageIo)
            .ToArray() ?? [];
        var layoutSha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
            request.Target.KernelAbi,
            kernel.Stage,
            Resources = resources,
            PushConstants = pushConstants,
            StageInputs = stageInputs,
            StageOutputs = stageOutputs
        }, s_JsonOptions)));
        var storage = resources.Where(static resource => resource.Kind == "storage-buffer").ToArray();
        var uniforms = resources.Where(static resource => resource.Kind == "uniform-buffer").ToArray();
        var bufferRequirements = new SpirvBufferRequirements(
            storage.Length,
            uniforms.Length,
            storage.Select(GetBindingSize).DefaultIfEmpty().Max(),
            uniforms.Select(GetBindingSize).DefaultIfEmpty().Max());
        return new SpirvArtifactManifest(
            kernel.Name,
            kernel.QualifiedName,
            kernel.MetadataToken,
            new SpirvManifestWorkgroupSize(
                kernel.WorkgroupSize.X,
                kernel.WorkgroupSize.Y,
                kernel.WorkgroupSize.Z),
            request.Target.Environment,
            ReadSpirvVersion(spirvPath),
            resources,
            pushConstants,
            stageInputs,
            stageOutputs,
            new SpirvManifestToolchain(llvmVersion, spirvToolsVersion, translatorVersion, translatorSha256),
            sourceHash,
            request.Target.KernelAbi == SpirvKernelAbi.WebGpu ? "webgpu" : "vulkan",
            kernel.Stage.ToString().ToLowerInvariant(),
            request.LlvmPasses,
            legalizationPlan.StrategyIds,
            targetName,
            spirvFile,
            spirvSha256,
            layoutSha256,
            bufferRequirements);
    }

    private static ulong GetBindingSize(SpirvManifestResource resource) =>
        checked((ulong)resource.ArrayStride * (ulong)(resource.ElementCount ?? 1));

    private static IReadOnlyList<SpirvManifestStructField> CreateManifestFields(
        PhysicalStructLayout layout) =>
        layout.LogicalType.Fields.Select((field, logicalFieldIndex) => {
            var member = layout.GetLogicalMember(logicalFieldIndex);
            return new SpirvManifestStructField(
                field.Name,
                SpirvTypeLayout.GetName(field.Type),
                member.Offset,
                member.Alignment,
                member.Size);
        }).ToArray();

    private static SpirvManifestStageIo CreateManifestStageIo(SpirvStageIoField field) =>
        new(
            field.Name,
            field.Kind switch {
                SpirvStageIoKind.Location => "location",
                SpirvStageIoKind.Position => "position",
                SpirvStageIoKind.VertexIndex => "vertex-index",
                SpirvStageIoKind.InstanceIndex => "instance-index",
                SpirvStageIoKind.FragmentPosition => "fragment-position",
                SpirvStageIoKind.FrontFacing => "front-facing",
                SpirvStageIoKind.FragmentDepth => "fragment-depth",
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            },
            SpirvTypeLayout.GetName(field.Type),
            field.Location,
            field.Flat,
            field.Interpolation?.ToString().ToLowerInvariant(),
            field.Sampling?.ToString().ToLowerInvariant());

    private static bool IsCacheHit(
        string manifestPath,
        string spirvPath,
        string wgslPath,
        string llvmPath,
        string sourceHash,
        bool emitWgsl,
        bool emitLlvmIr)
    {
        if (!File.Exists(manifestPath) || !File.Exists(spirvPath) ||
            emitWgsl && !File.Exists(wgslPath) ||
            emitLlvmIr && !File.Exists(llvmPath)) {
            return false;
        }
        try {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            return root.GetProperty("sourceHash").GetString() == sourceHash &&
                (!root.TryGetProperty("spirvSha256", out var hash) ||
                    hash.ValueKind == JsonValueKind.String && hash.GetString() == ComputeFileSha256(spirvPath));
        }
        catch (JsonException) {
            return false;
        }
    }

    private static string ComputeSourceHash(
        string assemblyHash,
        SpirvKernel kernel,
        SpirvFileCompilationRequest request,
        SpirvLegalizationPlan legalizationPlan,
        string llvmVersion,
        string spirvToolsVersion,
        string? translatorVersion,
        string? translatorSha256,
        string targetIdentity)
    {
        var compilerVersion = typeof(SpirvCompiler).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
        var compilerHash = Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(typeof(SpirvCompiler).Assembly.Location)));
        var input = string.Join(
            '|',
            assemblyHash,
            kernel.MetadataToken,
            kernel.Stage,
            compilerVersion,
            compilerHash,
            request.Target.Environment,
            targetIdentity,
            request.Target.KernelAbi,
            request.EmitWgsl,
            request.OptimizationLevel,
            request.LlvmPasses,
            request.EmitLlvmIr,
            request.Target.ResourceLimits.SupportsStorageBuffers,
            request.Target.ResourceLimits.PreferUniformForBoundedReadOnlyBuffers,
            request.Target.ResourceLimits.MaxStorageBuffersPerShaderStage,
            request.Target.ResourceLimits.MaxStorageBuffersInVertexStage,
            request.Target.ResourceLimits.MaxStorageBuffersInFragmentStage,
            request.Target.ResourceLimits.MaxStorageBufferBindingSize,
            request.Target.ResourceLimits.MaxUniformBuffersPerShaderStage,
            request.Target.ResourceLimits.MaxUniformBufferBindingSize,
            string.Join(',', legalizationPlan.StrategyIds),
            llvmVersion,
            spirvToolsVersion,
            translatorVersion,
            translatorSha256);
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

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}
