namespace Sia.Spirv.Compiler.Translation.IR;

/// <summary>Native memory-access mask and its literal alignment/constant scope operands.</summary>
public sealed record SpirvMemoryAccess(uint Flags, uint? Alignment = null, uint? AvailableScope = null, uint? VisibleScope = null)
{
    /// <summary>Derive a leaf access without assuming the base alignment also holds at every offset.</summary>
    internal SpirvMemoryAccess Leaf() => this with { Flags = Flags & ~2u, Alignment = null };
}
