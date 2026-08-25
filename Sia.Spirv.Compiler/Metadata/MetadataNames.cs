using System.Reflection.Metadata;

namespace Sia.Spirv.Compiler.Metadata;

internal static class MetadataNames
{
    public static string GetTypeName(MetadataReader reader, EntityHandle handle) =>
        handle.Kind switch {
            HandleKind.TypeDefinition => GetTypeName(reader, (TypeDefinitionHandle)handle),
            HandleKind.TypeReference => GetTypeName(reader, (TypeReferenceHandle)handle),
            _ => $"<{handle.Kind}>"
        };

    public static string GetTypeName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var name = reader.GetString(definition.Name);
        var declaringType = definition.GetDeclaringType();
        return declaringType.IsNil
            ? Join(reader.GetString(definition.Namespace), name)
            : $"{GetTypeName(reader, declaringType)}+{name}";
    }

    public static string GetTypeName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);
        return reference.ResolutionScope.Kind == HandleKind.TypeReference
            ? $"{GetTypeName(reader, (TypeReferenceHandle)reference.ResolutionScope)}+{name}"
            : Join(reader.GetString(reference.Namespace), name);
    }

    public static string GetAttributeTypeName(
        MetadataReader reader,
        CustomAttribute attribute) =>
        attribute.Constructor.Kind switch {
            HandleKind.MemberReference => GetTypeName(
                reader,
                reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent),
            HandleKind.MethodDefinition => GetTypeName(
                reader,
                reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor)
                    .GetDeclaringType()),
            _ => string.Empty
        };

    private static string Join(string namespaceName, string typeName) =>
        string.IsNullOrEmpty(namespaceName) ? typeName : $"{namespaceName}.{typeName}";
}
