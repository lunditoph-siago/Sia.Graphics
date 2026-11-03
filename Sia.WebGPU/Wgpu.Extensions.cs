using System.Runtime.InteropServices;

namespace Sia.WebGPU;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct WGPUEmscriptenSurfaceSourceCanvasHTMLSelector
{
    public WGPUChainedStruct Chain;
    public WGPUStringView Selector;

    public static WGPUEmscriptenSurfaceSourceCanvasHTMLSelector Default => new() {
        Chain = new() { SType = (WGPUSType)0x00040000 },
        Selector = WGPUStringView.Default,
    };
}
