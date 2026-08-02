namespace Sia.RenderGraph;

[Flags]
public enum RenderGraphBufferUsage : ulong
{
    None = 0,
    MapRead = 1 << 0,
    MapWrite = 1 << 1,
    CopySource = 1 << 2,
    CopyDestination = 1 << 3,
    Index = 1 << 4,
    Vertex = 1 << 5,
    Uniform = 1 << 6,
    Storage = 1 << 7,
    Indirect = 1 << 8,
    QueryResolve = 1 << 9
}
