using System.Reflection;
using Sia;
using Sia.GLFW;
using Sia.Graphics.Text;
using Sia.Graphics.UI;
using Sia.Input;
using Sia.Reactive;
using SiaReactive = Sia.Reactive.Reactive;
using UiText = Sia.Graphics.UI.Text;

namespace Sia.WebGPU.Example;

internal sealed unsafe partial class CornellBoxApp
{
    private static readonly Color _panelColor = new(0.035f, 0.045f, 0.065f, 0.94f);
    private static readonly Color _controlColor = new(0.12f, 0.15f, 0.21f, 0.96f);
    private static readonly Color _accentColor = new(0.18f, 0.46f, 0.82f, 1f);
    private static readonly Color _mutedTextColor = new(0.69f, 0.74f, 0.82f, 1f);

    private World? _uiWorld;
    private SystemStage? _uiLayoutStage;
    private SystemStage? _uiInteractionStage;
    private UiRenderer? _uiRenderer;
    private ReactiveMount<UiViewProps>? _uiMount;
    private bool _lockFocus = true;
    private float _uiFontSize = 16f;

    private void InitializeUi()
    {
        _uiWorld = new World();
        var device = _uiWorld.OwnWgpu(_device, IgnoreBorrowedRelease);
        var queue = _uiWorld.OwnWgpu(_queue, IgnoreBorrowedRelease);
        var pipeline = UiPipeline.Create(_uiWorld, device, queue, _surfaceFormat);
        _uiRenderer = new UiRenderer(pipeline);
        _uiWorld.AcquireAddon<UiChangeTracker>();

        _uiMount = _uiWorld.Mount(
            RenderUi,
            CreateUiProps(new Size(_initialWidth, _initialHeight)));
        _uiLayoutStage = SystemChain.Empty
            .AddReactiveUi()
            .CreateStage(_uiWorld);
        _uiInteractionStage = SystemChain.Empty
            .Add<UiHitTestSystem>()
            .Add<ButtonSystem>()
            .Add<SliderSystem>()
            .CreateStage(_uiWorld);

        _uiLayoutStage.Tick();
    }

