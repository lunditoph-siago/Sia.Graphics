using System.Security.Cryptography;
using System.Text.Json;
using Sia.Spirv.Compiler.Legalization;
using Sia.Spirv.Compiler.Model;
using Sia.Spirv.Runtime;

namespace Sia.Spirv.Compiler.Compilation;

internal static class ShaderManifestBuilder
{

    internal static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static SpirvArtifactManifest Create(
        SpirvKernel kernel,
        SpirvFileCompilationRequest request,
        SpirvLegalizationPlan legalizationPlan,
        SpirvManifestToolchain versions,
        string spirvPath,
        string sourceHash,
        string? targetName,
        string spirvFile,
        string spirvSha256)
    {
        var resources = new List<SpirvManifestResource>();
        var pushConstants = new List<SpirvManifestPushConstant>();
        foreach (var parameter in kernel.Parameters) {
            if (parameter.Kind is SpirvKernelParameterKind.WorkgroupMemory or SpirvKernelParameterKind.StageInput) {
                continue;
            }
            if (CreateManifestResource(parameter, resources.Count) is { } resource) {
                resources.Add(resource);
            }
            else {
                pushConstants.Add(new SpirvManifestPushConstant(
                    parameter.Name,
                    SpirvTypeLayout.GetName(parameter.ScalarType),
                    pushConstants.Count * 4,
                    4));
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
                resources.Count,
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
        }, JsonOptions)));
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
            versions,
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

    private static SpirvManifestResource? CreateManifestResource(SpirvKernelParameter parameter, int binding)
    {
        var handleKind = parameter.Kind switch {
            SpirvKernelParameterKind.SampledTexture2D => "sampled-texture-2d",
            SpirvKernelParameterKind.SampledTexture2DArray => "sampled-texture-2d-array",
            SpirvKernelParameterKind.Sampler => "sampler",
            _ => null
        };
        if (handleKind is not null) {
            return new(parameter.Name, handleKind, "read-only", "float32", 0, binding, 0, 0, 0);
        }
        if (parameter.Kind is not (SpirvKernelParameterKind.UniformBuffer or
            SpirvKernelParameterKind.ReadOnlyStorageBuffer or SpirvKernelParameterKind.StorageBuffer)) {
            return null;
        }

        var layout = parameter.PhysicalLayout;
        var scalarType = parameter.ScalarType;
        return new(
            parameter.Name,
            parameter.Kind == SpirvKernelParameterKind.UniformBuffer ? "uniform-buffer" : "storage-buffer",
            parameter.Kind == SpirvKernelParameterKind.StorageBuffer ? "read-write" : "read-only",
            layout?.LogicalType.Name ?? SpirvTypeLayout.GetName(scalarType),
            0,
            binding,
            layout?.Alignment ?? SpirvTypeLayout.GetAlignment(scalarType),
            layout?.Size ?? SpirvTypeLayout.GetSize(scalarType),
            layout?.ArrayStride ?? SpirvTypeLayout.GetArrayStride(scalarType),
            layout is null ? null : CreateManifestFields(layout),
            parameter.BufferLength);
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

}
