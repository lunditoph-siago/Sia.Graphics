using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Sia.Spirv;
using Sia.Spirv.Compiler.Analysis;
using Sia.Spirv.Compiler.Diagnostics;
using Sia.Spirv.Compiler.IL;
using Sia.Spirv.Compiler.Legalization;
using Sia.Spirv.Compiler.Metadata;
using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler;

public sealed class SpirvFrontend
{
    private const string k_KernelAttributeName = "Sia.Spirv.SpirvKernelAttribute";
    private const string k_VertexAttributeName = "Sia.Spirv.SpirvVertexShaderAttribute";
    private const string k_FragmentAttributeName = "Sia.Spirv.SpirvFragmentShaderAttribute";
    private const string k_LocationAttributeName = "Sia.Spirv.LocationAttribute";
    private const string k_PositionAttributeName = "Sia.Spirv.PositionAttribute";
    private const string k_VertexIndexAttributeName = "Sia.Spirv.VertexIndexAttribute";
    private const string k_InstanceIndexAttributeName = "Sia.Spirv.InstanceIndexAttribute";
    private const string k_FragmentPositionAttributeName = "Sia.Spirv.FragmentPositionAttribute";
    private const string k_FrontFacingAttributeName = "Sia.Spirv.FrontFacingAttribute";
    private const string k_FragmentDepthAttributeName = "Sia.Spirv.FragmentDepthAttribute";
    private const string k_FlatAttributeName = "Sia.Spirv.FlatAttribute";
    private const string k_InterpolateAttributeName = "Sia.Spirv.InterpolateAttribute";
    private const string k_BufferLengthAttributeName = "Sia.Spirv.SpirvBufferLengthAttribute";

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

