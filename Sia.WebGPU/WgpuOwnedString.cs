using System.Runtime.InteropServices;
using System.Text;

namespace Sia.WebGPU;

internal sealed unsafe class WgpuOwnedString : IDisposable
{
    private GCHandle _handle;

    private WgpuOwnedString(byte[]? bytes, nuint length)
    {
        if (bytes is null) {
            View = new WGPUStringView {
                Data = null,
                Length = length,
            };
            return;
        }

        _handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        View = new WGPUStringView {
            Data = (byte*)_handle.AddrOfPinnedObject(),
            Length = length,
        };
    }

    public WGPUStringView View { get; }

    public static WgpuOwnedString Create(string? value)
    {
        if (string.IsNullOrEmpty(value)) {
            return new WgpuOwnedString(null, value is null ? WgpuConstants.StrLen : 0);
        }

        // Emscripten's canvas-selector bridge reads a C string. Terminate
        // storage while preserving the explicit byte length for other readers.
        var length = Encoding.UTF8.GetByteCount(value);
        var bytes = new byte[checked(length + 1)];
        Encoding.UTF8.GetBytes(value.AsSpan(), bytes.AsSpan(0, length));
        return new WgpuOwnedString(bytes, (nuint)length);
    }

    public void Dispose()
    {
        if (_handle.IsAllocated) {
            _handle.Free();
            _handle = default;
        }
    }
}
