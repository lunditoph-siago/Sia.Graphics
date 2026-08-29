using Sia.Spirv;

namespace Sia.Spirv.Compiler.Tests;

internal readonly struct FullscreenVertexInput(uint vertexIndex, uint instanceIndex)
{
    [VertexIndex]
    public readonly uint VertexIndex = vertexIndex;

    [InstanceIndex]
    public readonly uint InstanceIndex = instanceIndex;
}
