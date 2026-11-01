using System.Runtime.InteropServices;
using System.Text;

namespace Sia.WebGPU;

internal sealed unsafe class WgpuOwnedString : IDisposable
{
    private GCHandle _handle;

    private WgpuOwnedString(byte[]? bytes, bool isNull, bool nullTerminated = false)
    {
        if (bytes is null) {
            View = new WGPUStringView {
                Data = null,
                Length = isNull ? WgpuConstants.StrLen : 0,
            };
            return;
        }

        _handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        View = new WGPUStringView {
            Data = (byte*)_handle.AddrOfPinnedObject(),
            Length = (nuint)(bytes.Length - (nullTerminated ? 1 : 0)),
        };
    }

    public WGPUStringView View { get; }

    public static WgpuOwnedString Create(string? value) =>
        value is null
            ? new WgpuOwnedString(null, isNull: true)
            : value.Length == 0
                ? new WgpuOwnedString(null, isNull: false)
                : new WgpuOwnedString(Encoding.UTF8.GetBytes(value), isNull: false);

    // Emscripten's canvas-selector bridge reads a C string, even when the
    // bound source also carries an explicit byte count. Keep both contracts.
    public static WgpuOwnedString CreateNullTerminated(string value) =>
        new(Encoding.UTF8.GetBytes(value + '\0'), isNull: false, nullTerminated: true);

    public void Dispose()
    {
        if (_handle.IsAllocated) {
            _handle.Free();
            _handle = default;
        }
    }
}
