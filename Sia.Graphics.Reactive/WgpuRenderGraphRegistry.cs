using Sia;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Graphics.Reactive;

public sealed partial class WgpuRenderGraphRegistry : IAddon, IDisposable
{
    private sealed record Entry<T>(long Id, T Value);

    private sealed record PassEntry(
        long Id,
        string Name,
        RenderGraphPassDeclaration Declaration);

    private readonly Dictionary<RenderGraphBufferKey, Entry<RenderGraphBufferDescriptor>>
        _buffers = [];
    private readonly Dictionary<RenderGraphBufferKey, Entry<RenderGraphBufferDescriptor>>
        _importedBuffers = [];
    private readonly Dictionary<RenderGraphTextureKey, Entry<RenderGraphTextureDescriptor>>
        _textures = [];
    private readonly Dictionary<RenderGraphTextureKey, Entry<RenderGraphTextureDescriptor>>
        _importedTextures = [];
    private readonly Dictionary<RenderGraphBufferKey, Entry<RenderGraphBufferUsage>>
        _exportedBuffers = [];
    private readonly Dictionary<RenderGraphTextureKey, Entry<RenderGraphTextureUsage>>
        _exportedTextures = [];
    private readonly Dictionary<RenderGraphPassKey, PassEntry> _passes = [];

    private readonly Dictionary<RenderGraphBufferKey, Entry<WgpuHandle<WGPUBuffer>>>
        _bufferBindings = [];
    private readonly Dictionary<RenderGraphTextureKey, Entry<WgpuHandle<WGPUTexture>>>
        _textureBindings = [];
    private readonly Dictionary<RenderGraphPassKey, Entry<WgpuReactiveRenderGraphPassHandler>>
        _passHandlers = [];

    private Dictionary<RenderGraphBufferKey, RenderGraphBufferHandle> _bufferHandles = [];
    private Dictionary<RenderGraphTextureKey, RenderGraphTextureHandle> _textureHandles = [];
    private Dictionary<RenderGraphPassKey, RenderGraphPassHandle> _passHandles = [];
    private WgpuRenderGraphPlan? _plan;
    private WgpuRenderGraphBindings? _bindings;
    private WgpuRenderGraphExports? _exports;
    private WgpuRenderGraphPlan? _executedPlan;
    private WgpuHandle<WGPUDevice> _device;
    private WgpuHandle<WGPUQueue> _queue;
    private long _nextRegistrationId;
    private long _structureVersion;
    private long _preparedStructureVersion = -1;
    private long _bindingVersion;
    private long _preparedBindingVersion = -1;
    private bool _disposed;

    public long StructureVersion => _structureVersion;

    public int CompilationCount { get; private set; }

    public ulong ExecutionCount { get; private set; }

    public bool IsConfigured => !_device.IsNull && !_queue.IsNull;

    public WgpuRenderGraphPlan? CurrentPlan => _plan;

    public void Configure(
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUQueue> queue)
    {
        ThrowIfDisposed();
        if (device.IsNull) {
            throw new ArgumentException("The WebGPU device is null.", nameof(device));
        }
        if (queue.IsNull) {
            throw new ArgumentException("The WebGPU queue is null.", nameof(queue));
        }
        _device = device;
        _queue = queue;
    }

    public WgpuRenderGraphPlan PreparePlan()
    {
        ThrowIfDisposed();
        if (_plan != null && _preparedStructureVersion == _structureVersion) {
            return _plan;
        }

        BuildPlan();
        return _plan!;
    }

    public WgpuRenderGraphBindings PrepareBindings()
    {
        var plan = PreparePlan();
        if (_bindings != null &&
            _preparedBindingVersion == _bindingVersion &&
            ReferenceEquals(_bindings.Plan, plan)) {
            return _bindings;
        }

        _bindings = BuildBindings(plan);
        _preparedBindingVersion = _bindingVersion;
        return _bindings;
    }

    public void Execute()
    {
        ThrowIfDisposed();
        if (!IsConfigured) {
            throw new InvalidOperationException(
                "Configure the reactive render graph with a WebGPU device and queue before execution.");
        }

        var plan = PreparePlan();
        var bindings = PrepareBindings();
        var nextExports = WgpuRenderGraphExecutor.Execute(
            plan,
            _device,
            _queue,
            bindings);
        var previousExports = _exports;
        _exports = nextExports;
        _executedPlan = plan;
        ExecutionCount++;
        previousExports?.Dispose();
    }

    public RenderGraphBufferHandle GetBufferHandle(RenderGraphBufferKey key)
    {
        PreparePlan();
        return _bufferHandles.TryGetValue(key, out var handle)
            ? handle
            : throw new KeyNotFoundException(
                $"Render graph buffer '{key}' is not registered.");
    }

    public RenderGraphTextureHandle GetTextureHandle(RenderGraphTextureKey key)
    {
        PreparePlan();
        return _textureHandles.TryGetValue(key, out var handle)
            ? handle
            : throw new KeyNotFoundException(
                $"Render graph texture '{key}' is not registered.");
    }

    public RenderGraphPassHandle GetPassHandle(RenderGraphPassKey key)
    {
        PreparePlan();
        return _passHandles.TryGetValue(key, out var handle)
            ? handle
            : throw new KeyNotFoundException(
                $"Render graph pass '{key}' is not registered.");
    }

    public WgpuHandle<WGPUBuffer> GetExportedBuffer(RenderGraphBufferKey key)
    {
        var plan = PreparePlan();
        if (_exports == null || !ReferenceEquals(_executedPlan, plan)) {
            throw new InvalidOperationException(
                "The current reactive render graph has not been executed.");
        }
        return _exports.GetBuffer(GetBufferHandle(key));
    }

    public WgpuHandle<WGPUTexture> GetExportedTexture(RenderGraphTextureKey key)
    {
        var plan = PreparePlan();
        if (_exports == null || !ReferenceEquals(_executedPlan, plan)) {
            throw new InvalidOperationException(
                "The current reactive render graph has not been executed.");
        }
        return _exports.GetTexture(GetTextureHandle(key));
    }

    public void Dispose()
    {
        if (_disposed) {
            return;
        }
        _disposed = true;
        _exports?.Dispose();
        _exports = null;
        _executedPlan = null;
        _bindings = null;
        _plan = null;
        _buffers.Clear();
        _importedBuffers.Clear();
        _textures.Clear();
        _importedTextures.Clear();
        _exportedBuffers.Clear();
        _exportedTextures.Clear();
        _passes.Clear();
        _bufferBindings.Clear();
        _textureBindings.Clear();
        _passHandlers.Clear();
        _bufferHandles.Clear();
        _textureHandles.Clear();
        _passHandles.Clear();
        _device = default;
        _queue = default;
    }

    void IAddon.OnUninitialize(World world) => Dispose();

    private long NextRegistrationId() => ++_nextRegistrationId;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
