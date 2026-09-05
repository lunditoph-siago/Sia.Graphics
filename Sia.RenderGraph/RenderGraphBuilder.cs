namespace Sia.RenderGraph;

public sealed class RenderGraphBuilder
{
    private static int s_NextGraphId;

    private readonly int _graphId = Interlocked.Increment(ref s_NextGraphId);
    private readonly List<RenderGraphBufferDefinition> _buffers = [];
    private readonly List<RenderGraphTextureDefinition> _textures = [];
    private readonly List<PassDraft> _passes = [];
    private bool _built;

    public RenderGraphBufferHandle CreateBuffer(RenderGraphBufferDescriptor descriptor) =>
        AddBuffer(descriptor, isImported: false);

    public RenderGraphBufferHandle ImportBuffer(RenderGraphBufferDescriptor descriptor) =>
        AddBuffer(descriptor, isImported: true);

    public RenderGraphTextureHandle CreateTexture(RenderGraphTextureDescriptor descriptor) =>
        AddTexture(descriptor, isImported: false);

    public RenderGraphTextureHandle ImportTexture(RenderGraphTextureDescriptor descriptor) =>
        AddTexture(descriptor, isImported: true);

    public void Export(
        RenderGraphBufferHandle buffer,
        RenderGraphBufferUsage usage = RenderGraphBufferUsage.None)
    {
        EnsureMutable();
        ValidateBuffer(buffer, nameof(buffer));
        RenderGraphValidation.Validate(usage, nameof(usage));
        var definition = _buffers[buffer.Index];
        _buffers[buffer.Index] = definition with {
            IsExported = true,
            ExportUsage = definition.ExportUsage | usage
        };
    }

    public void Export(
        RenderGraphTextureHandle texture,
        RenderGraphTextureUsage usage = RenderGraphTextureUsage.None)
    {
        EnsureMutable();
        ValidateTexture(texture, nameof(texture));
        RenderGraphValidation.Validate(usage, nameof(usage));
        var definition = _textures[texture.Index];
        _textures[texture.Index] = definition with {
            IsExported = true,
            ExportUsage = definition.ExportUsage | usage
        };
    }

    public RenderGraphPassBuilder AddPass(string name) =>
        AddPass(name, RenderGraphPassKind.Render);

    public RenderGraphPassBuilder AddComputePass(string name) =>
        AddPass(name, RenderGraphPassKind.Compute);

