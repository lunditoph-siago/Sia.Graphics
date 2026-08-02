namespace Sia.RenderGraph;

public readonly record struct RenderGraphBufferRange(ulong Offset, ulong Size)
{
    public static RenderGraphBufferRange Whole => default;

    public bool ExtendsToEnd => Size == 0;
}
