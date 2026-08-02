namespace Sia.RenderGraph;

public sealed class RenderGraphCompilationException : Exception
{
    public RenderGraphCompilationException(string message)
        : base(message)
    {
    }
}
