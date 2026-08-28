namespace Sia.Spirv.Compiler.LLVM;

internal readonly record struct LlvmValue(
    string Expression,
    LlvmValueType Type,
    bool IsReference = false,
    int AddressSpace = 0,
    string? ResourceExpression = null,
    string? ElementIndexExpression = null,
    LlvmValueType ResourceType = LlvmValueType.Void);
