namespace Sia.Spirv.Compiler.Translation.IR;

/// <summary>Native image texel access facts. These flags are ImageOperands, not MemoryAccess bits.
/// Spatial operands (coordinate, layer, sample or mip) remain typed SSA arguments.</summary>
internal sealed record SpirvImageAccess(uint Flags, uint? AvailableScope = null, uint? VisibleScope = null)
{
    internal const uint SupportedFlags = 0x7f00;
}
