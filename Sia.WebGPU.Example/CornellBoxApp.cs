using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Sia.GLFW;
using Sia.Graphics.Reactive;
using Sia.Input;
using Sia.Window;

namespace Sia.WebGPU.Example;

internal sealed unsafe partial class CornellBoxApp : IDisposable
{
    private const int _initialWidth = 1280;
    private const int _initialHeight = 720;
    private const int _uniformSize = 80;
    private const string _rasterShaderArtifactName =
        "Sia.WebGPU.Example.CornellBoxRasterShaders.FullscreenVertex";

    private readonly WgpuHandle<WGPUTexture>[] _accumulationTextures = new WgpuHandle<WGPUTexture>[2];
    private readonly HashSet<Key> _pressedKeys = [];

    private GlfwWindow _window;
    private WgpuHandle<WGPUInstance> _instance;
    private WgpuHandle<WGPUSurface> _surface;
    private WgpuHandle<WGPUAdapter> _adapter;
    private WgpuHandle<WGPUDevice> _device;
    private WgpuHandle<WGPUQueue> _queue;
    private WgpuHandle<WGPUBuffer> _uniformBuffer;
    private WgpuHandle<WGPUBindGroupLayout> _bindGroupLayout;
    private WgpuHandle<WGPUPipelineLayout> _pipelineLayout;
    private WgpuHandle<WGPURenderPipeline> _pathPipeline;
    private WgpuHandle<WGPURenderPipeline> _presentationPipeline;
#if BROWSER
    private byte[]? _browserVertexSpirv;
#endif

    private WGPUTextureFormat _surfaceFormat;
    private WGPUCompositeAlphaMode _alphaMode;
    private WGPUPresentMode _presentMode;
    private int _framebufferWidth;
    private int _framebufferHeight;
    private int _readIndex;
    private uint _frameIndex;
    private bool _glfwInitialized;
    private bool _surfaceConfigured;
    private bool _disposed;

    private float _cameraYaw;
    private float _cameraPitch = -0.035f;
    private float _cameraDistance = 3.35f;
    private float _exposure = 1.15f;
    private float _aperture = 0.012f;
    private float _focusDistance = 3.35f;
    private int _samplesPerFrame = 2;
    private int _maxBounces = 7;
    private double _lastTitleUpdate;

    public void Run()
    {
        Initialize();

        Console.WriteLine("Cornell Box path tracer controls:");
        Console.WriteLine("  arrows orbit | W/S dolly | -/= exposure | [/] samples");
        Console.WriteLine("  ,/. bounces | O/P aperture | R reset | Esc close");

        var clock = Stopwatch.StartNew();
        var previousTime = clock.Elapsed.TotalSeconds;

        while (!Glfw.ShouldClose(_window)) {
            Glfw.PollEvents();

            var currentTime = clock.Elapsed.TotalSeconds;
            var deltaTime = (float)System.Math.Min(currentTime - previousTime, 0.1);
            previousTime = currentTime;

            HandleInput(deltaTime);
            UpdateUi();
            if (!ResizeIfNeeded()) {
                Thread.Sleep(16);
                continue;
            }

            RenderFrame();
            Wgpu.ProcessEvents(_instance);
            UpdateWindowTitle(currentTime);
        }
    }

    private void Initialize()
    {
        Glfw.Initialize();
        _glfwInitialized = true;
        _window = Glfw.CreateWindow(
            new WindowDescriptor(
                _initialWidth,
                _initialHeight,
                "Sia.WebGPU · Cornell Box Path Tracer",
                Resizable: true),
            new GlfwWindowOptions(ClientApi.NoApi));

        _instance = OperatingSystem.IsBrowser()
            ? Wgpu.CreateInstance()
            : Wgpu.CreateSpirvInstance();
        _surface = CreateSurface(_instance, _window);

        var adapterOptions = BuildAdapterOptions();
        _adapter = Wgpu.RequestAdapter(_instance, in adapterOptions);

        var surfaceInfo = GetSurfaceInfo(_surface, _adapter);
        _surfaceFormat = surfaceInfo.Format;
        _alphaMode = surfaceInfo.AlphaMode;
        _presentMode = surfaceInfo.PresentMode;

        _device = Wgpu.RequestDevice(_adapter);
        _queue = Wgpu.GetQueue(_device);

        CreateUniformBuffer();
        CreatePipelines();
        InitializeUi();
        InitializeRenderGraph();
        ResizeIfNeeded(force: true);
    }

