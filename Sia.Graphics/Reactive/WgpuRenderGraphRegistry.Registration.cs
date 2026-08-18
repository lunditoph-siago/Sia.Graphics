using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Graphics.Reactive;

public sealed partial class WgpuRenderGraphRegistry
{
    public RenderGraphRegistration RegisterBuffer(
        RenderGraphBufferKey key,
        RenderGraphBufferDescriptor descriptor)
    {
        ThrowIfDisposed();
        EnsureBufferKeyAvailable(key);
        var id = NextRegistrationId();
        _buffers.Add(key, new(id, descriptor));
        StructureChanged();
        return new(() => Remove(_buffers, key, id, structure: true));
    }

    public RenderGraphRegistration RegisterImportedBuffer(
        RenderGraphBufferKey key,
        RenderGraphBufferDescriptor descriptor)
    {
        ThrowIfDisposed();
        EnsureBufferKeyAvailable(key);
        var id = NextRegistrationId();
        _importedBuffers.Add(key, new(id, descriptor));
        StructureChanged();
        return new(() => Remove(_importedBuffers, key, id, structure: true));
    }

    public RenderGraphRegistration RegisterTexture(
        RenderGraphTextureKey key,
        RenderGraphTextureDescriptor descriptor)
    {
        ThrowIfDisposed();
        EnsureTextureKeyAvailable(key);
        var id = NextRegistrationId();
        _textures.Add(key, new(id, descriptor));
        StructureChanged();
        return new(() => Remove(_textures, key, id, structure: true));
    }

    public RenderGraphRegistration RegisterImportedTexture(
        RenderGraphTextureKey key,
        RenderGraphTextureDescriptor descriptor)
    {
        ThrowIfDisposed();
        EnsureTextureKeyAvailable(key);
        var id = NextRegistrationId();
        _importedTextures.Add(key, new(id, descriptor));
        StructureChanged();
        return new(() => Remove(_importedTextures, key, id, structure: true));
    }

    public RenderGraphRegistration RegisterBufferExport(
        RenderGraphBufferKey key,
        RenderGraphBufferUsage usage = RenderGraphBufferUsage.None)
    {
        ThrowIfDisposed();
        Validate(key);
        var id = NextRegistrationId();
        AddUnique(_exportedBuffers, key, new(id, usage), "buffer export");
        StructureChanged();
        return new(() => Remove(_exportedBuffers, key, id, structure: true));
    }

    public RenderGraphRegistration RegisterTextureExport(
        RenderGraphTextureKey key,
        RenderGraphTextureUsage usage = RenderGraphTextureUsage.None)
    {
        ThrowIfDisposed();
        Validate(key);
        var id = NextRegistrationId();
        AddUnique(_exportedTextures, key, new(id, usage), "texture export");
        StructureChanged();
        return new(() => Remove(_exportedTextures, key, id, structure: true));
    }

    public RenderGraphRegistration RegisterPass(
        RenderGraphPassKey key,
        string name,
        RenderGraphPassDeclaration declaration,
        RenderGraphPassKind kind = RenderGraphPassKind.Render)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(declaration);
        Validate(key);
        var id = NextRegistrationId();
        if (!_passes.TryAdd(key, new(id, name, declaration, kind))) {
            throw Duplicate("pass", key);
        }
        StructureChanged();
        return new(() => RemovePass(key, id));
    }

    public RenderGraphRegistration BindImportedBuffer(
        RenderGraphBufferKey key,
        WgpuHandle<WGPUBuffer> buffer)
    {
        ThrowIfDisposed();
        Validate(key);
        if (buffer.IsNull) {
            throw new ArgumentException(
                "An imported render graph buffer cannot use a null handle.",
                nameof(buffer));
        }
        var id = NextRegistrationId();
        AddUnique(_bufferBindings, key, new(id, buffer), "buffer binding");
        BindingsChanged();
        return new(() => Remove(_bufferBindings, key, id, structure: false));
    }

    public RenderGraphRegistration BindImportedTexture(
        RenderGraphTextureKey key,
        WgpuHandle<WGPUTexture> texture)
    {
        ThrowIfDisposed();
        Validate(key);
        if (texture.IsNull) {
            throw new ArgumentException(
                "An imported render graph texture cannot use a null handle.",
                nameof(texture));
        }
        var id = NextRegistrationId();
        AddUnique(_textureBindings, key, new(id, texture), "texture binding");
        BindingsChanged();
        return new(() => Remove(_textureBindings, key, id, structure: false));
    }

    public RenderGraphRegistration BindPassHandler(
        RenderGraphPassKey key,
        WgpuReactiveRenderGraphPassHandler handler)
    {
        ThrowIfDisposed();
        Validate(key);
        ArgumentNullException.ThrowIfNull(handler);
        var id = NextRegistrationId();
        AddUnique(_passHandlers, key, new(id, handler), "pass handler");
        BindingsChanged();
        return new(() => Remove(_passHandlers, key, id, structure: false));
    }

    private static void AddUnique<TKey, TValue>(
        Dictionary<TKey, TValue> entries,
        TKey key,
        TValue value,
        string kind)
        where TKey : notnull
    {
        if (!entries.TryAdd(key, value)) {
            throw Duplicate(kind, key);
        }
    }

    private void EnsureBufferKeyAvailable(RenderGraphBufferKey key)
    {
        Validate(key);
        if (_buffers.ContainsKey(key) || _importedBuffers.ContainsKey(key)) {
            throw Duplicate("buffer", key);
        }
    }

    private void EnsureTextureKeyAvailable(RenderGraphTextureKey key)
    {
        Validate(key);
        if (_textures.ContainsKey(key) || _importedTextures.ContainsKey(key)) {
            throw Duplicate("texture", key);
        }
    }

    private void RemovePass(RenderGraphPassKey key, long id)
    {
        if (_disposed || !_passes.TryGetValue(key, out var entry) || entry.Id != id) {
            return;
        }
        _passes.Remove(key);
        StructureChanged();
    }

    private void Remove<TKey, TValue>(
        Dictionary<TKey, Entry<TValue>> entries,
        TKey key,
        long id,
        bool structure)
        where TKey : notnull
    {
        if (_disposed || !entries.TryGetValue(key, out var entry) || entry.Id != id) {
            return;
        }
        entries.Remove(key);
        if (structure) {
            StructureChanged();
        }
        else {
            BindingsChanged();
        }
    }

    private void StructureChanged()
    {
        _structureVersion++;
        _bindings = null;
        _preparedBindingVersion = -1;
        _viewCache.Clear();
    }

    private void BindingsChanged()
    {
        _bindingVersion++;
        _bindings = null;
        _preparedBindingVersion = -1;
    }

    private static InvalidOperationException Duplicate<TKey>(string kind, TKey key) =>
        new($"Render graph {kind} '{key}' is already registered.");

    private static void Validate(RenderGraphBufferKey key) => ValidateKey(key.Value);

    private static void Validate(RenderGraphTextureKey key) => ValidateKey(key.Value);

    private static void Validate(RenderGraphPassKey key) => ValidateKey(key.Value);

    private static void ValidateKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) {
            throw new ArgumentException("A render graph key cannot be empty.", nameof(key));
        }
    }
}
