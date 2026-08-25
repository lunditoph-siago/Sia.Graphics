using System.Reflection.Metadata;

namespace Sia.Spirv.Compiler.Metadata;

internal sealed class CustomAttributeTypeProvider : ICustomAttributeTypeProvider<string>
{
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

    public string GetSystemType() => "System.Type";

    public string GetSZArrayType(string elementType) => $"{elementType}[]";

    public string GetTypeFromDefinition(
        MetadataReader metadataReader,
        TypeDefinitionHandle handle,
        byte rawTypeKind) => MetadataNames.GetTypeName(metadataReader, handle);

    public string GetTypeFromReference(
        MetadataReader metadataReader,
        TypeReferenceHandle handle,
        byte rawTypeKind) => MetadataNames.GetTypeName(metadataReader, handle);

    public string GetTypeFromSerializedName(string name) => name;

    public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;

    public bool IsSystemType(string type) => type == "System.Type";
}