    private WGPURequestAdapterOptions BuildAdapterOptions(
        WGPUFeatureLevel featureLevel = WGPUFeatureLevel.Core,
        WGPUPowerPreference powerPreference = WGPUPowerPreference.HighPerformance) => new() {
        NextInChain = null,
        FeatureLevel = featureLevel,
        PowerPreference = powerPreference,
        ForceFallbackAdapter = 0,
        BackendType = WGPUBackendType.Undefined,
        CompatibleSurface = Pointer(_surface),
    };

    private static WgpuHandle<WGPUSurface> CreateSurface(
        WgpuHandle<WGPUInstance> instance,
        GlfwWindow window)
    {
#if BROWSER
        return Wgpu.CreateCanvasSurface(instance, "#canvas", "Cornell Box surface");
#else
        if (OperatingSystem.IsWindows()) {
            return Wgpu.CreateWindowsSurface(
                instance,
                GlfwPlatformNative.GetCurrentWin32ModuleHandle(),
                Glfw.GetWin32Window(window),
                "Cornell Box surface");
        }

        if (OperatingSystem.IsLinux()) {
            var waylandDisplay = Glfw.GetWaylandDisplay();
            if (waylandDisplay != 0) {
                return Wgpu.CreateWaylandSurface(
                    instance,
                    waylandDisplay,
                    Glfw.GetWaylandWindow(window),
                    "Cornell Box surface");
            }

            return Wgpu.CreateXlibSurface(
                instance,
                Glfw.GetX11Display(),
                (ulong)Glfw.GetX11Window(window),
                "Cornell Box surface");
        }

        throw new PlatformNotSupportedException(
            "This example currently creates WebGPU surfaces for Win32, X11, and Wayland.");
#endif
    }

    private static SurfaceInfo GetSurfaceInfo(
        WgpuHandle<WGPUSurface> surface,
        WgpuHandle<WGPUAdapter> adapter)
    {
        var capabilities = default(WGPUSurfaceCapabilities);
        var status = WgpuUnsafe.wgpuSurfaceGetCapabilities(
            Pointer(surface),
            Pointer(adapter),
            &capabilities);
        if (status != WGPUStatus.Success) {
            throw new WgpuException($"Surface capability query failed with status {status}.");
        }

        try {
#if !BROWSER
            if ((capabilities.Usages & WGPUTextureUsage.RenderAttachment) == 0) {
                throw new WgpuException("The selected surface cannot be used as a render attachment.");
            }
#endif
            if (capabilities.FormatCount == 0) {
                throw new WgpuException("The selected surface exposes no texture formats.");
            }

            var format = PickSurfaceFormat(in capabilities);
            var alphaMode = PickAlphaMode(in capabilities);
            var presentMode = PickPresentMode(in capabilities);
            return new SurfaceInfo(format, alphaMode, presentMode);
        }
        finally {
            WgpuUnsafe.wgpuSurfaceCapabilitiesFreeMembers(capabilities);
        }
    }

    private static WGPUTextureFormat PickSurfaceFormat(in WGPUSurfaceCapabilities capabilities)
    {
        WGPUTextureFormat[] preferredFormats = [
            WGPUTextureFormat.BGRA8Unorm,
            WGPUTextureFormat.RGBA8Unorm,
            WGPUTextureFormat.BGRA8UnormSrgb,
            WGPUTextureFormat.RGBA8UnormSrgb,
        ];

        foreach (var preferred in preferredFormats) {
            for (nuint index = 0; index < capabilities.FormatCount; index++) {
                if (capabilities.Formats[index] == preferred) {
                    return preferred;
                }
            }
        }

        return capabilities.Formats[0];
    }