    private RenderGraphPassBuilder AddPass(string name, RenderGraphPassKind kind)
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var handle = new RenderGraphPassHandle(_graphId, _passes.Count);
        _passes.Add(new PassDraft(name, kind));
        return new RenderGraphPassBuilder(this, handle);
    }

    public RenderGraphDefinition Build()
    {
        EnsureMutable();
        _built = true;

        var passes = _passes
            .Select(static pass => new RenderGraphPassDefinition(
                pass.Name,
                pass.Kind,
                [.. pass.Buffers.OrderBy(static use => use.BufferIndex)
                    .ThenBy(static use => use.Range.Offset)
                    .ThenBy(static use => use.Range.Size)],
                [.. pass.Textures.OrderBy(static use => use.TextureIndex)
                    .ThenBy(static use => use.Subresources.BaseMipLevel)
                    .ThenBy(static use => use.Subresources.BaseArrayLayer)
                    .ThenBy(static use => use.Subresources.Aspect)],
                [.. pass.Dependencies.Order()],
                pass.HasSideEffects))
            .ToArray();

        return new RenderGraphDefinition(
            _graphId,
            [.. _buffers],
            [.. _textures],
            passes);
    }

    internal void AddAccess(
        RenderGraphPassHandle pass,
        RenderGraphBufferHandle buffer,
        RenderGraphAccess access,
        RenderGraphBufferUsage usage,
        RenderGraphBufferRange range)
    {
        EnsureMutable();
        ValidatePass(pass, nameof(pass));
        ValidateBuffer(buffer, nameof(buffer));
        RenderGraphValidation.Validate(usage, nameof(usage));
        if (usage == RenderGraphBufferUsage.None) {
            throw new ArgumentException(
                "A buffer access must declare at least one usage.",
                nameof(usage));
        }

        var normalizedRange = RenderGraphValidation.Normalize(
            _buffers[buffer.Index].Descriptor,
            range);
        var uses = _passes[pass.Index].Buffers;
        for (var index = 0; index < uses.Count; index++) {
            var use = uses[index];
            if (use.BufferIndex != buffer.Index || use.Range != normalizedRange) {
                continue;
            }

            uses[index] = use with {
                Access = Merge(use.Access, access),
                Usage = use.Usage | usage
            };
            return;
        }

        uses.Add(new RenderGraphBufferUse(
            buffer.Index,
            access,
            usage,
            normalizedRange));
    }

    internal void AddAccess(
        RenderGraphPassHandle pass,
        RenderGraphTextureHandle texture,
        RenderGraphAccess access,
        RenderGraphTextureUsage usage,
        RenderGraphTextureSubresourceRange subresources)
    {
        EnsureMutable();
        ValidatePass(pass, nameof(pass));
        ValidateTexture(texture, nameof(texture));
        RenderGraphValidation.Validate(usage, nameof(usage));
        if (usage == RenderGraphTextureUsage.None) {
            throw new ArgumentException(
                "A texture access must declare at least one usage.",
                nameof(usage));
        }

        var normalizedSubresources = RenderGraphValidation.Normalize(
            _textures[texture.Index].Descriptor,
            subresources);
        var uses = _passes[pass.Index].Textures;
        for (var index = 0; index < uses.Count; index++) {
            var use = uses[index];
            if (use.TextureIndex != texture.Index ||
                use.Subresources != normalizedSubresources) {
                continue;
            }

            uses[index] = use with {
                Access = Merge(use.Access, access),
                Usage = use.Usage | usage
            };
            return;
        }

        uses.Add(new RenderGraphTextureUse(
            texture.Index,
            access,
            usage,
            normalizedSubresources));
    }

    internal void AddDependency(
        RenderGraphPassHandle pass,
        RenderGraphPassHandle dependency)
    {
        EnsureMutable();
        ValidatePass(pass, nameof(pass));
        ValidatePass(dependency, nameof(dependency));
        if (pass == dependency) {
            throw new ArgumentException(
                "A render graph pass cannot depend on itself.",
                nameof(dependency));
        }

        _passes[pass.Index].Dependencies.Add(dependency.Index);
    }

    internal void MarkSideEffect(RenderGraphPassHandle pass)
    {
        EnsureMutable();
        ValidatePass(pass, nameof(pass));
        _passes[pass.Index].HasSideEffects = true;
    }

    private RenderGraphBufferHandle AddBuffer(
        RenderGraphBufferDescriptor descriptor,
        bool isImported)
    {
        EnsureMutable();
        RenderGraphValidation.Validate(descriptor);
        var handle = new RenderGraphBufferHandle(_graphId, _buffers.Count);
        _buffers.Add(new RenderGraphBufferDefinition(
            descriptor,
            isImported,
            IsExported: false,
            RenderGraphBufferUsage.None));
        return handle;
    }

    private RenderGraphTextureHandle AddTexture(
        RenderGraphTextureDescriptor descriptor,
        bool isImported)
    {
        EnsureMutable();
        RenderGraphValidation.Validate(descriptor);
        var handle = new RenderGraphTextureHandle(_graphId, _textures.Count);
        _textures.Add(new RenderGraphTextureDefinition(
            descriptor,
            isImported,
            IsExported: false,
            RenderGraphTextureUsage.None));
        return handle;
    }

    private static RenderGraphAccess Merge(
        RenderGraphAccess first,
        RenderGraphAccess second) =>
        first == second ? first : RenderGraphAccess.ReadWrite;

    private void ValidatePass(RenderGraphPassHandle pass, string parameterName)
    {
        if (pass.GraphId != _graphId ||
            (uint)pass.Index >= (uint)_passes.Count) {
            throw new ArgumentException(
                "The pass does not belong to this render graph.",
                parameterName);
        }
    }

    private void ValidateBuffer(
        RenderGraphBufferHandle buffer,
        string parameterName)
    {
        if (buffer.GraphId != _graphId ||
            (uint)buffer.Index >= (uint)_buffers.Count) {
            throw new ArgumentException(
                "The buffer does not belong to this render graph.",
                parameterName);
        }
    }

    private void ValidateTexture(
        RenderGraphTextureHandle texture,
        string parameterName)
    {
        if (texture.GraphId != _graphId ||
            (uint)texture.Index >= (uint)_textures.Count) {
            throw new ArgumentException(
                "The texture does not belong to this render graph.",
                parameterName);
        }
    }

    private void EnsureMutable()
    {
        if (_built) {
            throw new InvalidOperationException(
                "The render graph has already been built.");
        }
    }

    private sealed class PassDraft(string name, RenderGraphPassKind kind)
    {
        public string Name { get; } = name;

        public RenderGraphPassKind Kind { get; } = kind;

        public List<RenderGraphBufferUse> Buffers { get; } = [];

        public List<RenderGraphTextureUse> Textures { get; } = [];

        public HashSet<int> Dependencies { get; } = [];

        public bool HasSideEffects { get; set; }
    }
}
