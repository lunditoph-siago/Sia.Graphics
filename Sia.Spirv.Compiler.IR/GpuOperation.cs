namespace Sia.Spirv.Compiler.IR;

public enum GpuOperation
{
    Parameter,
    Constant,
    Phi,
    Add,
    Subtract,
    Multiply,
    Divide,
    Remainder,
    Compare,
    Convert,
    Load,
    Store,
    Branch,
    ConditionalBranch,
    Call,
    Return,
    LoadBuffer,
    StoreBuffer,
    Builtin,
    Barrier,
    Atomic,
    VectorConstruct,
    VectorExtract
}
