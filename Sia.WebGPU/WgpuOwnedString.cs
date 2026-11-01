using System.Runtime.InteropServices;
using System.Text;

namespace Sia.WebGPU;

internal sealed unsafe class WgpuOwnedString : IDisposable
{
    private GCHandle _handle;

    public WgpuOwnedString(string? value)
    {
        if (string.IsNullOrEmpty(value)) {
            View = new WGPUStringView {
                Data = null,
                Length = value is null ? WgpuConstants.StrLen : 0,
            };
            return;
        }

        // Emscripten's canvas-selector bridge reads a C string. Terminate
        // storage while preserving the explicit byte length for other readers.
        var length = Encoding.UTF8.GetByteCount(value);
        var bytes = new byte[checked(length + 1)];
        Encoding.UTF8.GetBytes(value.AsSpan(), bytes.AsSpan(0, length));

        _handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        View = new WGPUStringView {
            Data = (byte*)_handle.AddrOfPinnedObject(),
            Length = (nuint)length,
        };
    }

    public WGPUStringView View { get; }

    public void Dispose()
    {
        if (_handle.IsAllocated) {
            _handle.Free();
            _handle = default;
        }
    }
}
