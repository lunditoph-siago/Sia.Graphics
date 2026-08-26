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
}
