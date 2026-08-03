using Sia.RenderGraph;

namespace Sia.Graphics.Reactive;

public sealed class RenderGraphPassDeclarationBuilder
{
    private readonly RenderGraphPassBuilder _pass;
    private readonly IReadOnlyDictionary<RenderGraphBufferKey, RenderGraphBufferHandle> _buffers;
    private readonly IReadOnlyDictionary<RenderGraphTextureKey, RenderGraphTextureHandle> _textures;
    private readonly IReadOnlyDictionary<RenderGraphPassKey, RenderGraphPassHandle> _passes;

    internal RenderGraphPassDeclarationBuilder(
        RenderGraphPassBuilder pass,
        IReadOnlyDictionary<RenderGraphBufferKey, RenderGraphBufferHandle> buffers,
        IReadOnlyDictionary<RenderGraphTextureKey, RenderGraphTextureHandle> textures,
        IReadOnlyDictionary<RenderGraphPassKey, RenderGraphPassHandle> passes)
    {
        _pass = pass;
        _buffers = buffers;
        _textures = textures;
        _passes = passes;
    }

    public RenderGraphPassDeclarationBuilder Read(
        RenderGraphBufferKey buffer,
        RenderGraphBufferUsage usage,
        RenderGraphBufferRange range = default)
    {
        _pass.Read(GetBuffer(buffer), usage, range);
        return this;
    }

    public RenderGraphPassDeclarationBuilder Write(
        RenderGraphBufferKey buffer,
        RenderGraphBufferUsage usage,
        RenderGraphBufferRange range = default)
    {
        _pass.Write(GetBuffer(buffer), usage, range);
        return this;
    }

    public RenderGraphPassDeclarationBuilder ReadWrite(
        RenderGraphBufferKey buffer,
        RenderGraphBufferUsage usage,
        RenderGraphBufferRange range = default)
    {
        _pass.ReadWrite(GetBuffer(buffer), usage, range);
        return this;
    }

    public RenderGraphPassDeclarationBuilder Read(
        RenderGraphTextureKey texture,
        RenderGraphTextureUsage usage,
        RenderGraphTextureSubresourceRange subresources = default)
    {
        _pass.Read(GetTexture(texture), usage, subresources);
        return this;
    }

    public RenderGraphPassDeclarationBuilder Write(
        RenderGraphTextureKey texture,
        RenderGraphTextureUsage usage,
        RenderGraphTextureSubresourceRange subresources = default)
    {
        _pass.Write(GetTexture(texture), usage, subresources);
        return this;
    }

    public RenderGraphPassDeclarationBuilder ReadWrite(
        RenderGraphTextureKey texture,
        RenderGraphTextureUsage usage,
        RenderGraphTextureSubresourceRange subresources = default)
    {
        _pass.ReadWrite(GetTexture(texture), usage, subresources);
        return this;
    }

    public RenderGraphPassDeclarationBuilder DependsOn(
        RenderGraphPassKey dependency)
    {
        if (!_passes.TryGetValue(dependency, out var handle)) {
            throw new InvalidOperationException(
                $"Render graph pass '{dependency}' has not been registered.");
        }
        _pass.DependsOn(handle);
        return this;
    }

    private RenderGraphBufferHandle GetBuffer(RenderGraphBufferKey key) =>
        _buffers.TryGetValue(key, out var handle)
            ? handle
            : throw new InvalidOperationException(
                $"Render graph buffer '{key}' has not been registered.");

    private RenderGraphTextureHandle GetTexture(RenderGraphTextureKey key) =>
        _textures.TryGetValue(key, out var handle)
            ? handle
            : throw new InvalidOperationException(
                $"Render graph texture '{key}' has not been registered.");
}
