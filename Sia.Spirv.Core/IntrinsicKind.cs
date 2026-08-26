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
    Min,
    Max,
    InverseSqrt,
    Saturate,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    Equal,
    Select,
    Discard,
    SetPosition,
    SetOutput,
    SetFlatOutput,
    BufferIndex,
    Texture2DLoad,
    Texture2DArrayLoad,
    Texture2DArraySampleLevel,
    Sqrt,
    Sin,
    Cos,
    Pow,
    Abs,

    // Never attributed with [SpirvIntrinsic]: Sia.Math.float3/math is an
    // independent SIMD type; recognized structurally instead (MathBinding).
    Float3Construct,
    Float3Broadcast,
    Float3GetX,
    Float3GetY,
    Float3GetZ,
    Float3Add,
    Float3Subtract,
    Float3Negate,
    Float3MultiplyVector,
    Float3MultiplyScalar,
    Float3DivideVector,
    Float3DivideScalar,
    Float3Dot,
    Float3Cross,
    Float3Normalize,
    Float3Min,
    Float3Max,
    Float3Reflect,
}
