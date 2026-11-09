namespace Sia.Spirv.Compiler.Tests.Support;

public sealed class SpirvToolchainFactAttribute : FactAttribute
{
    public SpirvToolchainFactAttribute()
    {
        if (SpirvTestToolchain.Directory is null) Skip = SpirvTestToolchain.UnavailableReason;
    }
}