    private static WGPUCompositeAlphaMode PickAlphaMode(in WGPUSurfaceCapabilities capabilities)
    {
        for (nuint index = 0; index < capabilities.AlphaModeCount; index++) {
            if (capabilities.AlphaModes[index] == WGPUCompositeAlphaMode.Opaque) {
                return WGPUCompositeAlphaMode.Opaque;
            }
        }

        return capabilities.AlphaModeCount == 0
            ? WGPUCompositeAlphaMode.Auto
            : capabilities.AlphaModes[0];
    }

    private static WGPUPresentMode PickPresentMode(in WGPUSurfaceCapabilities capabilities)
    {
        for (nuint index = 0; index < capabilities.PresentModeCount; index++) {
            if (capabilities.PresentModes[index] == WGPUPresentMode.Fifo) {
                return WGPUPresentMode.Fifo;
            }
        }

        return capabilities.PresentModeCount == 0
            ? WGPUPresentMode.Fifo
            : capabilities.PresentModes[0];
    }

    private void CreateUniformBuffer()
    {
        var descriptor = new WGPUBufferDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst,
            Size = _uniformSize,
            MappedAtCreation = 0,
        };
        _uniformBuffer = Wgpu.CreateBuffer(_device, in descriptor);
    }

    private void CreatePipelines()
    {
        var entries = stackalloc WGPUBindGroupLayoutEntry[2];
        entries[0] = new WGPUBindGroupLayoutEntry {
            NextInChain = null,
            Binding = 0,
            Visibility = WGPUShaderStage.Fragment,
            BindingArraySize = 0,
            Buffer = new WGPUBufferBindingLayout {
                NextInChain = null,
                Type = WGPUBufferBindingType.Uniform,
                HasDynamicOffset = 0,
                MinBindingSize = _uniformSize,
            },
            Sampler = default,
            Texture = default,
            StorageTexture = default,
        };
        entries[1] = new WGPUBindGroupLayoutEntry {
            NextInChain = null,
            Binding = 1,
            Visibility = WGPUShaderStage.Fragment,
            BindingArraySize = 0,
            Buffer = default,
            Sampler = default,
            Texture = new WGPUTextureBindingLayout {
                NextInChain = null,
                SampleType = WGPUTextureSampleType.UnfilterableFloat,
                ViewDimension = WGPUTextureViewDimension._2D,
                Multisampled = 0,
            },
            StorageTexture = default,
        };

        var bindGroupLayoutDescriptor = new WGPUBindGroupLayoutDescriptor {
            NextInChain = null,
            Label = default,
            EntryCount = 2,
            Entries = entries,
        };
        _bindGroupLayout = Wgpu.CreateBindGroupLayout(_device, in bindGroupLayoutDescriptor);

        var layouts = stackalloc WGPUBindGroupLayout*[1];
        layouts[0] = Pointer(_bindGroupLayout);
        var pipelineLayoutDescriptor = new WGPUPipelineLayoutDescriptor {
            NextInChain = null,
            Label = default,
            BindGroupLayoutCount = 1,
            BindGroupLayouts = layouts,
            ImmediateSize = 0,
        };
        _pipelineLayout = Wgpu.CreatePipelineLayout(_device, in pipelineLayoutDescriptor);

        var fragmentShader = Wgpu.CreateWgslShaderModule(
            _device,
            CornellBoxShaders.Source,
            "Cornell Box path tracer");
        var vertexShader = default(WgpuHandle<WGPUShaderModule>);
        try {
#if BROWSER
            vertexShader = Wgpu.CreateSpirvShaderModule(
                _device,
                _browserVertexSpirv ?? throw new InvalidOperationException(
                    "The browser SPIR-V shader was not loaded."),
                "C# full-screen vertex shader");
            _pathPipeline = CreateRenderPipeline(
                vertexShader,
                nameof(CornellBoxRasterShaders.FullscreenVertex),
                fragmentShader,
                "path_main",
                WGPUTextureFormat.RGBA16Float);
            _presentationPipeline = CreateRenderPipeline(
                vertexShader,
                nameof(CornellBoxRasterShaders.FullscreenVertex),
                fragmentShader,
                "present_main",
                _surfaceFormat);
#else
            vertexShader = CreateCSharpVertexShader();
            _pathPipeline = CreateRenderPipeline(
                vertexShader,
                nameof(CornellBoxRasterShaders.FullscreenVertex),
                fragmentShader,
                "path_main",
                WGPUTextureFormat.RGBA16Float);
            _presentationPipeline = CreateRenderPipeline(
                vertexShader,
                nameof(CornellBoxRasterShaders.FullscreenVertex),
                fragmentShader,
                "present_main",
                _surfaceFormat);
#endif
        }
        finally {
            Wgpu.Release(ref vertexShader);
            Wgpu.Release(ref fragmentShader);
        }
    }

    private WgpuHandle<WGPURenderPipeline> CreateRenderPipeline(
        WgpuHandle<WGPUShaderModule> vertexShader,
        string vertexEntryPoint,
        WgpuHandle<WGPUShaderModule> fragmentShader,
        string fragmentEntryPoint,
        WGPUTextureFormat targetFormat)
    {
        using var vertexEntry = new NativeString(vertexEntryPoint);
        using var fragmentEntry = new NativeString(fragmentEntryPoint);

        var colorTarget = new WGPUColorTargetState {
            NextInChain = null,
            Format = targetFormat,
            Blend = null,
            WriteMask = WGPUColorWriteMask.All,
        };
        var fragment = new WGPUFragmentState {
            NextInChain = null,
            Module = Pointer(fragmentShader),
            EntryPoint = fragmentEntry.View,
            ConstantCount = 0,
            Constants = null,
            TargetCount = 1,
            Targets = &colorTarget,
        };
        var descriptor = new WGPURenderPipelineDescriptor {
            NextInChain = null,
            Label = default,
            Layout = Pointer(_pipelineLayout),
            Vertex = new WGPUVertexState {
                NextInChain = null,
                Module = Pointer(vertexShader),
                EntryPoint = vertexEntry.View,
                ConstantCount = 0,
                Constants = null,
                BufferCount = 0,
                Buffers = null,
            },
            Primitive = new WGPUPrimitiveState {
                NextInChain = null,
                Topology = WGPUPrimitiveTopology.TriangleList,
                StripIndexFormat = WGPUIndexFormat.Undefined,
                FrontFace = WGPUFrontFace.CCW,
                CullMode = WGPUCullMode.None,
                UnclippedDepth = 0,
            },
            DepthStencil = null,
            Multisample = new WGPUMultisampleState {
                NextInChain = null,
                Count = 1,
                Mask = uint.MaxValue,
                AlphaToCoverageEnabled = 0,
            },
            Fragment = &fragment,
        };
        return Wgpu.CreateRenderPipeline(_device, in descriptor);
    }

