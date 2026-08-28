namespace Sia.Spirv;

/// <summary>
/// Identifies a GPU operation a marker method stands in for, recovered
/// from <see cref="SpirvIntrinsicAttribute"/> rather than matched by name.
/// </summary>
public enum IntrinsicKind
{
    GlobalInvocationId,
    LocalInvocationId,
    WorkGroupId,
    Barrier,
    VertexIndex,
    InstanceIndex,
    GetInput,
    GetFlatInput,
    GetFragmentPosition,
    AsFloat,
    UnpackHalf,
    InverseSqrt,
    Select,
    Discard,
    SetPosition,
    SetOutput,
    SetFlatOutput,
    BufferIndex,
    AtomicAdd,
    AtomicExchange,
    Texture2DLoad,
    Texture2DSampleLevel,
    Texture2DArrayLoad,
    Texture2DArraySampleLevel,
    Sqrt,
    Sin,
    Cos,
    Pow,
    Abs,

    // Never attributed with [SpirvIntrinsic]: Sia.Math is an independent
    // SIMD library and is recognized structurally by MathBinding.
    MathConstruct,
    MathGetComponent,
    MathAdd,
    MathSubtract,
    MathNegate,
    MathMultiply,
    MathDivide,
    MathDot,
    MathCross,
    MathNormalize,
    MathMin,
    MathMax,
    MathClamp,
    MathSaturate,
    MathReflect,
    MathAny,
    MathAll,
    MathMul,
    MathTranspose,
}
