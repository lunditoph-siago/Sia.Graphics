using System.Runtime.InteropServices;

namespace Sia.WebGPU;

public static unsafe partial class Wgpu
{
    public static WgpuBackendKind Backend {
        get {
#if BROWSER
            return (WgpuBackendKind)SiaWebGpuGetBackend();
#else
            return WgpuBackendKind.Native;
#endif
        }
    }

#if BROWSER
    [DllImport(
        "__Internal_emscripten",
        EntryPoint = "siaWebGpuGetBackend",
        ExactSpelling = true)]
    private static extern uint SiaWebGpuGetBackend();

    [DllImport(
        "__Internal_emscripten",
        EntryPoint = "siaWebGpuSurfacePresent",
        ExactSpelling = true)]
    private static extern WGPUStatus SiaWebGpuSurfacePresent(WGPUSurface* surface);
#endif
}