    private static ReactiveNode RenderUi(in UiViewProps props, ref Hooks hooks)
    {
        var assets = hooks.UseRef(static () => UiAssets.Load()).Value;
        var root = ReactiveUiNode.Create(
            "root", null, 0,
            new Node {
                Width = Val.Percent(100f),
                Height = Val.Percent(100f)
            },
            HList.From(new UiRoot(props.Viewport)));
        var panel = ReactiveUiNode.Create(
            "panel", "root", 0,
            new Node {
                PositionType = PositionType.Absolute,
                Left = Val.Px(18f),
                Top = Val.Px(18f),
                Width = Val.Px(430f),
                Padding = UiRect.All(Val.Px(14f)),
                Border = UiRect.All(Val.Px(1f)),
                BorderRadius = BorderRadius.All(Val.Px(10f)),
                FlexDirection = FlexDirection.Column,
                RowGap = Val.Px(8f)
            },
            HList.From(
                new BackgroundColor(_panelColor),
                new BorderColor(new Color(0.24f, 0.31f, 0.43f, 1f)),
                new ZIndex(1000)));
        var title = SiaReactive.Component(
            RenderText,
            new TextProps(
                "title", 0, "Sia Graphics · UI Inspector", 22f, Color.White, assets, 34f));
        var status = SiaReactive.Component(
            RenderText,
            new TextProps(
                "status", 1,
                $"{props.SampleCount:N0} spp · {props.FramebufferWidth} × {props.FramebufferHeight}",
                14f, _mutedTextColor, assets, null));
        var preview = SiaReactive.Component(
            RenderText,
            new TextProps(
                "preview", 2,
                "Sia Graphics · 实时文字渲染\nArabic coverage: مرحبا · Devanagari coverage: नमस्ते",
                props.FontSize, Color.White, assets, 66f));

        var exposure = SiaReactive.Component(
            RenderControl,
            ControlProps.Slider(
                props.App, ControlKind.Exposure, "exposure", 3,
                $"Exposure / 曝光    {props.Exposure:0.0}",
                props.Exposure, 0.2f, 4f, 0.1f, assets));
        var samples = SiaReactive.Component(
            RenderControl,
            ControlProps.Slider(
                props.App, ControlKind.Samples, "samples", 4,
                $"Samples / 采样      {props.SamplesPerFrame}",
                props.SamplesPerFrame, 1f, 8f, 1f, assets));
        var bounces = SiaReactive.Component(
            RenderControl,
            ControlProps.Slider(
                props.App, ControlKind.Bounces, "bounces", 5,
                $"Bounces / 反弹      {props.MaxBounces}",
                props.MaxBounces, 1f, 12f, 1f, assets));
        var aperture = SiaReactive.Component(
            RenderControl,
            ControlProps.Slider(
                props.App, ControlKind.Aperture, "aperture", 6,
                $"Aperture / 光圈     {props.Aperture:0.000}",
                props.Aperture, 0f, 0.05f, 0.001f, assets));
        var focus = SiaReactive.Component(
            RenderControl,
            ControlProps.Slider(
                props.App, ControlKind.Focus, "focus", 7,
                $"Focus / 焦距        {props.FocusDistance:0.00}",
                props.FocusDistance, 2.45f, 5f, 0.05f, assets));
        var fontSize = SiaReactive.Component(
            RenderControl,
            ControlProps.Slider(
                props.App, ControlKind.FontSize, "font-size", 8,
                $"Font size / 字号    {props.FontSize:0}",
                props.FontSize, 12f, 28f, 1f, assets));
        var lockFocus = SiaReactive.Component(
            RenderControl,
            ControlProps.Button(
                props.App, ControlKind.LockFocus, "lock-focus", 9,
                props.LockFocus
                    ? "[x] Dolly follows focus / 焦距跟随"
                    : "[ ] Dolly follows focus / 焦距跟随",
                props.LockFocus ? _accentColor : _controlColor,
                assets));
        var reset = SiaReactive.Component(
            RenderControl,
            ControlProps.Button(
                props.App, ControlKind.Reset, "reset", 10,
                "Reset renderer / 重置渲染",
                new Color(0.48f, 0.18f, 0.22f, 1f),
                assets));

        return SiaReactive.Group(
            root, panel, title, status, preview,
            exposure, samples, bounces, aperture, focus, fontSize, lockFocus, reset);
    }

    private static ReactiveNode RenderControl(in ControlProps props, ref Hooks hooks)
    {
        var hovered = hooks.UseState(false);
        var pressed = hooks.UseState(false);
        var color = pressed.Value
            ? new Color(0.12f, 0.38f, 0.68f, 1f)
            : hovered.Value
                ? new Color(0.16f, 0.27f, 0.43f, 1f)
                : props.BaseColor;
        var interactions = SiaReactive.Group(
            SiaReactive.On<PointerEnter, State<bool>>(
                hovered,
                static (in PointerEnter _, in State<bool> state) => state.Set(true)),
            SiaReactive.On<PointerLeave, State<bool>>(
                hovered,
                static (in PointerLeave _, in State<bool> state) => state.Set(false)),
            SiaReactive.On<PointerPress, State<bool>>(
                pressed,
                static (in PointerPress _, in State<bool> state) => state.Set(true)),
            SiaReactive.On<PointerRelease, State<bool>>(
                pressed,
                static (in PointerRelease _, in State<bool> state) => state.Set(false)));

        if (props.IsSlider) {
            return ReactiveUiNode.Create(
                props.Key, "panel", props.Order, ControlNode(),
                HList.From(
                    new UiText(props.Label),
                    CreateStyle(props.Assets),
                    new BackgroundColor(color),
                    new BorderColor(new Color(0.25f, 0.31f, 0.42f, 1f)),
                    new SliderValue(props.Value),
                    new SliderRange(props.Min, props.Max),
                    new SliderStep(props.Step)),
                SiaReactive.Group(
                    interactions,
                    SiaReactive.On<SliderChanged, ControlCapture>(
                        new(props.App, props.Kind),
                        static (in SliderChanged message, in ControlCapture capture) =>
                            capture.App.SetUiControl(capture.Kind, message.Value))));
        }

        return ReactiveUiNode.Create(
            props.Key, "panel", props.Order, ControlNode(),
            HList.From(
                new UiText(props.Label),
                CreateStyle(props.Assets),
                new BackgroundColor(color),
                new Button()),
            SiaReactive.Group(
                interactions,
                SiaReactive.On<Activate, ControlCapture>(
                    new(props.App, props.Kind),
                    static (in Activate _, in ControlCapture capture) =>
                        capture.App.ActivateUiControl(capture.Kind))));

        static TextStyle CreateStyle(UiAssets assets) =>
            new(assets.Latin, 15f, Color.White) { FallbackFonts = assets.Fallbacks };
    }

