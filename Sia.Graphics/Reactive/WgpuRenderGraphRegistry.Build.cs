using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Graphics.Reactive;

public sealed partial class WgpuRenderGraphRegistry
{
    private void BuildPlan()
    {
        var builder = new RenderGraphBuilder();
        var bufferHandles = new Dictionary<
            RenderGraphBufferKey, RenderGraphBufferHandle>(
                _buffers.Count + _importedBuffers.Count);
        var textureHandles = new Dictionary<
            RenderGraphTextureKey, RenderGraphTextureHandle>(
                _textures.Count + _importedTextures.Count);
        var passHandles = new Dictionary<
            RenderGraphPassKey, RenderGraphPassHandle>(_passes.Count);
        var passBuilders = new Dictionary<
            RenderGraphPassKey, RenderGraphPassBuilder>(_passes.Count);

        foreach (var (key, descriptor, imported) in EnumerateBuffers()) {
            var handle = imported
                ? builder.ImportBuffer(descriptor)
                : builder.CreateBuffer(descriptor);
            bufferHandles.Add(key, handle);
        }
        foreach (var (key, descriptor, imported) in EnumerateTextures()) {
            var handle = imported
                ? builder.ImportTexture(descriptor)
                : builder.CreateTexture(descriptor);
            textureHandles.Add(key, handle);
        }

        var orderedPasses = _passes.OrderBy(static item => item.Value.Order).ToArray();
        foreach (var (key, entry) in orderedPasses) {
            var pass = entry.Kind == RenderGraphPassKind.Compute
                ? builder.AddComputePass(entry.Name)
                : builder.AddPass(entry.Name);
            passBuilders.Add(key, pass);
            passHandles.Add(key, pass.Handle);
        }
        foreach (var (key, entry) in orderedPasses) {
            var pass = new RenderGraphPassDeclarationBuilder(
                passBuilders[key],
                bufferHandles,
                textureHandles,
                passHandles);
            entry.Declaration(pass);
        }

        foreach (var (key, entry) in Ordered(_exportedBuffers)) {
            if (!bufferHandles.TryGetValue(key, out var handle)) {
                throw new InvalidOperationException(
                    $"Exported render graph buffer '{key}' has not been registered.");
            }
            builder.Export(handle, entry.Value);
        }
        foreach (var (key, entry) in Ordered(_exportedTextures)) {
            if (!textureHandles.TryGetValue(key, out var handle)) {
                throw new InvalidOperationException(
                    $"Exported render graph texture '{key}' has not been registered.");
            }
            builder.Export(handle, entry.Value);
        }

        var graph = RenderGraphCompiler.Compile(builder.Build());
        var plan = WgpuRenderGraphLowerer.Lower(graph);
        _bufferHandles = bufferHandles;
        _textureHandles = textureHandles;
        _passHandles = passHandles;
        _plan = plan;
        _bindings = null;
        _passAdapters.Clear();
        _preparedStructureVersion = _structureVersion;
        _preparedBindingVersion = -1;
        CompilationCount++;
    }

    private readonly Dictionary<RenderGraphPassKey, (long HandlerId, ReactiveRenderGraphPassAdapter Adapter)>
        _passAdapters = [];

