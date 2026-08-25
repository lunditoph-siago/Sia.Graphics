using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace Sia.Spirv.Compiler.Metadata;

internal sealed class SignatureTypeProvider : ISignatureTypeProvider<SignatureType, object?>
{
    public static SignatureTypeProvider Instance { get; } = new();

    public SignatureType GetArrayType(SignatureType elementType, ArrayShape shape) => default;

    public SignatureType GetByReferenceType(SignatureType elementType) => default;

    public SignatureType GetFunctionPointerType(MethodSignature<SignatureType> signature) => default;

    public SignatureType GetGenericInstantiation(
        SignatureType genericType,
        ImmutableArray<SignatureType> typeArguments) => default;

    public SignatureType GetGenericMethodParameter(object? genericContext, int index) => default;

    public SignatureType GetGenericTypeParameter(object? genericContext, int index) => default;

    public SignatureType GetModifiedType(
        SignatureType modifier,
        SignatureType unmodifiedType,
        bool isRequired) => unmodifiedType;

    public SignatureType GetPinnedType(SignatureType elementType) => elementType;

    public SignatureType GetPointerType(SignatureType elementType) => default;

    public SignatureType GetPrimitiveType(PrimitiveTypeCode typeCode) =>
        new(typeCode == PrimitiveTypeCode.Void);

    public SignatureType GetSZArrayType(SignatureType elementType) => default;

    public SignatureType GetTypeFromDefinition(
        MetadataReader reader,
        TypeDefinitionHandle handle,
        byte rawTypeKind) => default;

    public SignatureType GetTypeFromReference(
        MetadataReader reader,
        TypeReferenceHandle handle,
        byte rawTypeKind) => default;

    public SignatureType GetTypeFromSpecification(
        MetadataReader reader,
        object? genericContext,
        TypeSpecificationHandle handle,
        byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
}