                    var parameters = DecodeParameters(reader, method, stage);
                    var returnLayout = DecodeReturnLayout(reader, method, stage);
                    SpirvLegalityAnalyzer.Analyze(
                        qualifiedName,
                        view,
                        diagnostics,
                        returnLayout?.Name);
                    kernels.Add(new SpirvKernel(
                        declaringType,
                        methodName,
                        MetadataTokens.GetToken(methodHandle),
                        stage,
                        workgroupSize,
                        parameters,
                        returnLayout,
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
        if ((method.Attributes & MethodAttributes.Static) == 0 ||
            stage == SpirvShaderStage.Compute && !signature.ReturnType.IsVoid) {
            diagnostics.Add(new SpirvDiagnostic(
                SpirvDiagnosticIds.InvalidKernelSignature,
                SpirvDiagnosticSeverity.Error,
                stage == SpirvShaderStage.Compute
                    ? "A compute shader must be a static method returning void."
                    : "A raster shader must be a static method.",
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
        MethodDefinition method,
        SpirvShaderStage stage)
    {
        var signature = method.DecodeSignature(new KernelTypeProvider(), genericContext: null);
        var parameterDefinitions = method.GetParameters()
            .Select(reader.GetParameter)
            .Where(static parameter => parameter.SequenceNumber != 0)
            .OrderBy(static parameter => parameter.SequenceNumber)
            .ToArray();
        var parameters = new SpirvKernelParameter[signature.ParameterTypes.Length];
        for (var position = 0; position < signature.ParameterTypes.Length; position++) {
            var type = signature.ParameterTypes[position];
            var (kind, scalarType, physicalLayout, stageIoLayout) =
                DecodeParameterType(reader, type, stage);
            var definition = parameterDefinitions[position];
            var bufferLength = DecodeBufferLength(reader, definition);
            if (bufferLength != null &&
                kind != SpirvKernelParameterKind.ReadOnlyStorageBuffer) {
                throw new InvalidDataException(
                    $"SpirvBufferLengthAttribute can only annotate a read-only storage buffer, " +
                    $"not '{reader.GetString(definition.Name)}'.");
            }
            parameters[position] = new SpirvKernelParameter(
                reader.GetString(definition.Name),
                position,
                kind,
                scalarType,
                physicalLayout,
                stageIoLayout,
                bufferLength);
        }
        if (parameters.Count(static parameter =>
            parameter.Kind == SpirvKernelParameterKind.StageInput) > 1) {
            throw new InvalidDataException("A raster shader can declare only one stage-input parameter.");
        }
        return parameters;
    }

    private static int? DecodeBufferLength(
        MetadataReader reader,
        Parameter definition)
    {
        int? length = null;
        foreach (var attributeHandle in definition.GetCustomAttributes()) {
            var attribute = reader.GetCustomAttribute(attributeHandle);
            if (MetadataNames.GetAttributeTypeName(reader, attribute) !=
                k_BufferLengthAttributeName) {
                continue;
            }
            if (length != null) {
                throw new InvalidDataException(
                    "A shader parameter cannot declare multiple SpirvBufferLength attributes.");
            }
            var value = attribute.DecodeValue(new CustomAttributeTypeProvider());
            length = Convert.ToInt32(value.FixedArguments.Single().Value);
            if (length <= 0) {
                throw new InvalidDataException("A shader buffer length must be greater than zero.");
            }
        }
        return length;
    }

    private static (
        SpirvKernelParameterKind Kind,
        SpirvScalarType ScalarType,
        PhysicalStructLayout? PhysicalLayout,
        SpirvStageIoLayout? StageIoLayout)
        DecodeParameterType(MetadataReader reader, KernelType type, SpirvShaderStage stage)
    {
        if (TryDecodeStageIoLayout(reader, type.Name, stage, true, out var stageIoLayout)) {
            return (
                SpirvKernelParameterKind.StageInput,
                SpirvScalarType.Struct,
                null,
                stageIoLayout);
        }
        if (type.Name == "Sia.Spirv.Texture2D") {
            return (SpirvKernelParameterKind.SampledTexture2D, SpirvScalarType.Float32, null, null);
        }
        if (type.Name == "Sia.Spirv.Texture2DArray") {
            return (SpirvKernelParameterKind.SampledTexture2DArray, SpirvScalarType.Float32, null, null);
        }
        if (type.Name == "Sia.Spirv.Sampler") {
            return (SpirvKernelParameterKind.Sampler, SpirvScalarType.Float32, null, null);
        }
        if (type.Name == "Sia.Spirv.ReadOnlyStorageBuffer`1" && type.ElementType != null) {
            var (elementType, layout) = DecodeBufferElementType(reader, type.ElementType);
            return (SpirvKernelParameterKind.ReadOnlyStorageBuffer, elementType, layout, null);
        }
        if (type.Name == "Sia.Spirv.StorageBuffer`1" && type.ElementType != null) {
            var (elementType, layout) = DecodeBufferElementType(reader, type.ElementType);
            return (SpirvKernelParameterKind.StorageBuffer, elementType, layout, null);
        }
        if (type.Name == "Sia.Spirv.WorkgroupMemory`1" && type.ElementType != null) {
            return (
                SpirvKernelParameterKind.WorkgroupMemory,
                DecodeScalarType(type.ElementType),
                null,
                null);
        }
        return (SpirvKernelParameterKind.PushConstant, DecodePushConstantType(type), null, null);
    }

    private static SpirvStageIoLayout? DecodeReturnLayout(
        MetadataReader reader,
        MethodDefinition method,
        SpirvShaderStage stage)
    {
        var returnType = method.DecodeSignature(new KernelTypeProvider(), genericContext: null).ReturnType;
        if (returnType.Name == "System.Void") {
            return null;
        }
        if (stage == SpirvShaderStage.Compute) {
            throw new InvalidDataException("Compute shaders cannot return stage outputs.");
        }
        if (!TryDecodeStageIoLayout(reader, returnType.Name, stage, false, out var layout)) {
            throw new InvalidDataException(
                $"Raster-shader return type '{returnType.Name}' does not declare stage-output semantics.");
        }
        return layout;
    }

    private static bool TryDecodeStageIoLayout(
        MetadataReader reader,
        string typeName,
        SpirvShaderStage stage,
        bool input,
        out SpirvStageIoLayout? layout)
    {
        layout = null;
        var typeHandle = reader.TypeDefinitions.FirstOrDefault(handle =>
            MetadataNames.GetTypeName(reader, handle) == typeName);
        if (typeHandle.IsNil) {
            return false;
        }

        var definition = reader.GetTypeDefinition(typeHandle);
        var fields = new List<SpirvStageIoField>();
        var unannotatedFields = new List<string>();
        var hasSemantic = false;
        foreach (var fieldHandle in definition.GetFields()) {
            var field = reader.GetFieldDefinition(fieldHandle);
            if ((field.Attributes & FieldAttributes.Static) != 0) {
                continue;
            }

            SpirvStageIoKind? kind = null;
            uint? location = null;
            InterpolationMode? interpolation = null;
            InterpolationSampling? sampling = null;
            foreach (var attributeHandle in field.GetCustomAttributes()) {
                var attribute = reader.GetCustomAttribute(attributeHandle);
                var attributeName = MetadataNames.GetAttributeTypeName(reader, attribute);
                var currentKind = attributeName switch {
                    k_LocationAttributeName => SpirvStageIoKind.Location,
                    k_PositionAttributeName => SpirvStageIoKind.Position,
                    k_VertexIndexAttributeName => SpirvStageIoKind.VertexIndex,
                    k_InstanceIndexAttributeName => SpirvStageIoKind.InstanceIndex,
                    k_FragmentPositionAttributeName => SpirvStageIoKind.FragmentPosition,
                    k_FrontFacingAttributeName => SpirvStageIoKind.FrontFacing,
                    k_FragmentDepthAttributeName => SpirvStageIoKind.FragmentDepth,
                    _ => (SpirvStageIoKind?)null
                };
                if (currentKind != null) {
                    if (kind != null) {
                        throw new InvalidDataException(
                            $"Stage-I/O field '{typeName}.{reader.GetString(field.Name)}' has multiple semantics.");
                    }
                    kind = currentKind;
                    hasSemantic = true;
                    if (currentKind == SpirvStageIoKind.Location) {
                        var value = attribute.DecodeValue(new CustomAttributeTypeProvider());
                        location = Convert.ToUInt32(value.FixedArguments[0].Value);
                    }
                }
                else if (attributeName == k_FlatAttributeName) {
                    if (interpolation != null) {
                        throw new InvalidDataException(
                            $"Stage-I/O field '{typeName}.{reader.GetString(field.Name)}' has multiple interpolation attributes.");
                    }
                    interpolation = InterpolationMode.Flat;
                    sampling = InterpolationSampling.Center;
                }
                else if (attributeName == k_InterpolateAttributeName) {
                    if (interpolation != null) {
                        throw new InvalidDataException(
                            $"Stage-I/O field '{typeName}.{reader.GetString(field.Name)}' has multiple interpolation attributes.");
                    }
                    var value = attribute.DecodeValue(new CustomAttributeTypeProvider());
                    interpolation = (InterpolationMode)Convert.ToInt32(value.FixedArguments[0].Value);
                    sampling = (InterpolationSampling)Convert.ToInt32(value.FixedArguments[1].Value);
                    if (!Enum.IsDefined(interpolation.Value) || !Enum.IsDefined(sampling.Value)) {
                        throw new InvalidDataException(
                            $"Stage-I/O field '{typeName}.{reader.GetString(field.Name)}' has invalid interpolation values.");
                    }
                }
            }

            if (kind == null) {
                if (interpolation != null) {
                    throw new InvalidDataException(
                        $"Stage-I/O field '{typeName}.{reader.GetString(field.Name)}' uses interpolation without Location.");
                }
                unannotatedFields.Add(reader.GetString(field.Name));
                continue;
            }

            var fieldType = field.DecodeSignature(new KernelTypeProvider(), genericContext: null);
            if (!TryDecodeScalarType(fieldType, out var scalarType)) {
                throw new InvalidDataException(
                    $"Stage-I/O field '{typeName}.{reader.GetString(field.Name)}' has unsupported type '{fieldType.Name}'.");
            }
            if (kind == SpirvStageIoKind.Location &&
                interpolation == null &&
                IsIntegerType(scalarType) &&
                (stage == SpirvShaderStage.Vertex && !input ||
                 stage == SpirvShaderStage.Fragment && input)) {
                interpolation = InterpolationMode.Flat;
                sampling = InterpolationSampling.Center;
            }
            ValidateStageIoField(typeName, reader.GetString(field.Name), stage, input,
                kind.Value, scalarType, interpolation, sampling);
            fields.Add(new SpirvStageIoField(
                reader.GetString(field.Name),
                MetadataTokens.GetToken(fieldHandle),
                kind.Value,
                scalarType,
                location,
                interpolation,
                sampling));
        }

        if (!hasSemantic) {
            return false;
        }
        if (unannotatedFields.Count != 0) {
            throw new InvalidDataException(
                $"Every instance field in stage-I/O struct '{typeName}' must declare a semantic; " +
                $"missing: {string.Join(", ", unannotatedFields)}.");
        }
        if (fields.Count == 0) {
            throw new InvalidDataException($"Stage-I/O struct '{typeName}' has no semantic fields.");
        }
        var duplicateLocation = fields
            .Where(static field => field.Kind == SpirvStageIoKind.Location)
            .GroupBy(static field => field.Location)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateLocation != null) {
            throw new InvalidDataException(
                $"Stage-I/O struct '{typeName}' declares location {duplicateLocation.Key} more than once.");
        }
        var duplicateBuiltin = fields
            .Where(static field => field.Kind != SpirvStageIoKind.Location)
            .GroupBy(static field => field.Kind)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateBuiltin != null) {
            throw new InvalidDataException(
                $"Stage-I/O struct '{typeName}' declares {duplicateBuiltin.Key} more than once.");
        }
        if (!input && stage == SpirvShaderStage.Vertex &&
            fields.All(static field => field.Kind != SpirvStageIoKind.Position)) {
            throw new InvalidDataException(
                $"Vertex output '{typeName}' must declare one Position field.");
        }
        layout = new SpirvStageIoLayout(typeName, fields);
        return true;
    }

    private static void ValidateStageIoField(
        string typeName,
        string fieldName,
        SpirvShaderStage stage,
        bool input,
        SpirvStageIoKind kind,
        SpirvScalarType type,
        InterpolationMode? interpolation,
        InterpolationSampling? sampling)
    {
        var validSemantic = (stage, input, kind) switch {
            (SpirvShaderStage.Vertex, true, SpirvStageIoKind.Location or
                SpirvStageIoKind.VertexIndex or SpirvStageIoKind.InstanceIndex) => true,
            (SpirvShaderStage.Vertex, false, SpirvStageIoKind.Location or
                SpirvStageIoKind.Position) => true,
            (SpirvShaderStage.Fragment, true, SpirvStageIoKind.Location or
                SpirvStageIoKind.FragmentPosition or SpirvStageIoKind.FrontFacing) => true,
            (SpirvShaderStage.Fragment, false, SpirvStageIoKind.Location or
                SpirvStageIoKind.FragmentDepth) => true,
            _ => false
        };
        if (!validSemantic) {
            throw new InvalidDataException(
                $"Stage-I/O semantic {kind} is invalid on '{typeName}.{fieldName}'.");
        }
        var expectedType = kind switch {
            SpirvStageIoKind.Position or SpirvStageIoKind.FragmentPosition =>
                SpirvScalarType.Float32x4,
            SpirvStageIoKind.VertexIndex or SpirvStageIoKind.InstanceIndex =>
                SpirvScalarType.UInt32,
            SpirvStageIoKind.FrontFacing => SpirvScalarType.Boolean,
            SpirvStageIoKind.FragmentDepth => SpirvScalarType.Float32,
            _ => (SpirvScalarType?)null
        };
        if (expectedType != null && type != expectedType) {
            throw new InvalidDataException(
                $"Stage-I/O field '{typeName}.{fieldName}' must have type '{expectedType.Value}'.");
        }
        if (kind == SpirvStageIoKind.Location && !IsLocationType(type)) {
            throw new InvalidDataException(
                $"Stage-I/O location field '{typeName}.{fieldName}' has unsupported type '{type}'.");
        }
        if (interpolation != null && (kind != SpirvStageIoKind.Location ||
            stage == SpirvShaderStage.Vertex && input ||
            stage == SpirvShaderStage.Fragment && !input)) {
            throw new InvalidDataException(
                $"Interpolation is valid only on vertex-output and fragment-input Location fields, " +
                $"not '{typeName}.{fieldName}'.");
        }
        if (interpolation == InterpolationMode.Flat &&
            sampling != InterpolationSampling.Center) {
            throw new InvalidDataException(
                $"Flat interpolation on '{typeName}.{fieldName}' does not accept a sampling mode.");
        }
        if (kind == SpirvStageIoKind.Location && IsIntegerType(type) &&
            (stage == SpirvShaderStage.Vertex && !input ||
             stage == SpirvShaderStage.Fragment && input) &&
            interpolation != InterpolationMode.Flat) {
            throw new InvalidDataException(
                $"Integer stage-I/O field '{typeName}.{fieldName}' must use flat interpolation.");
        }
    }

    private static (SpirvScalarType Type, PhysicalStructLayout? Layout)
        DecodeBufferElementType(MetadataReader reader, KernelType type)
    {
        if (TryDecodeScalarType(type, out var scalarType)) {
            if (IsHostShareableType(scalarType)) {
                return (scalarType, null);
            }
            throw new InvalidDataException(
                $"Storage-buffer element type '{type.Name}' is not host-shareable.");
        }
        var logicalType = DecodeStructType(reader, type.Name);
        return (
            SpirvScalarType.Struct,
            new ShaderLayoutEngine().Legalize(logicalType, ShaderAddressSpace.Storage));
    }

    private static ShaderStructType DecodeStructType(MetadataReader reader, string typeName)
    {
        var typeHandle = reader.TypeDefinitions.FirstOrDefault(handle =>
            MetadataNames.GetTypeName(reader, handle) == typeName);
        if (typeHandle.IsNil) {
            throw new InvalidDataException(
                $"Storage-buffer struct '{typeName}' must be declared in the shader assembly.");
        }
        var definition = reader.GetTypeDefinition(typeHandle);

        var fields = new List<ShaderStructField>();
        foreach (var fieldHandle in definition.GetFields()) {
            var field = reader.GetFieldDefinition(fieldHandle);
            if ((field.Attributes & FieldAttributes.Static) != 0) {
                continue;
            }
            var fieldType = field.DecodeSignature(new KernelTypeProvider(), genericContext: null);
            if (!TryDecodeScalarType(fieldType, out var scalarType) ||
                !IsHostShareableType(scalarType)) {
                throw new InvalidDataException(
                    $"Storage-buffer struct field '{typeName}.{reader.GetString(field.Name)}' " +
                    $"has unsupported type '{fieldType.Name}'.");
            }
            fields.Add(new ShaderStructField(reader.GetString(field.Name), scalarType));
        }
        if (fields.Count == 0) {
            throw new InvalidDataException($"Storage-buffer struct '{typeName}' has no instance fields.");
        }
        return new ShaderStructType(typeName, fields);
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
            "System.Boolean" => SpirvScalarType.Boolean,
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

    private static bool IsLocationType(SpirvScalarType type) =>
        type != SpirvScalarType.Boolean && type != SpirvScalarType.Struct;

    private static bool IsIntegerType(SpirvScalarType type) => type is
        SpirvScalarType.Int32 or SpirvScalarType.UInt32 or
        SpirvScalarType.Int32x2 or SpirvScalarType.Int32x3 or SpirvScalarType.Int32x4 or
        SpirvScalarType.UInt32x2 or SpirvScalarType.UInt32x3 or SpirvScalarType.UInt32x4;

    private static bool IsHostShareableType(SpirvScalarType type) =>
        type != SpirvScalarType.Boolean && type != SpirvScalarType.Struct;

}
