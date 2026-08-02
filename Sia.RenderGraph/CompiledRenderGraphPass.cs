namespace Sia.RenderGraph;

public sealed class CompiledRenderGraphPass
{
    internal CompiledRenderGraphPass(
        RenderGraphPassHandle handle,
        string name,
        int declarationIndex,
        int executionIndex,
        RenderGraphPassHandle[] dependencies,
        RenderGraphBufferAccess[] buffers,
        RenderGraphTextureAccess[] textures)
    {
        Handle = handle;
        Name = name;
        DeclarationIndex = declarationIndex;
        ExecutionIndex = executionIndex;
        Dependencies = Array.AsReadOnly(dependencies);
        Buffers = Array.AsReadOnly(buffers);
        Textures = Array.AsReadOnly(textures);
    }

    public RenderGraphPassHandle Handle { get; }

    public string Name { get; }

    public int DeclarationIndex { get; }

    public int ExecutionIndex { get; }

    public IReadOnlyList<RenderGraphPassHandle> Dependencies { get; }

    public IReadOnlyList<RenderGraphBufferAccess> Buffers { get; }

    public IReadOnlyList<RenderGraphTextureAccess> Textures { get; }
}
