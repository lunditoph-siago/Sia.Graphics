namespace Sia.Spirv.Compiler.Translation.IR;

public sealed record SpirvMemoryAccess(uint Flags, uint? Alignment = null, uint? AvailableScope = null, uint? VisibleScope = null)
{
    internal SpirvMemoryAccess Leaf() => this with { Flags = Flags & ~2u, Alignment = null };
}
