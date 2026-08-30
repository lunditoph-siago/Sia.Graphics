using Sia.WebGPU;

namespace Sia.Graphics.UI;

internal static class UiGpuBuffer
{
    public static bool EnsureCapacity(
        World world,
        Entity device,
        ref Entity buffer,
        ref ulong capacity,
        ulong requiredBytes,
        ulong stride,
        WGPUBufferUsage usage)
    {
        requiredBytes = System.Math.Max(requiredBytes, stride);
        if (capacity >= requiredBytes)
            return false;

        var newCapacity = System.Math.Max(capacity, stride * 256);
        while (newCapacity < requiredBytes)
            newCapacity *= 2;

        if (buffer.IsValid)
            buffer.Destroy();

        buffer = world.CreateWgpuBuffer(device, new WGPUBufferDescriptor {
            NextInChain = null,
            Label = default,
            Usage = usage | WGPUBufferUsage.CopyDst,
            Size = newCapacity,
            MappedAtCreation = 0
        });
        capacity = newCapacity;
        return true;
    }
}
