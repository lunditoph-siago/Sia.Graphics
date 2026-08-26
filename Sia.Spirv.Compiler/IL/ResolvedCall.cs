using System.Reflection.Metadata;
using Sia.Spirv;
using Sia.Spirv.Compiler.Metadata;

namespace Sia.Spirv.Compiler.IL;

/// <summary>
/// The identity of a <c>call</c>/<c>callvirt</c> target, resolved once by
/// <see cref="CilCallResolver"/> and shared by every consumer that needs it
/// (stack-depth analysis, legality checks, LLVM IR generation) instead of
/// each re-decoding the same metadata token independently.
/// </summary>
public sealed record ResolvedCall(
    string DeclaringType,
    string Name,
    MethodSignature<KernelType> Signature,
    IntrinsicKind? Intrinsic)
{
    public int ParameterCount => Signature.ParameterTypes.Length;

    public bool IsInstance => Signature.Header.IsInstance;

    public bool ReturnsVoid => Signature.ReturnType.Name == "System.Void";
}
