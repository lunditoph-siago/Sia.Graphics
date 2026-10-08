namespace Sia.Spirv.Compiler.Translation.Front;

public static partial class SpirvReader
{
    private sealed partial class Reader
    {
        private string CooperativeMemoryName(bool load, uint layout)
        {
            uint value = ConstantUint(layout);
            if (value > 1) throw Error("Cooperative matrix memory layout is not row-major or column-major.");
            return (load ? "coopLoad" : "coopStore") + (value == 0 ? "T" : "");
        }
    }
}
