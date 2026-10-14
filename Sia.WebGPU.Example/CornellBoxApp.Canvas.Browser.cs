#if BROWSER && SIA_WEBGPU_BACKEND_DAWN
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using Sia.Input;
using Sia.Window;

namespace Sia.WebGPU.Example;

internal sealed partial class CornellBoxApp
{
    private JSObject? _browserCanvas;
    private int _maxCanvasDimension;

    private async Task RunCanvasLoopAsync()
    {
        while (!ShouldClose()) {
            var frame = ParseCanvasFrame(await NextCanvasFrame(_browserCanvas!));
            await Task.Yield();
            ApplyCanvasFrame(frame);
            if (!RenderAnimationFrame(frame[0])) break;
        }
    }

    private static double[] ParseCanvasFrame(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(value => value.GetDouble()).ToArray();
    }

    private void ApplyCanvasFrame(double[] frame)
    {
        var window = _windowEntity!.Value;
        var previous = window.Get<WindowState>();
        var scale = frame[3];
        var size = new WindowSize(System.Math.Max(1, (int)frame[1]), System.Math.Max(1, (int)frame[2]));
        var framebuffer = new WindowSize(
            System.Math.Clamp((int)System.Math.Round(frame[1] * scale), 1, _maxCanvasDimension),
            System.Math.Clamp((int)System.Math.Round(frame[2] * scale), 1, _maxCanvasDimension));
        var next = new WindowState(size, framebuffer, false, new((float)framebuffer.Width / size.Width, (float)framebuffer.Height / size.Height));
        window.Get<WindowState>() = next;
        if (previous.Size != size) _windowWorld!.Send(window, new WindowEvents.Resized(size));
        if (previous.FramebufferSize != framebuffer) _windowWorld!.Send(window, new WindowEvents.FramebufferResized(framebuffer));
        if (previous.ContentScale != next.ContentScale) _windowWorld!.Send(window, new WindowEvents.ContentScaleChanged(next.ContentScale));
        UpdateUiInput(new(size.Width, size.Height), new(size.Width, size.Height));
        for (var i = 4; i + 4 < frame.Length; i += 5) {
            var x = frame[i + 1];
            var y = frame[i + 2];
            var code = (int)frame[i + 3];
            var mods = (KeyModifiers)(int)frame[i + 4];
            switch ((int)frame[i]) {
                case 0: _windowWorld!.Send(window, new InputEvents.MouseMoved(new(x, y))); break;
                case 1: _windowWorld!.Send(window, new InputEvents.MouseScrolled(new(x, y))); break;
                case 2: _windowWorld!.Send(window, new InputEvents.MouseEntered()); break;
                case 3: _windowWorld!.Send(window, new InputEvents.MouseExited()); break;
                case 4: _windowWorld!.Send(window, new InputEvents.MouseButtonPressed((MouseButton)code, mods)); break;
                case 5: _windowWorld!.Send(window, new InputEvents.MouseButtonReleased((MouseButton)code, mods)); break;
                case 6: _windowWorld!.Send(window, new InputEvents.KeyPressed((Key)code, 0, mods)); break;
                case 7: _windowWorld!.Send(window, new InputEvents.KeyRepeated((Key)code, 0, mods)); break;
                case 8: _windowWorld!.Send(window, new InputEvents.KeyReleased((Key)code, 0, mods)); break;
                case 9: _windowWorld!.Send(window, new InputEvents.TextEntered((uint)code)); break;
            }
        }
    }

    [JSImport("createCanvas", "main.js")]
    private static partial JSObject CreateBrowserCanvas();

    [JSImport("nextCanvasFrame", "main.js")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    private static partial Task<string> NextCanvasFrame(JSObject canvas);

    [JSImport("disposeCanvas", "main.js")]
    private static partial void DisposeBrowserCanvas(JSObject canvas);

    [JSImport("canvasPresentation", "main.js")]
    private static partial string GetCanvasPresentation();

    [JSImport("setTitle", "main.js")]
    private static partial void SetBrowserTitle(string title);
}
#endif
