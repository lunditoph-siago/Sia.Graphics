using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Sia.Spirv.Compiler.Analysis;
using Sia.Spirv.Compiler.Diagnostics;
using Sia.Spirv.Compiler.IL;
using Sia.Spirv.Compiler.Metadata;
using Sia.Spirv.Compiler.Model;

namespace Sia.Spirv.Compiler;

public sealed class SpirvFrontend
{
    private const string _kernelAttributeName = "Sia.Spirv.SpirvKernelAttribute";

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
        var kernels = new List<SpirvKernel>();
        var diagnostics = new List<SpirvDiagnostic>();
        foreach (var typeHandle in reader.TypeDefinitions) {
            var type = reader.GetTypeDefinition(typeHandle);
            var declaringType = MetadataNames.GetTypeName(reader, typeHandle);
            foreach (var methodHandle in type.GetMethods()) {
                var method = reader.GetMethodDefinition(methodHandle);
                if (!TryGetWorkgroupSize(reader, method, out var workgroupSize)) {
                    continue;
                }

                var methodName = reader.GetString(method.Name);
                var qualifiedName = $"{declaringType}.{methodName}";
                if (!ValidateKernelDeclaration(
                    reader,
                    method,
                    qualifiedName,
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
                    new CilStackAnalyzer(reader, methodHandle).Validate(graph);

                    if (body.ExceptionRegions.Length != 0) {
                        diagnostics.Add(new SpirvDiagnostic(
                            SpirvDiagnosticIds.ExceptionHandling,
                            SpirvDiagnosticSeverity.Error,
                            "Exception regions are not supported inside a SPIR-V kernel.",
                            qualifiedName));
                    }

                    SpirvLegalityAnalyzer.Analyze(qualifiedName, graph, diagnostics);
                    kernels.Add(new SpirvKernel(
                        declaringType,
                        methodName,
                        MetadataTokens.GetToken(methodHandle),
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

    private static bool TryGetWorkgroupSize(
        MetadataReader reader,
        MethodDefinition method,
        out SpirvWorkgroupSize workgroupSize)
    {
        foreach (var attributeHandle in method.GetCustomAttributes()) {
            var attribute = reader.GetCustomAttribute(attributeHandle);
            if (MetadataNames.GetAttributeTypeName(reader, attribute) != _kernelAttributeName) {
                continue;
            }

            var value = attribute.DecodeValue(new CustomAttributeTypeProvider());
            if (value.FixedArguments.Length != 3) {
                throw new BadImageFormatException(
                    "SpirvKernelAttribute must contain three fixed arguments.");
            }

            workgroupSize = new SpirvWorkgroupSize(
                Convert.ToUInt32(value.FixedArguments[0].Value),
                Convert.ToUInt32(value.FixedArguments[1].Value),
                Convert.ToUInt32(value.FixedArguments[2].Value));
            return true;
        }

        workgroupSize = default;
        return false;
    }

    private static bool ValidateKernelDeclaration(
        MetadataReader reader,
        MethodDefinition method,
        string qualifiedName,
        SpirvWorkgroupSize workgroupSize,
        ICollection<SpirvDiagnostic> diagnostics)
    {
        var valid = true;
        var signature = method.DecodeSignature(SignatureTypeProvider.Instance, genericContext: null);
        if ((method.Attributes & MethodAttributes.Static) == 0 || !signature.ReturnType.IsVoid) {
            diagnostics.Add(new SpirvDiagnostic(
                SpirvDiagnosticIds.InvalidKernelSignature,
                SpirvDiagnosticSeverity.Error,
                "A SPIR-V kernel must be a static method returning void.",
                qualifiedName));
            valid = false;
        }

        if (method.RelativeVirtualAddress == 0) {
            diagnostics.Add(new SpirvDiagnostic(
                SpirvDiagnosticIds.InvalidKernelSignature,
                SpirvDiagnosticSeverity.Error,
                "A SPIR-V kernel must have a CIL method body.",
                qualifiedName));
            valid = false;
        }

        if (workgroupSize.X == 0 || workgroupSize.Y == 0 || workgroupSize.Z == 0) {
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
            var (kind, scalarType) = DecodeParameterType(type);
            parameters[position] = new SpirvKernelParameter(
                position < names.Length ? names[position] : $"arg{position}",
                position,
                kind,
                scalarType);
        }
        return parameters;
    }

    private static (SpirvKernelParameterKind Kind, SpirvScalarType ScalarType)
        DecodeParameterType(KernelType type)
    {
        if (type.Name == "Sia.Spirv.StorageBuffer`1" && type.ElementType != null) {
            return (SpirvKernelParameterKind.StorageBuffer, DecodeScalarType(type.ElementType));
        }
        return (SpirvKernelParameterKind.PushConstant, DecodeScalarType(type));
    }

    private static SpirvScalarType DecodeScalarType(KernelType type) => type.Name switch {
        "System.Int32" => SpirvScalarType.Int32,
        "System.UInt32" => SpirvScalarType.UInt32,
        "System.Single" => SpirvScalarType.Float32,
        _ => throw new InvalidDataException($"Kernel parameter type '{type.Name}' is not supported.")
    };
}