    private WgpuRenderGraphBindings BuildBindings(WgpuRenderGraphPlan plan)
    {
        var retained = _bindings != null && ReferenceEquals(_bindings.Plan, plan);
        var bindings = retained
            ? _bindings!
            : new WgpuRenderGraphBindings(plan);
        var rebuild = !retained
            || HasRemoved(_dirtyBufferBindings, _bufferBindings)
            || HasRemoved(_dirtyTextureBindings, _textureBindings)
            || HasRemoved(_dirtyPassHandlers, _passHandlers);
        if (rebuild) {
            bindings.Clear();
            foreach (var (key, entry) in _bufferBindings) { BindBuffer(key, entry); }
            foreach (var (key, entry) in _textureBindings) { BindTexture(key, entry); }
            foreach (var (key, entry) in _passHandlers) { BindHandler(key, entry); }
            ValidateImportedBindings(plan);
        }
        else {
            foreach (var key in _dirtyBufferBindings) { BindBuffer(key, _bufferBindings[key]); }
            foreach (var key in _dirtyTextureBindings) { BindTexture(key, _textureBindings[key]); }
            foreach (var key in _dirtyPassHandlers) { BindHandler(key, _passHandlers[key]); }
        }
        _dirtyBufferBindings.Clear();
        _dirtyTextureBindings.Clear();
        _dirtyPassHandlers.Clear();
        return bindings;

        void BindBuffer(RenderGraphBufferKey key, Entry<WgpuHandle<WGPUBuffer>> entry)
        {
            if (!_importedBuffers.ContainsKey(key) ||
                !_bufferHandles.TryGetValue(key, out var handle)) {
                throw new InvalidOperationException(
                    $"Bound render graph buffer '{key}' is not an imported buffer.");
            }
            bindings.Bind(handle, entry.Value);
        }

        void BindTexture(RenderGraphTextureKey key, Entry<WgpuHandle<WGPUTexture>> entry)
        {
            if (!_importedTextures.ContainsKey(key) ||
                !_textureHandles.TryGetValue(key, out var handle)) {
                throw new InvalidOperationException(
                    $"Bound render graph texture '{key}' is not an imported texture.");
            }
            bindings.Bind(handle, entry.Value);
        }

        void BindHandler(RenderGraphPassKey key, Entry<WgpuReactiveRenderGraphPassHandler> entry)
        {
            if (!_passHandles.TryGetValue(key, out var handle)) {
                throw new InvalidOperationException(
                    $"Bound render graph pass '{key}' has not been registered.");
            }
            if (plan.Graph.IsPassLive(handle)) {
                bindings.SetPassHandler(handle, GetOrCreateAdapter(key, entry).Handler);
            }
        }
    }

    private static bool HasRemoved<TKey, TValue>(HashSet<TKey> dirty, Dictionary<TKey, TValue> entries)
        where TKey : notnull
    {
        foreach (var key in dirty) {
            if (!entries.ContainsKey(key)) { return true; }
        }
        return false;
    }

    private ReactiveRenderGraphPassAdapter GetOrCreateAdapter(
        RenderGraphPassKey key,
        Entry<WgpuReactiveRenderGraphPassHandler> entry)
    {
        if (_passAdapters.TryGetValue(key, out var cached) && cached.HandlerId == entry.Id) {
            return cached.Adapter;
        }

        var adapter = new ReactiveRenderGraphPassAdapter(
            entry.Value,
            _bufferHandles,
            _textureHandles);
        _passAdapters[key] = (entry.Id, adapter);
        return adapter;
    }

    private void ValidateImportedBindings(WgpuRenderGraphPlan plan)
    {
        foreach (var (key, handle) in _bufferHandles) {
            if (plan.Graph.IsImported(handle) &&
                plan.Graph.GetLifetime(handle).IsUsed &&
                !_bufferBindings.ContainsKey(key)) {
                throw new InvalidOperationException(
                    $"Imported render graph buffer '{key}' has not been bound.");
            }
        }
        foreach (var (key, handle) in _textureHandles) {
            if (plan.Graph.IsImported(handle) &&
                plan.Graph.GetLifetime(handle).IsUsed &&
                !_textureBindings.ContainsKey(key)) {
                throw new InvalidOperationException(
                    $"Imported render graph texture '{key}' has not been bound.");
            }
        }
    }

    private IEnumerable<(
        RenderGraphBufferKey Key,
        RenderGraphBufferDescriptor Descriptor,
        bool Imported)> EnumerateBuffers() =>
        _buffers
            .Select(static item => (item.Key, item.Value.Value, Imported: false))
            .Concat(_importedBuffers.Select(
                static item => (item.Key, item.Value.Value, Imported: true)))
            .OrderBy(static item => item.Key.Value, StringComparer.Ordinal);

    private IEnumerable<(
        RenderGraphTextureKey Key,
        RenderGraphTextureDescriptor Descriptor,
        bool Imported)> EnumerateTextures() =>
        _textures
            .Select(static item => (item.Key, item.Value.Value, Imported: false))
            .Concat(_importedTextures.Select(
                static item => (item.Key, item.Value.Value, Imported: true)))
            .OrderBy(static item => item.Key.Value, StringComparer.Ordinal);

    private static IEnumerable<KeyValuePair<TKey, TValue>> Ordered<TKey, TValue>(
        Dictionary<TKey, TValue> entries)
        where TKey : notnull =>
        entries.OrderBy(static item => item.Key.ToString(), StringComparer.Ordinal);
}
