using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Sia.Spirv;
using Sia.Spirv.Compiler.Analysis;
using Sia.Spirv.Compiler.Diagnostics;
using Sia.Spirv.Compiler.IL;
using Sia.Spirv.Compiler.Metadata;
using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler;

public sealed class SpirvFrontend
{
    private const string k_KernelAttributeName = "Sia.Spirv.SpirvKernelAttribute";
    private const string k_VertexAttributeName = "Sia.Spirv.SpirvVertexShaderAttribute";
    private const string k_FragmentAttributeName = "Sia.Spirv.SpirvFragmentShaderAttribute";

    public SpirvFrontendResult Analyze(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);

        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream, PEStreamOptions.PrefetchEntireImage);
        if (!peReader.HasMetadata) {
            throw new BadImageFormatException(
                $"'{assemblyPath}' does not contain managed metadata.");
        }

        var reader = peReader.GetMetadataReader();
        using var intrinsics = IntrinsicCatalog.Open(assemblyPath);
        var resolver = new CilCallResolver(reader, intrinsics);
        var kernels = new List<SpirvKernel>();
        var diagnostics = new List<SpirvDiagnostic>();
        foreach (var typeHandle in reader.TypeDefinitions) {
            var type = reader.GetTypeDefinition(typeHandle);
            var declaringType = MetadataNames.GetTypeName(reader, typeHandle);
            foreach (var methodHandle in type.GetMethods()) {
                var method = reader.GetMethodDefinition(methodHandle);
                if (!TryGetShaderDeclaration(
                    reader,
                    method,
                    out var stage,
                    out var workgroupSize)) {
                    continue;
                }

                var methodName = reader.GetString(method.Name);
                var qualifiedName = $"{declaringType}.{methodName}";
                if (!ValidateKernelDeclaration(
                    reader,
                    method,
                    qualifiedName,
                    stage,
                    workgroupSize,
                    diagnostics)) {
                    continue;
                }

                try {
                    var body = peReader.GetMethodBody(method.RelativeVirtualAddress);
                    var il = body.GetILBytes() ?? throw new InvalidDataException(
                        "The kernel method body does not contain CIL bytes.");
                    var instructions = CilInstructionDecoder.Decode(il);
                    var graph = CilControlFlowGraph.Create(instructions, il.Length);
                    var view = new ShaderCilView(graph, resolver);
                    new CilStackAnalyzer(reader, methodHandle).Validate(view);

                    if (body.ExceptionRegions.Length != 0) {
                        diagnostics.Add(new SpirvDiagnostic(
                            SpirvDiagnosticIds.ExceptionHandling,
                            SpirvDiagnosticSeverity.Error,
                            "Exception regions are not supported inside a SPIR-V kernel.",
                            qualifiedName));
                    }

                    SpirvLegalityAnalyzer.Analyze(qualifiedName, view, diagnostics);
                    kernels.Add(new SpirvKernel(
                        declaringType,
                        methodName,
                        MetadataTokens.GetToken(methodHandle),
                        stage,
                        workgroupSize,
                        DecodeParameters(reader, method),
                        graph));
                }
                catch (InvalidDataException exception) {
                    diagnostics.Add(new SpirvDiagnostic(
                        SpirvDiagnosticIds.InvalidControlFlow,
                        SpirvDiagnosticSeverity.Error,
                        exception.Message,
                        qualifiedName));
                }
            }
        }

        return new SpirvFrontendResult(kernels, diagnostics);
    }

    private static bool TryGetShaderDeclaration(
        MetadataReader reader,
        MethodDefinition method,
        out SpirvShaderStage stage,
        out SpirvWorkgroupSize workgroupSize)
    {
        SpirvShaderStage? declaredStage = null;
        workgroupSize = new SpirvWorkgroupSize(1, 1, 1);
        foreach (var attributeHandle in method.GetCustomAttributes()) {
            var attribute = reader.GetCustomAttribute(attributeHandle);
            var attributeName = MetadataNames.GetAttributeTypeName(reader, attribute);
            SpirvShaderStage currentStage;
            if (attributeName == k_KernelAttributeName) {
                currentStage = SpirvShaderStage.Compute;
                var value = attribute.DecodeValue(new CustomAttributeTypeProvider());
                if (value.FixedArguments.Length != 3) {
                    throw new BadImageFormatException(
                        "SpirvKernelAttribute must contain three fixed arguments.");
                }
                workgroupSize = new SpirvWorkgroupSize(
                    Convert.ToUInt32(value.FixedArguments[0].Value),
                    Convert.ToUInt32(value.FixedArguments[1].Value),
                    Convert.ToUInt32(value.FixedArguments[2].Value));
            }
            else if (attributeName == k_VertexAttributeName) {
                currentStage = SpirvShaderStage.Vertex;
            }
            else if (attributeName == k_FragmentAttributeName) {
                currentStage = SpirvShaderStage.Fragment;
            }
            else {
                continue;
            }

            if (declaredStage != null) {
                throw new BadImageFormatException(
                    "A SPIR-V shader method must declare exactly one shader stage attribute.");
            }
            declaredStage = currentStage;
        }

        stage = declaredStage.GetValueOrDefault();
        return declaredStage != null;
    }

    private static bool ValidateKernelDeclaration(
        MetadataReader reader,
        MethodDefinition method,
        string qualifiedName,
        SpirvShaderStage stage,
        SpirvWorkgroupSize workgroupSize,
        ICollection<SpirvDiagnostic> diagnostics)
    {
        var valid = true;
        var signature = method.DecodeSignature(SignatureTypeProvider.Instance, genericContext: null);
        if ((method.Attributes & MethodAttributes.Static) == 0 || !signature.ReturnType.IsVoid) {
            diagnostics.Add(new SpirvDiagnostic(
                SpirvDiagnosticIds.InvalidKernelSignature,
                SpirvDiagnosticSeverity.Error,
                "A SPIR-V shader must be a static method returning void.",
                qualifiedName));
            valid = false;
        }

        if (method.RelativeVirtualAddress == 0) {
            diagnostics.Add(new SpirvDiagnostic(
                SpirvDiagnosticIds.InvalidKernelSignature,
                SpirvDiagnosticSeverity.Error,
                "A SPIR-V shader must have a CIL method body.",
                qualifiedName));
            valid = false;
        }

        if (stage == SpirvShaderStage.Compute &&
            (workgroupSize.X == 0 || workgroupSize.Y == 0 || workgroupSize.Z == 0)) {
            diagnostics.Add(new SpirvDiagnostic(
                SpirvDiagnosticIds.InvalidWorkgroupSize,
                SpirvDiagnosticSeverity.Error,
                "SPIR-V workgroup dimensions must be greater than zero.",
                qualifiedName));
            valid = false;
        }

        return valid;
    }

    private static IReadOnlyList<SpirvKernelParameter> DecodeParameters(
        MetadataReader reader,
        MethodDefinition method)
    {
        var signature = method.DecodeSignature(new KernelTypeProvider(), genericContext: null);
        var names = method.GetParameters()
            .Select(reader.GetParameter)
            .Where(static parameter => parameter.SequenceNumber != 0)
            .OrderBy(static parameter => parameter.SequenceNumber)
            .Select(parameter => reader.GetString(parameter.Name))
            .ToArray();
        var parameters = new SpirvKernelParameter[signature.ParameterTypes.Length];
        for (var position = 0; position < signature.ParameterTypes.Length; position++) {
            var type = signature.ParameterTypes[position];
            var (kind, scalarType, structLayout) = DecodeParameterType(reader, type);
            parameters[position] = new SpirvKernelParameter(
                position < names.Length ? names[position] : $"arg{position}",
                position,
                kind,
                scalarType,
                structLayout);
        }
        return parameters;
    }

    private static (
        SpirvKernelParameterKind Kind,
        SpirvScalarType ScalarType,
        SpirvStructLayout? StructLayout)
        DecodeParameterType(MetadataReader reader, KernelType type)
    {
        if (type.Name == "Sia.Spirv.Texture2D") {
            return (SpirvKernelParameterKind.SampledTexture2D, SpirvScalarType.Float32, null);
        }
        if (type.Name == "Sia.Spirv.Texture2DArray") {
            return (SpirvKernelParameterKind.SampledTexture2DArray, SpirvScalarType.Float32, null);
        }
        if (type.Name == "Sia.Spirv.Sampler") {
            return (SpirvKernelParameterKind.Sampler, SpirvScalarType.Float32, null);
        }
        if (type.Name == "Sia.Spirv.ReadOnlyStorageBuffer`1" && type.ElementType != null) {
            var (elementType, layout) = DecodeBufferElementType(reader, type.ElementType);
            return (SpirvKernelParameterKind.ReadOnlyStorageBuffer, elementType, layout);
        }
        if (type.Name == "Sia.Spirv.StorageBuffer`1" && type.ElementType != null) {
            var (elementType, layout) = DecodeBufferElementType(reader, type.ElementType);
            return (SpirvKernelParameterKind.StorageBuffer, elementType, layout);
        }
        if (type.Name == "Sia.Spirv.WorkgroupMemory`1" && type.ElementType != null) {
            return (
                SpirvKernelParameterKind.WorkgroupMemory,
                DecodeScalarType(type.ElementType),
                null);
        }
        return (SpirvKernelParameterKind.PushConstant, DecodePushConstantType(type), null);
    }

    private static (SpirvScalarType Type, SpirvStructLayout? Layout)
        DecodeBufferElementType(MetadataReader reader, KernelType type)
    {
        if (TryDecodeScalarType(type, out var scalarType)) {
            return (scalarType, null);
        }
        return (SpirvScalarType.Struct, DecodeStructLayout(reader, type.Name));
    }

    private static SpirvStructLayout DecodeStructLayout(MetadataReader reader, string typeName)
    {
        var typeHandle = reader.TypeDefinitions.FirstOrDefault(handle =>
            MetadataNames.GetTypeName(reader, handle) == typeName);
        if (typeHandle.IsNil) {
            throw new InvalidDataException(
                $"Storage-buffer struct '{typeName}' must be declared in the shader assembly.");
        }
        var definition = reader.GetTypeDefinition(typeHandle);
        if ((definition.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.SequentialLayout) {
            throw new InvalidDataException(
                $"Storage-buffer struct '{typeName}' must use sequential layout.");
        }

        var fields = new List<SpirvStructField>();
        var offset = 0;
        var structAlignment = 1;
        foreach (var fieldHandle in definition.GetFields()) {
            var field = reader.GetFieldDefinition(fieldHandle);
            if ((field.Attributes & FieldAttributes.Static) != 0) {
                continue;
            }
            var fieldType = field.DecodeSignature(new KernelTypeProvider(), genericContext: null);
            if (!TryDecodeScalarType(fieldType, out var scalarType)) {
                throw new InvalidDataException(
                    $"Storage-buffer struct field '{typeName}.{reader.GetString(field.Name)}' " +
                    $"has unsupported type '{fieldType.Name}'.");
            }
            var alignment = GetTypeAlignment(scalarType);
            var size = GetTypeSize(scalarType);
            offset = AlignUp(offset, alignment);
            fields.Add(new SpirvStructField(
                reader.GetString(field.Name), scalarType, offset, alignment, size));
            offset += size;
            structAlignment = Math.Max(structAlignment, alignment);
        }
        if (fields.Count == 0) {
            throw new InvalidDataException($"Storage-buffer struct '{typeName}' has no instance fields.");
        }
        var sizeAligned = AlignUp(offset, structAlignment);
        return new SpirvStructLayout(
            typeName,
            structAlignment,
            sizeAligned,
            sizeAligned,
            fields);
    }

    private static SpirvScalarType DecodePushConstantType(KernelType type) => type.Name switch {
        "System.Int32" => SpirvScalarType.Int32,
        "System.UInt32" => SpirvScalarType.UInt32,
        "System.Single" => SpirvScalarType.Float32,
        _ => throw new InvalidDataException(
            $"Push-constant type '{type.Name}' is not supported; use a storage buffer for vector data.")
    };

    private static SpirvScalarType DecodeScalarType(KernelType type) =>
        TryDecodeScalarType(type, out var scalarType)
            ? scalarType
            : throw new InvalidDataException($"Kernel parameter type '{type.Name}' is not supported.");

    private static bool TryDecodeScalarType(KernelType type, out SpirvScalarType scalarType)
    {
        scalarType = type.Name switch {
            "System.Int32" => SpirvScalarType.Int32,
            "System.UInt32" => SpirvScalarType.UInt32,
            "System.Single" => SpirvScalarType.Float32,
            "Sia.Math.int2" => SpirvScalarType.Int32x2,
            "Sia.Math.int3" => SpirvScalarType.Int32x3,
            "Sia.Math.int4" => SpirvScalarType.Int32x4,
            "Sia.Math.uint2" => SpirvScalarType.UInt32x2,
            "Sia.Math.uint3" => SpirvScalarType.UInt32x3,
            "Sia.Math.uint4" => SpirvScalarType.UInt32x4,
            "Sia.Math.float2" => SpirvScalarType.Float32x2,
            "Sia.Math.float3" => SpirvScalarType.Float32x3,
            "Sia.Math.float4" => SpirvScalarType.Float32x4,
            _ => SpirvScalarType.Struct
        };
        return scalarType != SpirvScalarType.Struct;
    }

    private static int GetTypeAlignment(SpirvScalarType type) => type switch {
        SpirvScalarType.Int32 or SpirvScalarType.UInt32 or SpirvScalarType.Float32 => 4,
        SpirvScalarType.Int32x2 or SpirvScalarType.UInt32x2 or SpirvScalarType.Float32x2 => 8,
        _ => 16
    };

    private static int GetTypeSize(SpirvScalarType type) => type switch {
        SpirvScalarType.Int32 or SpirvScalarType.UInt32 or SpirvScalarType.Float32 => 4,
        SpirvScalarType.Int32x2 or SpirvScalarType.UInt32x2 or SpirvScalarType.Float32x2 => 8,
        SpirvScalarType.Int32x3 or SpirvScalarType.UInt32x3 or SpirvScalarType.Float32x3 => 12,
        _ => 16
    };

    private static int AlignUp(int value, int alignment) =>
        checked((value + alignment - 1) / alignment * alignment);
}
