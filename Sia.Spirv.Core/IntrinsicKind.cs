namespace Sia.Spirv;

/// <summary>
/// Identifies a GPU operation a marker method in this assembly stands in
/// for. The compiler recovers this from the method's <see
/// cref="SpirvIntrinsicAttribute"/> instead of matching on its declaring
/// type name and method name.
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

    // Float3Construct through Float3Reflect are never attributed with
    // [SpirvIntrinsic]: they identify operations on Sia.Math.float3/math,
    // a real SIMD type this compiler does not own and must not ask to
    // carry SPIR-V-specific metadata. The compiler recognizes them
    // structurally instead — see Sia.Spirv.Compiler.Metadata.MathBinding.
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
