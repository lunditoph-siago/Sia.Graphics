namespace Sia.RenderGraph;

public sealed class CompiledRenderGraph
{
    private readonly int _graphId;
    private readonly CompiledRenderGraphPass[] _passesByDeclaration;

    internal CompiledRenderGraph(
        int graphId,
        CompiledRenderGraphBuffer[] buffers,
        CompiledRenderGraphTexture[] textures,
        CompiledRenderGraphPass[] passes,
        RenderGraphStructureHash structureHash)
    {
        _graphId = graphId;
        Buffers = Array.AsReadOnly(buffers);
        Textures = Array.AsReadOnly(textures);
        Passes = Array.AsReadOnly(passes);
        StructureHash = structureHash;
        _passesByDeclaration = new CompiledRenderGraphPass[passes.Length];
        foreach (var pass in passes)
        {
            _passesByDeclaration[pass.DeclarationIndex] = pass;
        }
    }

    public IReadOnlyList<CompiledRenderGraphBuffer> Buffers { get; }

    public IReadOnlyList<CompiledRenderGraphTexture> Textures { get; }

    public IReadOnlyList<CompiledRenderGraphPass> Passes { get; }

    public RenderGraphStructureHash StructureHash { get; }

    public CompiledRenderGraphPass GetPass(RenderGraphPassHandle pass)
    {
        if (pass.GraphId != _graphId ||
            (uint)pass.Index >= (uint)_passesByDeclaration.Length)
        {
            throw new ArgumentException(
                "The pass does not belong to this compiled render graph.",
                nameof(pass));
        }

        return _passesByDeclaration[pass.Index];
    }

    public CompiledRenderGraphBuffer GetBuffer(RenderGraphBufferHandle buffer)
    {
        ValidateBuffer(buffer);
        return Buffers[buffer.Index];
    }

    public CompiledRenderGraphTexture GetTexture(RenderGraphTextureHandle texture)
    {
        ValidateTexture(texture);
        return Textures[texture.Index];
    }

    public RenderGraphBufferDescriptor GetDescriptor(
        RenderGraphBufferHandle buffer) => GetBuffer(buffer).Descriptor;

    public RenderGraphTextureDescriptor GetDescriptor(
        RenderGraphTextureHandle texture) => GetTexture(texture).Descriptor;

    public string GetName(RenderGraphBufferHandle buffer) =>
        GetBuffer(buffer).Descriptor.Name;

    public string GetName(RenderGraphTextureHandle texture) =>
        GetTexture(texture).Descriptor.Name;

    public bool IsImported(RenderGraphBufferHandle buffer) =>
        GetBuffer(buffer).IsImported;

    public bool IsImported(RenderGraphTextureHandle texture) =>
        GetTexture(texture).IsImported;

    public bool IsExported(RenderGraphBufferHandle buffer) =>
        GetBuffer(buffer).IsExported;

    public bool IsExported(RenderGraphTextureHandle texture) =>
        GetTexture(texture).IsExported;

    public RenderGraphResourceLifetime GetLifetime(
        RenderGraphBufferHandle buffer) => GetBuffer(buffer).Lifetime;

    public RenderGraphResourceLifetime GetLifetime(
        RenderGraphTextureHandle texture) => GetTexture(texture).Lifetime;

    private void ValidateBuffer(RenderGraphBufferHandle buffer)
    {
        if (buffer.GraphId != _graphId ||
            (uint)buffer.Index >= (uint)Buffers.Count)
        {
            throw new ArgumentException(
                "The buffer does not belong to this compiled render graph.",
                nameof(buffer));
        }
    }

    private void ValidateTexture(RenderGraphTextureHandle texture)
    {
        if (texture.GraphId != _graphId ||
            (uint)texture.Index >= (uint)Textures.Count)
        {
            throw new ArgumentException(
                "The texture does not belong to this compiled render graph.",
                nameof(texture));
        }
    }
}
