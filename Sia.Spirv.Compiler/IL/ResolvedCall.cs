using System.Reflection.Metadata;
using Sia.Spirv;
using Sia.Spirv.Compiler.Metadata;

namespace Sia.Spirv.Compiler.IL;

public sealed record ResolvedCall(
    int MetadataToken,
    string DeclaringType,
    string Name,
    MethodSignature<KernelType> Signature,
    IntrinsicKind? Intrinsic)
{
    public int ParameterCount => Signature.ParameterTypes.Length;

    public bool IsInstance => Signature.Header.IsInstance;

    public bool ReturnsVoid => Signature.ReturnType.Name == "System.Void";
}
