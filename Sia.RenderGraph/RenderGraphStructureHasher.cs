namespace Sia.RenderGraph;

internal static class RenderGraphStructureHasher
{
    public static RenderGraphStructureHash Compute(
        RenderGraphDefinition definition)
    {
        var hash = new StableHash64();
        hash.Add(1u);
        hash.Add((uint)definition.BufferCount);
        foreach (var buffer in definition.Buffers)
        {
            hash.Add(buffer.Descriptor.Size);
            hash.Add((ulong)buffer.Descriptor.Usage);
            hash.Add(buffer.IsImported);
            hash.Add(buffer.IsExported);
            hash.Add((ulong)buffer.ExportUsage);
        }

        hash.Add((uint)definition.TextureCount);
        foreach (var texture in definition.Textures)
        {
            hash.Add((uint)texture.Descriptor.Format);
            hash.Add(texture.Descriptor.Width);
            hash.Add(texture.Descriptor.Height);
            hash.Add(texture.Descriptor.DepthOrArrayLayers);
            hash.Add(texture.Descriptor.MipLevelCount);
            hash.Add(texture.Descriptor.SampleCount);
            hash.Add((uint)texture.Descriptor.Dimension);
            hash.Add((ulong)texture.Descriptor.Usage);
            hash.Add(texture.IsImported);
            hash.Add(texture.IsExported);
            hash.Add((ulong)texture.ExportUsage);
        }

        hash.Add((uint)definition.PassCount);
        foreach (var pass in definition.Passes)
        {
            hash.Add((uint)pass.Buffers.Length);
            foreach (var buffer in pass.Buffers)
            {
                hash.Add((uint)buffer.BufferIndex);
                hash.Add((uint)buffer.Access);
                hash.Add((ulong)buffer.Usage);
                hash.Add(buffer.Range.Offset);
                hash.Add(buffer.Range.Size);
            }

            hash.Add((uint)pass.Textures.Length);
            foreach (var texture in pass.Textures)
            {
                hash.Add((uint)texture.TextureIndex);
                hash.Add((uint)texture.Access);
                hash.Add((ulong)texture.Usage);
                hash.Add(texture.Subresources.BaseMipLevel);
                hash.Add(texture.Subresources.MipLevelCount);
                hash.Add(texture.Subresources.BaseArrayLayer);
                hash.Add(texture.Subresources.ArrayLayerCount);
                hash.Add((uint)texture.Subresources.Aspect);
            }

            hash.Add((uint)pass.Dependencies.Length);
            foreach (var dependency in pass.Dependencies)
            {
                hash.Add((uint)dependency);
            }
        }

        return new RenderGraphStructureHash(hash.Value);
    }

    private struct StableHash64
    {
        private const ulong OffsetBasis = 14695981039346656037;
        private const ulong Prime = 1099511628211;

        private ulong _value;

        public readonly ulong Value => _value == 0 ? OffsetBasis : _value;

        public void Add(bool value) => Add(value ? 1u : 0u);

        public void Add(uint value) => Add((ulong)value);

        public void Add(ulong value)
        {
            if (_value == 0)
            {
                _value = OffsetBasis;
            }

            for (var index = 0; index < sizeof(ulong); index++)
            {
                _value ^= (byte)(value >> (index * 8));
                _value *= Prime;
            }
        }
    }
}
