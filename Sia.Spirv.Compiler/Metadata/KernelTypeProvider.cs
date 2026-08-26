using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace Sia.Spirv.Compiler.Metadata;

internal sealed class KernelTypeProvider : ISignatureTypeProvider<KernelType, object?>
{
    public KernelType GetArrayType(KernelType elementType, ArrayShape shape) =>
        new("System.Array", elementType);

    public KernelType GetByReferenceType(KernelType elementType) =>
        elementType with { IsByReference = true };

    public KernelType GetFunctionPointerType(MethodSignature<KernelType> signature) =>
        new("System.FunctionPointer");

    public KernelType GetGenericInstantiation(
        KernelType genericType,
        ImmutableArray<KernelType> typeArguments) =>
        genericType.Name is "Sia.Spirv.ReadOnlyStorageBuffer`1" or
            "Sia.Spirv.StorageBuffer`1" && typeArguments.Length == 1
            ? new(genericType.Name, typeArguments[0])
            : new(genericType.Name);

    public KernelType GetGenericMethodParameter(object? genericContext, int index) =>
        new($"!!{index}");

    public KernelType GetGenericTypeParameter(object? genericContext, int index) =>
        new($"!{index}");

    public KernelType GetModifiedType(
        KernelType modifier,
        KernelType unmodifiedType,
        bool isRequired) => unmodifiedType;

    public KernelType GetPinnedType(KernelType elementType) => elementType;

    public KernelType GetPointerType(KernelType elementType) =>
        new("System.Pointer", elementType);

    public KernelType GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch {
        PrimitiveTypeCode.Void => KernelType.Void,
        PrimitiveTypeCode.Boolean => new("System.Boolean"),
        PrimitiveTypeCode.Int32 => new("System.Int32"),
        PrimitiveTypeCode.UInt32 => new("System.UInt32"),
        PrimitiveTypeCode.Int64 => new("System.Int64"),
        PrimitiveTypeCode.UInt64 => new("System.UInt64"),
        PrimitiveTypeCode.Single => new("System.Single"),
        PrimitiveTypeCode.Double => new("System.Double"),
        _ => new($"System.{typeCode}")
    };

    public KernelType GetSZArrayType(KernelType elementType) =>
        new("System.Array", elementType);

    public KernelType GetTypeFromDefinition(
        MetadataReader reader,
        TypeDefinitionHandle handle,
        byte rawTypeKind) => new(MetadataNames.GetTypeName(reader, handle));

    public KernelType GetTypeFromReference(
        MetadataReader reader,
        TypeReferenceHandle handle,
        byte rawTypeKind) => new(MetadataNames.GetTypeName(reader, handle));

    public KernelType GetTypeFromSpecification(
        MetadataReader reader,
        object? genericContext,
        TypeSpecificationHandle handle,
        byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
}
