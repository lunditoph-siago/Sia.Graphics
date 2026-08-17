using Sia.Math;

namespace Sia.Graphics.Scene;

public sealed record MeshData(MeshVertex[] Vertices, uint[] Indices, Aabb Bounds);
