using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Sia.Spirv.Compiler.Metadata;

namespace Sia.Spirv.Compiler.IL;

public sealed class CilCallResolver(MetadataReader reader, IntrinsicCatalog intrinsics)
{
    private readonly Dictionary<int, ResolvedCall> _cache = [];

    public ResolvedCall Resolve(int token)
    {
        if (_cache.TryGetValue(token, out var cached)) {
            return cached;
        }

        var resolved = ResolveCore(token);
        _cache[token] = resolved;
        return resolved;
    }

    private ResolvedCall ResolveCore(int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind == HandleKind.MethodSpecification) {
            var specification = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
            return ResolveCore(MetadataTokens.GetToken(specification.Method));
        }

        string declaringType;
        string name;
        MethodSignature<KernelType> signature;
        if (handle.Kind == HandleKind.MemberReference) {
            var reference = reader.GetMemberReference((MemberReferenceHandle)handle);
            declaringType = GetDeclaringTypeName(reference.Parent);
            name = reader.GetString(reference.Name);
            signature = reference.DecodeMethodSignature(new KernelTypeProvider(), genericContext: null);
        }
        else if (handle.Kind == HandleKind.MethodDefinition) {
            var definition = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
            declaringType = MetadataNames.GetTypeName(reader, definition.GetDeclaringType());
            name = reader.GetString(definition.Name);
            signature = definition.DecodeSignature(new KernelTypeProvider(), genericContext: null);
        }
        else {
            throw new InvalidDataException($"Token 0x{token:x8} is not a method.");
        }

        var intrinsic = MathBinding.Resolve(declaringType, name, signature)
            ?? intrinsics.Resolve(declaringType, name, signature);
        return new ResolvedCall(token, declaringType, name, signature, intrinsic);
    }

    private string GetDeclaringTypeName(EntityHandle handle) => handle.Kind switch {
        HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle)
            .DecodeSignature(new KernelTypeProvider(), genericContext: null).Name,
        _ => MetadataNames.GetTypeName(reader, handle)
    };
}
