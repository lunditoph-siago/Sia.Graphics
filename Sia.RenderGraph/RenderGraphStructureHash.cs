namespace Sia.RenderGraph;

public readonly record struct RenderGraphStructureHash(ulong Value)
{
    public override string ToString() => Value.ToString("x16");
}
