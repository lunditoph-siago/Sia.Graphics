namespace Sia.RenderGraph;

public sealed class CompiledRenderGraph
{
    private readonly int _graphId;
    private readonly CompiledRenderGraphPass?[] _passesByDeclaration;

    internal CompiledRenderGraph(
        int graphId,
        CompiledRenderGraphBuffer[] buffers,
        CompiledRenderGraphTexture[] textures,
        CompiledRenderGraphPass[] passes,
        RenderGraphPassGroup[] passGroups,
        RenderGraphPassStatus[] passStatuses,
        RenderGraphStructureHash structureHash,
        int declaredPassCount)
    {
        _graphId = graphId;
        Buffers = Array.AsReadOnly(buffers);
        Textures = Array.AsReadOnly(textures);
        Passes = Array.AsReadOnly(passes);
        PassGroups = Array.AsReadOnly(passGroups);
        PassStatuses = Array.AsReadOnly(passStatuses);
        StructureHash = structureHash;
        _passesByDeclaration = new CompiledRenderGraphPass?[declaredPassCount];
        foreach (var pass in passes) {
            _passesByDeclaration[pass.DeclarationIndex] = pass;
        }
    }

    public IReadOnlyList<CompiledRenderGraphBuffer> Buffers { get; }

    public IReadOnlyList<CompiledRenderGraphTexture> Textures { get; }

    public IReadOnlyList<CompiledRenderGraphPass> Passes { get; }

    public IReadOnlyList<RenderGraphPassGroup> PassGroups { get; }

    public IReadOnlyList<RenderGraphPassStatus> PassStatuses { get; }

    public int CulledPassCount => PassStatuses.Count(static status => !status.IsLive);

    public RenderGraphStructureHash StructureHash { get; }

    public CompiledRenderGraphPass GetPass(RenderGraphPassHandle pass)
    {
        ValidatePass(pass);
        return _passesByDeclaration[pass.Index] ??
            throw new InvalidOperationException(
                "The render graph pass was culled during compilation.");
    }

    public bool IsPassLive(RenderGraphPassHandle pass)
    {
        ValidatePass(pass);
        return _passesByDeclaration[pass.Index] is not null;
    }

    public bool TryGetPass(
        RenderGraphPassHandle pass,
        out CompiledRenderGraphPass? compiledPass)
    {
        ValidatePass(pass);
        compiledPass = _passesByDeclaration[pass.Index];
        return compiledPass is not null;
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
            (uint)buffer.Index >= (uint)Buffers.Count) {
            throw new ArgumentException(
                "The buffer does not belong to this compiled render graph.",
                nameof(buffer));
        }
    }

    private void ValidateTexture(RenderGraphTextureHandle texture)
    {
        if (texture.GraphId != _graphId ||
            (uint)texture.Index >= (uint)Textures.Count) {
            throw new ArgumentException(
                "The texture does not belong to this compiled render graph.",
                nameof(texture));
        }
    }

    private void ValidatePass(RenderGraphPassHandle pass)
    {
        if (pass.GraphId != _graphId ||
            (uint)pass.Index >= (uint)_passesByDeclaration.Length) {
            throw new ArgumentException(
                "The pass does not belong to this compiled render graph.",
                nameof(pass));
        }
    }
}
