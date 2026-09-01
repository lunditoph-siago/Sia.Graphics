namespace Sia.Spirv.Compiler.LLVM;

internal readonly record struct LlvmValue(
    string Expression,
    LlvmValueType Type,
    bool IsReference = false,
    int AddressSpace = 0,
    LlvmBufferBinding? BufferBinding = null);
