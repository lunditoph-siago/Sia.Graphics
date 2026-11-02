#if BROWSER
global using unsafe WgpuCallbackMessage = Sia.WebGPU.WGPUStringView*;
#else
global using WgpuCallbackMessage = Sia.WebGPU.WGPUStringView;
#endif

namespace Sia.WebGPU;

public static unsafe partial class Wgpu
{
    internal static WGPUStringView ReadCallbackMessage(WgpuCallbackMessage message) =>
#if BROWSER
        message == null ? default : *message;
#else
        message;
#endif
}
