namespace Sia.Spirv.Compiler.LLVM;

internal sealed record MetadataMethod(
    string DeclaringType,
    string Name,
    int ParameterCount,
    bool IsInstance,
    LlvmValueType ReturnType);
