namespace Sia.Spirv.Compiler.LLVM;

internal enum LlvmValueType
{
    Void,
    Boolean,
    Int32,
    UInt32,
    Float32,
    UInt3,
    Texture2DFloat,
    Texture2DArrayFloat,
    Sampler,
    ReadOnlyBufferInt32,
    ReadOnlyBufferUInt32,
    ReadOnlyBufferFloat32,
    BufferInt32,
    BufferUInt32,
    BufferFloat32
}
