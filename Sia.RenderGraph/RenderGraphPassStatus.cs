namespace Sia.RenderGraph;

public readonly record struct RenderGraphPassStatus(
    RenderGraphPassHandle Handle,
    string Name,
    RenderGraphPassKind Kind,
    bool IsLive,
    bool HasSideEffects);