    private static ReactiveNode RenderText(in TextProps props, ref Hooks hooks)
    {
        _ = hooks;
        return
        ReactiveUiNode.Create(
            props.Key, "panel", props.Order,
            new Node {
                Width = Val.Percent(100f),
                Height = props.Height is { } fixedHeight ? Val.Px(fixedHeight) : Val.Auto,
                FlexShrink = 0f
            },
            HList.From(
                new UiText(props.Value),
                new TextStyle(props.Assets.Latin, props.FontSize, props.Color) {
                    FallbackFonts = props.Assets.Fallbacks
                }));
    }

    private static Node ControlNode() => new() {
        Width = Val.Percent(100f),
        Height = Val.Px(34f),
        Padding = UiRect.Axes(Val.Px(9f), Val.Px(6f)),
        Border = UiRect.All(Val.Px(1f)),
        BorderRadius = BorderRadius.All(Val.Px(5f)),
        FlexShrink = 0f
    };

    private void UpdateUi()
    {
        if (_uiWorld is null || _uiMount is not { } mount
            || _uiLayoutStage is null || _uiInteractionStage is null) {
            return;
        }

        var framebufferSize = Glfw.GetFramebufferSize(_window);
        var windowSize = Glfw.GetSize(_window);
        if (framebufferSize.Width <= 0 || framebufferSize.Height <= 0
            || windowSize.Width <= 0 || windowSize.Height <= 0) {
            return;
        }

        var viewport = new Size(framebufferSize.Width, framebufferSize.Height);
        UpdateUiProps(mount, CreateUiProps(viewport));
        _uiWorld.FlushReactive();
        _uiLayoutStage.Tick();

        var cursor = Glfw.GetCursorPosition(_window);
        var pointer = _uiWorld.AcquireAddon<UiPointerState>();
        pointer.Position = new Point(
            (float)(cursor.X * framebufferSize.Width / windowSize.Width),
            (float)(cursor.Y * framebufferSize.Height / windowSize.Height));
        pointer.ButtonDown = Glfw.GetMouseButton(_window, MouseButton.Left) != InputAction.Release;
        _uiInteractionStage.Tick();

        UpdateUiProps(mount, CreateUiProps(viewport));
        _uiWorld.FlushReactive();
        _uiLayoutStage.Tick();
    }

    private static void UpdateUiProps(
        ReactiveMount<UiViewProps> mount,
        in UiViewProps props)
    {
        if (mount.Props != props) {
            mount.Update(props);
        }
    }

    private UiViewProps CreateUiProps(Size viewport) => new(
        this,
        viewport,
        _frameIndex * (uint)_samplesPerFrame,
        _framebufferWidth,
        _framebufferHeight,
        _exposure,
        _samplesPerFrame,
        _maxBounces,
        _aperture,
        _focusDistance,
        _uiFontSize,
        _lockFocus);

    private void SetUiControl(ControlKind kind, float value)
    {
        switch (kind) {
            case ControlKind.Exposure:
                _exposure = value;
                break;
            case ControlKind.Samples:
                _samplesPerFrame = (int)value;
                break;
            case ControlKind.Bounces:
                _maxBounces = (int)value;
                break;
            case ControlKind.Aperture:
                _aperture = value;
                break;
            case ControlKind.Focus:
                _focusDistance = value;
                break;
            case ControlKind.FontSize:
                _uiFontSize = value;
                break;
            default:
                return;
        }
        ResetAccumulation();
    }

