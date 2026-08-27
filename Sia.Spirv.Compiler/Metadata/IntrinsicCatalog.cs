using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Sia.Spirv;

namespace Sia.Spirv.Compiler.Metadata;

/// <summary>
/// Recovers the <see cref="IntrinsicKind"/> a marker method in
/// <c>Sia.Spirv.Core.dll</c> declares via <c>[SpirvIntrinsic(...)]</c>. A
/// call site only resolves to a <see cref="MemberReferenceHandle"/>
/// (type/name/signature, never the attribute directly), so this opens a
/// second <see cref="MetadataReader"/> for that assembly and matches
/// structurally, once per distinct method.
/// </summary>
public sealed class IntrinsicCatalog : IDisposable
{
    private const string CoreAssemblyFileName = "Sia.Spirv.Core.dll";
    private const string SpirvIntrinsicAttributeName = "Sia.Spirv.SpirvIntrinsicAttribute";

    private readonly FileStream? _stream;
    private readonly PEReader? _peReader;
    private readonly MetadataReader? _reader;
    private readonly Dictionary<(string DeclaringType, string Name, string Parameters), IntrinsicKind?> _cache = [];

    private IntrinsicCatalog(FileStream? stream, PEReader? peReader, MetadataReader? reader)
    {
        _stream = stream;
        _peReader = peReader;
        _reader = reader;
    }

    /// <summary>
    /// Opens <c>Sia.Spirv.Core.dll</c> next to <paramref name="shaderAssemblyPath"/>.
    /// Missing (e.g. isolated tests) is not an error — lookups just resolve
    /// to no intrinsic.
    /// </summary>
    public static IntrinsicCatalog Open(string shaderAssemblyPath)
    {
        var directory = Path.GetDirectoryName(shaderAssemblyPath);
        var corePath = string.IsNullOrEmpty(directory)
            ? CoreAssemblyFileName
            : Path.Combine(directory, CoreAssemblyFileName);
        if (!File.Exists(corePath)) {
            return new IntrinsicCatalog(null, null, null);
        }

        var stream = File.OpenRead(corePath);
        var peReader = new PEReader(stream, PEStreamOptions.PrefetchEntireImage);
        return new IntrinsicCatalog(stream, peReader, peReader.GetMetadataReader());
    }

    public IntrinsicKind? Resolve(
        string declaringType,
        string name,
        MethodSignature<KernelType> signature)
    {
        if (_reader is null) {
            return null;
        }

        var key = (declaringType, name, FormatParameters(signature));
        if (_cache.TryGetValue(key, out var cached)) {
            return cached;
        }

        var kind = ResolveCore(declaringType, name, signature);
        _cache[key] = kind;
        return kind;
    }

    private IntrinsicKind? ResolveCore(
        string declaringType,
        string name,
        MethodSignature<KernelType> signature)
    {
        var reader = _reader!;
        foreach (var typeHandle in reader.TypeDefinitions) {
            if (MetadataNames.GetTypeName(reader, typeHandle) != declaringType) {
                continue;
            }

            var type = reader.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in type.GetMethods()) {
                var method = reader.GetMethodDefinition(methodHandle);
                if (reader.GetString(method.Name) != name) {
                    continue;
                }

                var candidate = method.DecodeSignature(new KernelTypeProvider(), genericContext: null);
                if (!SignatureMatches(candidate, signature)) {
                    continue;
                }

                return FindIntrinsicKind(reader, method);
            }
        }

        return null;
    }

    private static bool SignatureMatches(
        MethodSignature<KernelType> candidate,
        MethodSignature<KernelType> target) =>
        candidate.Header.IsInstance == target.Header.IsInstance &&
        candidate.ParameterTypes.SequenceEqual(target.ParameterTypes);

    private static IntrinsicKind? FindIntrinsicKind(MetadataReader reader, MethodDefinition method)
    {
        foreach (var attributeHandle in method.GetCustomAttributes()) {
            var attribute = reader.GetCustomAttribute(attributeHandle);
            if (MetadataNames.GetAttributeTypeName(reader, attribute) != SpirvIntrinsicAttributeName) {
                continue;
            }

            var value = attribute.DecodeValue(new CustomAttributeTypeProvider());
            return (IntrinsicKind)Convert.ToInt32(value.FixedArguments[0].Value);
        }

        return null;
    }

    private static string FormatParameters(MethodSignature<KernelType> signature) =>
        string.Join('|', signature.ParameterTypes.Select(static type => type.Name));

    public void Dispose()
    {
        _peReader?.Dispose();
        _stream?.Dispose();
    }
}
