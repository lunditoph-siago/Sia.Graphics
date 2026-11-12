namespace Sia.Spirv.Compiler.Translation.IR;

internal sealed record SpirvImageAccess(uint Flags, uint? AvailableScope = null, uint? VisibleScope = null)
{
    internal const uint SupportedFlags = 0x7f00;
}
