using System.Runtime.InteropServices;

namespace Sia.WebGPU;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct WGPUEmscriptenSurfaceSourceCanvasHTMLSelector
{
    public WGPUChainedStruct Chain;
    public WGPUStringView Selector;
}