    private void ActivateUiControl(ControlKind kind)
    {
        if (kind == ControlKind.LockFocus) {
            _lockFocus = !_lockFocus;
            return;
        }
        if (kind != ControlKind.Reset)
            return;

        _cameraYaw = 0f;
        _cameraPitch = -0.035f;
        _cameraDistance = 3.35f;
        _exposure = 1.15f;
        _aperture = 0.012f;
        _focusDistance = 3.35f;
        _samplesPerFrame = 2;
        _maxBounces = 7;
        _uiFontSize = 16f;
        ResetAccumulation();
    }

    private uint? PrepareUiFrame() =>
        _uiWorld is not null && _uiRenderer is not null
            ? _uiRenderer.PrepareFrame(
                _uiWorld,
                new Size(_framebufferWidth, _framebufferHeight))
            : null;

    private void EncodeUi(
        WgpuHandle<WGPUCommandEncoder> encoder,
        WgpuHandle<WGPUTextureView> surfaceView,
        uint? primitiveCount)
    {
        if (_uiRenderer is null || primitiveCount is null)
            return;

        _uiRenderer.EncodeDrawCalls(
            encoder,
            surfaceView,
            primitiveCount.Value,
            WGPULoadOp.Load);
    }

    private void DisposeUi()
    {
        if (_uiMount is { } mount && mount.IsMounted)
            mount.Unmount();
        _uiInteractionStage?.Dispose();
        _uiLayoutStage?.Dispose();
        _uiWorld?.Dispose();
        _uiMount = null;
        _uiInteractionStage = null;
        _uiLayoutStage = null;
        _uiWorld = null;
        _uiRenderer = null;
    }

    private static Font LoadEmbeddedFont(string fileName)
    {
        var assembly = typeof(CornellBoxApp).Assembly;
        var resourceName = $"Sia.WebGPU.Example.Fonts.{fileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded font '{resourceName}' was not found in {assembly.GetName().Name}.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return new Font(memory.ToArray());
    }

    private static void IgnoreBorrowedRelease<T>(ref WgpuHandle<T> handle)
        where T : unmanaged
    {
        _ = handle;
    }

    private sealed record UiAssets(Font Latin, Font[] Fallbacks)
    {
        public static UiAssets Load()
        {
            var latin = LoadEmbeddedFont("NotoSans-UiSubset.ttf");
            Font[] fallbacks = [
                LoadEmbeddedFont("NotoSansSC-UiSubset.ttf"),
                LoadEmbeddedFont("NotoSansArabic-UiSubset.ttf"),
                LoadEmbeddedFont("NotoSansDevanagari-UiSubset.ttf")
            ];
            return new(latin, fallbacks);
        }
    }

    private readonly record struct UiViewProps(
        CornellBoxApp App,
        Size Viewport,
        uint SampleCount,
        int FramebufferWidth,
        int FramebufferHeight,
        float Exposure,
        int SamplesPerFrame,
        int MaxBounces,
        float Aperture,
        float FocusDistance,
        float FontSize,
        bool LockFocus);

    private readonly record struct ControlProps(
        CornellBoxApp App,
        ControlKind Kind,
        string Key,
        int Order,
        string Label,
        float Value,
        float Min,
        float Max,
        float Step,
        Color BaseColor,
        UiAssets Assets,
        bool IsSlider)
    {
        public static ControlProps Slider(
            CornellBoxApp app,
            ControlKind kind,
            string key,
            int order,
            string label,
            float value,
            float min,
            float max,
            float step,
            UiAssets assets) =>
            new(app, kind, key, order, label, value, min, max, step, _controlColor, assets, true);

        public static ControlProps Button(
            CornellBoxApp app,
            ControlKind kind,
            string key,
            int order,
            string label,
            Color color,
            UiAssets assets) =>
            new(app, kind, key, order, label, 0f, 0f, 0f, 0f, color, assets, false);
    }

    private readonly record struct TextProps(
        string Key,
        int Order,
        string Value,
        float FontSize,
        Color Color,
        UiAssets Assets,
        float? Height);

    private readonly record struct ControlCapture(CornellBoxApp App, ControlKind Kind);

    private enum ControlKind
    {
        Exposure,
        Samples,
        Bounces,
        Aperture,
        Focus,
        FontSize,
        LockFocus,
        Reset
    }
}
