using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.IR;

/// <summary>Intrinsic effect facts shared by type admission and the canonical IR.
/// Unknown operations remain conservative; stage and memory legality use the shared validator.</summary>
internal static class ShaderBuiltinEffects
{
    private static readonly HashSet<string> FloatUnary = new("acos acosh asin asinh atan atanh ceil cos cosh degrees exp exp2 floor fract inverseSqrt log log2 quantizeToF16 radians round saturate sin sinh sqrt tan tanh trunc".Split(' '), StringComparer.Ordinal);
    private static readonly HashSet<string> IntegerUnary = new("countLeadingZeros countOneBits countTrailingZeros firstLeadingBit firstTrailingBit reverseBits".Split(' '), StringComparer.Ordinal);
    private static readonly HashSet<string> Numeric = new("select dot4I8Packed dot4U8Packed all any isNan isInf abs sign min max clamp atan2 pow step distance dot cross reflect length normalize fma smoothstep faceForward mix refract transpose determinant outerProduct modf frexp extractBits insertBits pack4x8snorm pack4x8unorm pack2x16snorm pack2x16unorm pack2x16float pack4xI8 pack4xU8 pack4xI8Clamp pack4xU8Clamp unpack4x8snorm unpack4x8unorm unpack2x16snorm unpack2x16unorm unpack2x16float unpack4xI8 unpack4xU8".Split(' '), StringComparer.Ordinal);
    public static bool IsFloatUnary(string name) => FloatUnary.Contains(name);
    public static bool IsIntegerUnary(string name) => IntegerUnary.Contains(name);
    public static bool IsPureNumeric(string name) => IsFloatUnary(name) || IsIntegerUnary(name) || Numeric.Contains(name);
    private static readonly HashSet<string> Atomic = new("atomicLoad atomicStore atomicAdd atomicSub atomicMin atomicMax atomicAnd atomicOr atomicXor atomicExchange atomicCompareExchangeWeak spirvAtomicCompareExchange".Split(' '), StringComparer.Ordinal);
    private static readonly HashSet<string> Texture = new("textureDimensions textureNumLayers textureNumLevels textureNumSamples textureLoad textureStore textureSample textureSampleBias textureSampleLevel textureSampleGrad textureSampleCompare textureSampleCompareLevel textureSampleBaseClampToEdge textureGather textureGatherCompare textureAtomicAdd textureAtomicMin textureAtomicMax textureAtomicAnd textureAtomicOr textureAtomicXor".Split(' '), StringComparer.Ordinal);
    private static readonly HashSet<string> Derivatives = new("dpdx dpdxCoarse dpdxFine dpdy dpdyCoarse dpdyFine fwidth fwidthCoarse fwidthFine".Split(' '), StringComparer.Ordinal);
    private static readonly HashSet<string> QueryUpdates = new("rayQueryInitialize rayQueryProceed rayQueryTerminate rayQueryConfirmIntersection rayQueryGenerateIntersection spirvRayQueryInitializeKHR spirvRayQueryProceedKHR spirvRayQueryTerminateKHR spirvRayQueryConfirmIntersectionKHR spirvRayQueryGenerateIntersectionKHR".Split(' '), StringComparer.Ordinal);
    public static bool IsQuery(string name) => QueryUpdates.Contains(name) || RayQueryTypes.RawGetterType(name) is not null
        || name is "rayQueryGetCandidateIntersection" or "rayQueryGetCommittedIntersection" or "getCandidateHitVertexPositions" or "getCommittedHitVertexPositions";
    public static bool IsKnown(string name) => IsPureNumeric(name) || Atomic.Contains(name) || Texture.Contains(name)
        || Derivatives.Contains(name) || SubgroupBuiltins.Contains(name) || IsQuery(name)
        || name is "arrayLength" or "workgroupUniformLoad" or "storageBarrier" or "workgroupBarrier" or "textureBarrier" or "subgroupBarrier";
    public static ShaderEffects For(string name)
    {
        if (IsPureNumeric(name)) return ShaderEffects.None;
        if (name == "arrayLength") return ShaderEffects.ReadMemory;
        if (IsQuery(name)) return ShaderEffects.Resource | ShaderEffects.ReadMemory
            | (QueryUpdates.Contains(name) ? ShaderEffects.WriteMemory : ShaderEffects.None);
        if (Atomic.Contains(name)) return ShaderEffects.Atomic | (name == "atomicLoad" ? ShaderEffects.ReadMemory
            : name == "atomicStore" ? ShaderEffects.WriteMemory : ShaderEffects.ReadMemory | ShaderEffects.WriteMemory);
        if (name is "storageBarrier" or "workgroupBarrier" or "textureBarrier" or "subgroupBarrier" or "workgroupUniformLoad")
            return ShaderEffects.ReadMemory | ShaderEffects.WriteMemory | ShaderEffects.Synchronization | ShaderEffects.Convergent | ShaderEffects.MemoryOrdering;
        if (Texture.Contains(name)) return ShaderEffects.Resource
            | (name == "textureStore" ? ShaderEffects.WriteMemory : name.StartsWith("textureAtomic", StringComparison.Ordinal)
                ? ShaderEffects.ReadMemory | ShaderEffects.WriteMemory | ShaderEffects.Atomic : ShaderEffects.ReadMemory)
            | (name is "textureSample" or "textureSampleBias" or "textureSampleCompare" ? ShaderEffects.Convergent : ShaderEffects.None);
        if (Derivatives.Contains(name) || SubgroupBuiltins.Contains(name)) return ShaderEffects.Convergent;
        return ShaderEffects.ReadMemory | ShaderEffects.WriteMemory | ShaderEffects.UnknownCall;
    }
    public static ShaderEffects Memory(SpirvMemoryAccess? memory) =>
        (memory is { Flags: var flags } && (flags & 1) != 0 ? ShaderEffects.Volatile : ShaderEffects.None)
        | (memory is { Flags: var ordering } && (ordering & (8 | 16 | 32)) != 0 ? ShaderEffects.MemoryOrdering : ShaderEffects.None);
    public static ShaderEffects AtomicMemory(SpirvAtomicMemory? memory) => memory is null ? ShaderEffects.None
        : ((memory.Semantics & 30) != 0 ? ShaderEffects.MemoryOrdering : ShaderEffects.None)
        | ((memory.Semantics & 32768) != 0 ? ShaderEffects.Volatile : ShaderEffects.None);
    public static ShaderEffects Barrier(bool control) => ShaderEffects.ReadMemory | ShaderEffects.WriteMemory | ShaderEffects.MemoryOrdering
        | (control ? ShaderEffects.Synchronization | ShaderEffects.Convergent : ShaderEffects.None);
}
