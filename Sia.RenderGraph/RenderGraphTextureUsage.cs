namespace Sia.RenderGraph;

[Flags]
public enum RenderGraphTextureUsage : ulong
{
    None = 0,
    CopySource = 1 << 0,
    CopyDestination = 1 << 1,
    TextureBinding = 1 << 2,
    StorageBinding = 1 << 3,
    RenderAttachment = 1 << 4,
    TransientAttachment = 1 << 5
}
