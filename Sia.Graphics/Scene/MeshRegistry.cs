using System.Runtime.InteropServices;
using Sia;

namespace Sia.Graphics.Scene;

public sealed class MeshRegistry : IAddon
{
    private readonly List<MeshData> _meshes = [];
    private readonly Dictionary<ulong, MeshHandle> _byContentHash = [];

    public MeshHandle Register(MeshData data)
    {
        var hash = ComputeContentHash(data);
        if (_byContentHash.TryGetValue(hash, out var existing)) {
            return existing;
        }

        var handle = new MeshHandle(_meshes.Count);
        _meshes.Add(data);
        _byContentHash.Add(hash, handle);
        return handle;
    }

    public MeshData Get(MeshHandle handle)
    {
        if (!handle.IsValid || handle.Id >= _meshes.Count) {
            throw new ArgumentException("The mesh handle is not registered.", nameof(handle));
        }
        return _meshes[handle.Id];
    }

    private static ulong ComputeContentHash(MeshData data)
    {
        var hash = new StableHash64();
        hash.Add(MemoryMarshal.AsBytes(data.Vertices.AsSpan()));
        hash.Add(MemoryMarshal.AsBytes(data.Indices.AsSpan()));
        return hash.Value;
    }

    private struct StableHash64
    {
        private const ulong OffsetBasis = 14695981039346656037;
        private const ulong Prime = 1099511628211;

        private ulong _value;

        public readonly ulong Value => _value == 0 ? OffsetBasis : _value;

        public void Add(ReadOnlySpan<byte> bytes)
        {
            if (_value == 0) {
                _value = OffsetBasis;
            }
            foreach (var b in bytes) {
                _value ^= b;
                _value *= Prime;
            }
        }
    }
}
