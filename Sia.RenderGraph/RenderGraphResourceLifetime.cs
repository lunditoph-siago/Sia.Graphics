namespace Sia.RenderGraph;

public readonly record struct RenderGraphResourceLifetime(int FirstUse, int LastUse)
{
    public static RenderGraphResourceLifetime Unused => new(-1, -1);

    public bool IsUsed => FirstUse >= 0;
}
