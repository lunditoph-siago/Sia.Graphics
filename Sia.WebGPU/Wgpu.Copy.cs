namespace Sia.WebGPU;

public static unsafe partial class Wgpu
{
    public static void WriteTexture<T>(
        WgpuHandle<WGPUQueue> queue,
        WgpuHandle<WGPUTexture> texture,
        ReadOnlySpan<T> data,
        in WGPUTexelCopyBufferLayout layout,
        in WGPUExtent3D size,
        uint mipLevel = 0,
        WGPUOrigin3D origin = default,
        WGPUTextureAspect aspect = WGPUTextureAspect.All)
        where T : unmanaged
    {
        var destination = new WGPUTexelCopyTextureInfo {
            Texture = GetPointer(texture),
            MipLevel = mipLevel,
            Origin = origin,
            Aspect = aspect,
        };
        WriteTexture(queue, in destination, data, in layout, in size);
    }

    public static void WriteTexture<T>(
        WgpuHandle<WGPUQueue> queue,
        in WGPUTexelCopyTextureInfo destination,
        ReadOnlySpan<T> data,
        in WGPUTexelCopyBufferLayout layout,
        in WGPUExtent3D size)
        where T : unmanaged
    {
        var byteSize = checked((nuint)data.Length * (nuint)sizeof(T));
        fixed (T* dataPointer = data)
        fixed (WGPUTexelCopyTextureInfo* destinationPointer = &destination)
        fixed (WGPUTexelCopyBufferLayout* layoutPointer = &layout)
        fixed (WGPUExtent3D* sizePointer = &size) {
            WgpuUnsafe.wgpuQueueWriteTexture(
                GetPointer(queue), destinationPointer, dataPointer, byteSize, layoutPointer, sizePointer);
        }
    }

    public static void CopyBufferToTexture(
        WgpuHandle<WGPUCommandEncoder> encoder,
        in WGPUTexelCopyBufferInfo source,
        in WGPUTexelCopyTextureInfo destination,
        in WGPUExtent3D size)
    {
#if BROWSER
        if (size.Width > 0 && size.Height > 0 && size.DepthOrArrayLayers > 1
            && source.Layout.RowsPerImage > size.Height && source.Layout.RowsPerImage != uint.MaxValue
            && source.Layout.BytesPerRow > 0 && source.Layout.BytesPerRow != uint.MaxValue
            && source.Buffer != null && destination.Texture != null && destination.MipLevel < 32
            && WgpuUnsafe.wgpuTextureGetDimension(destination.Texture) == WGPUTextureDimension._3D) {
            var depth = WgpuUnsafe.wgpuTextureGetDepthOrArrayLayers(destination.Texture) >> (int)destination.MipLevel;
            if (destination.Origin.Z <= depth && size.DepthOrArrayLayers <= depth - destination.Origin.Z) {
                CopySlices(GetPointer(encoder), source, destination, size);
                return;
            }
        }

        static void CopySlices(WGPUCommandEncoder* encoder, WGPUTexelCopyBufferInfo source,
            WGPUTexelCopyTextureInfo destination, WGPUExtent3D size)
        {
            var depth = size.DepthOrArrayLayers;
            var offset = source.Layout.Offset;
            var origin = destination.Origin.Z;
            var stride = (ulong)source.Layout.BytesPerRow * source.Layout.RowsPerImage;
            _ = checked(offset + stride * (depth - 1));
            _ = checked(origin + (depth - 1));
            size.DepthOrArrayLayers = 1;
            for (uint slice = 0; slice < depth; slice++) {
                source.Layout.Offset = offset + stride * slice;
                destination.Origin.Z = origin + slice;
                WgpuUnsafe.wgpuCommandEncoderCopyBufferToTexture(encoder, &source, &destination, &size);
            }
        }
#endif
        fixed (WGPUTexelCopyBufferInfo* sourcePointer = &source)
        fixed (WGPUTexelCopyTextureInfo* destinationPointer = &destination)
        fixed (WGPUExtent3D* sizePointer = &size) {
            WgpuUnsafe.wgpuCommandEncoderCopyBufferToTexture(
                GetPointer(encoder), sourcePointer, destinationPointer, sizePointer);
        }
    }

    public static void CopyTextureToBuffer(
        WgpuHandle<WGPUCommandEncoder> encoder,
        in WGPUTexelCopyTextureInfo source,
        in WGPUTexelCopyBufferInfo destination,
        in WGPUExtent3D size)
    {
        fixed (WGPUTexelCopyTextureInfo* sourcePointer = &source)
        fixed (WGPUTexelCopyBufferInfo* destinationPointer = &destination)
        fixed (WGPUExtent3D* sizePointer = &size) {
            WgpuUnsafe.wgpuCommandEncoderCopyTextureToBuffer(
                GetPointer(encoder), sourcePointer, destinationPointer, sizePointer);
        }
    }

    public static void CopyTextureToTexture(
        WgpuHandle<WGPUCommandEncoder> encoder,
        in WGPUTexelCopyTextureInfo source,
        in WGPUTexelCopyTextureInfo destination,
        in WGPUExtent3D size)
    {
        fixed (WGPUTexelCopyTextureInfo* sourcePointer = &source)
        fixed (WGPUTexelCopyTextureInfo* destinationPointer = &destination)
        fixed (WGPUExtent3D* sizePointer = &size) {
            WgpuUnsafe.wgpuCommandEncoderCopyTextureToTexture(
                GetPointer(encoder), sourcePointer, destinationPointer, sizePointer);
        }
    }

    public static void CopyTextureToBuffer(
        WgpuHandle<WGPUCommandEncoder> encoder,
        in WGPUTexelCopyTextureInfo source,
        in WGPUTexelCopyBufferInfo destination,
        in WGPUExtent3D size,
        in WGPUTexelCopyTextureInfo intermediate)
    {
        CopyTextureToTexture(encoder, in source, in intermediate, in size);
        CopyTextureToBuffer(encoder, in intermediate, in destination, in size);
    }
}