#if !BROWSER
    private WgpuHandle<WGPUShaderModule> CreateCSharpVertexShader()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "spirv");
        var spirv = File.ReadAllBytes(Path.Combine(directory, _rasterShaderArtifactName + ".spv"));
        return Wgpu.CreateSpirvShaderModule(
            _device,
            spirv,
            "C# full-screen vertex shader");
    }
#endif

    private bool ResizeIfNeeded(bool force = false)
    {
        var size = Glfw.GetFramebufferSize(_window);
        if (size.Width <= 0 || size.Height <= 0) {
            return false;
        }
        if (!force && size.Width == _framebufferWidth && size.Height == _framebufferHeight) {
            return true;
        }

        ReleaseSamplingBindGroups();
        ReleaseAccumulationResources();
        _framebufferWidth = size.Width;
        _framebufferHeight = size.Height;

        var configuration = new WGPUSurfaceConfiguration {
            NextInChain = null,
            Device = Pointer(_device),
            Format = _surfaceFormat,
            Usage = WGPUTextureUsage.RenderAttachment,
            Width = (uint)_framebufferWidth,
            Height = (uint)_framebufferHeight,
            ViewFormatCount = 0,
            ViewFormats = null,
            AlphaMode = _alphaMode,
            PresentMode = _presentMode,
        };
        Wgpu.ConfigureSurface(_surface, in configuration);
        _surfaceConfigured = true;

        CreateAccumulationResources();
        ResetAccumulation();
        return true;
    }

    private void CreateAccumulationResources()
    {
        var textureDescriptor = new WGPUTextureDescriptor {
            NextInChain = null,
            Label = default,
            Usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.RenderAttachment,
            Dimension = WGPUTextureDimension._2D,
            Size = new WGPUExtent3D {
                Width = (uint)_framebufferWidth,
                Height = (uint)_framebufferHeight,
                DepthOrArrayLayers = 1,
            },
            Format = WGPUTextureFormat.RGBA16Float,
            MipLevelCount = 1,
            SampleCount = 1,
            ViewFormatCount = 0,
            ViewFormats = null,
        };

        for (var index = 0; index < 2; index++) {
            _accumulationTextures[index] = Wgpu.CreateTexture(_device, in textureDescriptor);
        }
    }

    private WgpuHandle<WGPUBindGroup> CreateSamplingBindGroup(
        WgpuHandle<WGPUTextureView> textureView)
    {
        var entries = stackalloc WGPUBindGroupEntry[2];
        entries[0] = new WGPUBindGroupEntry {
            NextInChain = null,
            Binding = 0,
            Buffer = Pointer(_uniformBuffer),
            Offset = 0,
            Size = _uniformSize,
            Sampler = null,
            TextureView = null,
        };
        entries[1] = new WGPUBindGroupEntry {
            NextInChain = null,
            Binding = 1,
            Buffer = null,
            Offset = 0,
            Size = 0,
            Sampler = null,
            TextureView = Pointer(textureView),
        };
        var descriptor = new WGPUBindGroupDescriptor {
            NextInChain = null,
            Label = default,
            Layout = Pointer(_bindGroupLayout),
            EntryCount = 2,
            Entries = entries,
        };
        return Wgpu.CreateBindGroup(_device, in descriptor);
    }

    private void RenderFrame()
    {
        var surfaceTexture = Wgpu.AcquireSurfaceTexture(_surface);
        if (surfaceTexture.Status is not (
            WGPUSurfaceGetCurrentTextureStatus.SuccessOptimal
            or WGPUSurfaceGetCurrentTextureStatus.SuccessSuboptimal)) {
            if (surfaceTexture.HasTexture) {
                Wgpu.Release(ref surfaceTexture);
            }

            if (surfaceTexture.Status is WGPUSurfaceGetCurrentTextureStatus.Outdated
                or WGPUSurfaceGetCurrentTextureStatus.Lost) {
                ResizeIfNeeded(force: true);
                return;
            }
            if (surfaceTexture.Status == WGPUSurfaceGetCurrentTextureStatus.Timeout) {
                return;
            }

            throw new WgpuException($"Surface texture acquisition failed with status {surfaceTexture.Status}.");
        }

        try {
            WriteUniforms();

            var writeIndex = 1 - _readIndex;
            UpdateRenderGraph(surfaceTexture.Texture, writeIndex);
            _renderGraphWorld!.ExecuteWgpuRenderGraph();
            EndSamplingBindGroupFrame();

#if !BROWSER
            Wgpu.PresentSurfaceOrThrow(_surface);
#endif
            _readIndex = writeIndex;
            _frameIndex++;
        }
        finally {
            Wgpu.Release(ref surfaceTexture);
        }
    }

    private void WriteUniforms()
    {
        var target = new Vector3(0.0f, 0.96f, 0.0f);
        var horizontalDistance = MathF.Cos(_cameraPitch) * _cameraDistance;
        var position = target + new Vector3(
            MathF.Sin(_cameraYaw) * horizontalDistance,
            MathF.Sin(_cameraPitch) * _cameraDistance,
            MathF.Cos(_cameraYaw) * horizontalDistance);

        var uniforms = new PathTracerUniforms {
            CameraPositionExposure = new Vector4(position, _exposure),
            CameraTargetFov = new Vector4(target, MathF.PI / 4.0f),
            ResolutionFrame = new Vector4(
                _framebufferWidth,
                _framebufferHeight,
                _frameIndex,
                0.0f),
            RenderSettings = new Vector4(
                _samplesPerFrame,
                _maxBounces,
                _aperture,
                _focusDistance),
            PresentationSettings = new Vector4(IsSrgb(_surfaceFormat) ? 1.0f : 0.0f, 0.0f, 0.0f, 0.0f),
        };
        Wgpu.WriteBuffer(_queue, _uniformBuffer, 0, [uniforms]);
    }

    private void HandleInput(float deltaTime)
    {
        if (IsDown(Key.Escape)) {
            Glfw.RequestClose(_window);
        }

        var cameraChanged = false;
        var orbitStep = 0.8f * deltaTime;
        var dollyStep = 1.25f * deltaTime;

        if (IsDown(Key.Left)) {
            _cameraYaw -= orbitStep;
            cameraChanged = true;
        }
        if (IsDown(Key.Right)) {
            _cameraYaw += orbitStep;
            cameraChanged = true;
        }
        if (IsDown(Key.Up)) {
            _cameraPitch = System.Math.Clamp(_cameraPitch + orbitStep, -0.42f, 0.42f);
            cameraChanged = true;
        }
        if (IsDown(Key.Down)) {
            _cameraPitch = System.Math.Clamp(_cameraPitch - orbitStep, -0.42f, 0.42f);
            cameraChanged = true;
        }
        if (IsDown(Key.W)) {
            _cameraDistance = System.Math.Clamp(_cameraDistance - dollyStep, 2.45f, 5.0f);
            if (_lockFocus) {
                _focusDistance = _cameraDistance;
            }
            cameraChanged = true;
        }
        if (IsDown(Key.S)) {
            _cameraDistance = System.Math.Clamp(_cameraDistance + dollyStep, 2.45f, 5.0f);
            if (_lockFocus) {
                _focusDistance = _cameraDistance;
            }
            cameraChanged = true;
        }

        var increaseExposure = Pressed(Key.Equal) | Pressed(Key.KeypadAdd);
        var decreaseExposure = Pressed(Key.Minus) | Pressed(Key.KeypadSubtract);
        if (increaseExposure) {
            _exposure = System.Math.Clamp(_exposure + 0.1f, 0.2f, 4.0f);
        }
        if (decreaseExposure) {
            _exposure = System.Math.Clamp(_exposure - 0.1f, 0.2f, 4.0f);
        }

        var settingsChanged = false;
        if (Pressed(Key.RightBracket)) {
            _samplesPerFrame = System.Math.Clamp(_samplesPerFrame + 1, 1, 8);
            settingsChanged = true;
        }
        if (Pressed(Key.LeftBracket)) {
            _samplesPerFrame = System.Math.Clamp(_samplesPerFrame - 1, 1, 8);
            settingsChanged = true;
        }
        if (Pressed(Key.Period)) {
            _maxBounces = System.Math.Clamp(_maxBounces + 1, 1, 12);
            settingsChanged = true;
        }
        if (Pressed(Key.Comma)) {
            _maxBounces = System.Math.Clamp(_maxBounces - 1, 1, 12);
            settingsChanged = true;
        }
        if (Pressed(Key.P)) {
            _aperture = System.Math.Clamp(_aperture + 0.003f, 0.0f, 0.05f);
            settingsChanged = true;
        }
        if (Pressed(Key.O)) {
            _aperture = System.Math.Clamp(_aperture - 0.003f, 0.0f, 0.05f);
            settingsChanged = true;
        }

        if (cameraChanged || settingsChanged || Pressed(Key.R)) {
            ResetAccumulation();
        }
    }

    private bool IsDown(Key key) => Glfw.GetKey(_window, key) != InputAction.Release;

    private bool Pressed(Key key)
    {
        if (IsDown(key)) {
            return _pressedKeys.Add(key);
        }

        _pressedKeys.Remove(key);
        return false;
    }

    private void ResetAccumulation()
    {
        _frameIndex = 0;
    }

    private void UpdateWindowTitle(double currentTime)
    {
        if (currentTime - _lastTitleUpdate < 0.25) {
            return;
        }

        _lastTitleUpdate = currentTime;
        Glfw.SetTitle(
            _window,
            $"Cornell Box · {_frameIndex * (uint)_samplesPerFrame:N0} spp · "
                + $"{_maxBounces} bounces · exposure {_exposure:0.0} · aperture {_aperture:0.000}");
    }

    private void ReleaseAccumulationResources()
    {
        for (var index = 0; index < 2; index++) {
            Wgpu.Release(ref _accumulationTextures[index]);
        }
    }

    public void Dispose()
    {
        if (_disposed) {
            return;
        }
        _disposed = true;

        ReleaseAccumulationResources();
        DisposeRenderGraph();
        DisposeUi();
        Wgpu.Release(ref _presentationPipeline);
        Wgpu.Release(ref _pathPipeline);
        Wgpu.Release(ref _pipelineLayout);
        Wgpu.Release(ref _bindGroupLayout);
        Wgpu.Release(ref _uniformBuffer);
        Wgpu.Release(ref _queue);

        if (!_surface.IsNull && _surfaceConfigured) {
            Wgpu.UnconfigureSurface(_surface);
            _surfaceConfigured = false;
        }
        if (!_device.IsNull) {
            Wgpu.DestroyDevice(_device);
        }
        Wgpu.Release(ref _device);
        Wgpu.Release(ref _adapter);
        Wgpu.Release(ref _surface);
        Wgpu.Release(ref _instance);

        if (!_window.IsNull) {
            Glfw.DestroyWindow(ref _window);
        }
        if (_glfwInitialized) {
            Glfw.Terminate();
            _glfwInitialized = false;
        }
    }

    private static bool IsSrgb(WGPUTextureFormat format) => format is
        WGPUTextureFormat.BGRA8UnormSrgb or WGPUTextureFormat.RGBA8UnormSrgb;

    private static T* Pointer<T>(WgpuHandle<T> handle)
        where T : unmanaged => (T*)handle.DangerousGetHandle();

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTracerUniforms
    {
        public Vector4 CameraPositionExposure;
        public Vector4 CameraTargetFov;
        public Vector4 ResolutionFrame;
        public Vector4 RenderSettings;
        public Vector4 PresentationSettings;
    }

    private readonly record struct SurfaceInfo(
        WGPUTextureFormat Format,
        WGPUCompositeAlphaMode AlphaMode,
        WGPUPresentMode PresentMode);

    private sealed class NativeString : IDisposable
    {
        private nint _memory;

        public NativeString(string value)
        {
            _memory = Marshal.StringToCoTaskMemUTF8(value);
            View = new WGPUStringView {
                Data = (byte*)_memory,
                Length = (nuint)Encoding.UTF8.GetByteCount(value),
            };
        }

        public WGPUStringView View { get; }

        public void Dispose()
        {
            if (_memory == 0) {
                return;
            }

            Marshal.FreeCoTaskMem(_memory);
            _memory = 0;
        }
    }
}
