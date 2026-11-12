using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;

namespace Sia.Spirv.Compiler.Translation.Legalization;

internal static class ShaderValueLowering
{
    public static CanonicalModule Run(CanonicalModule canonical)
    {
        canonical = UndefinedValueLowering.Run(canonical);
        canonical = AtomicStorageLowering.Run(canonical);
        return MatrixStorageLowering.Run(canonical);
    }
}
