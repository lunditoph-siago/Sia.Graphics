namespace Sia.Graphics.UI;

internal static class UiVertexSourceFactory
{
    public static IUiVertexSource Create() =>
#if SIA_WEBGPU_BACKEND_WGPU
        new CompatVertexSource();
#else
        new StorageBufferVertexSource();
#endif
}
