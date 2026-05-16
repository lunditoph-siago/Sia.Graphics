using System.Runtime.InteropServices;
using System.Text;

namespace Sia.WebGPU;

internal sealed unsafe class WgpuOwnedString : IDisposable
{
    private GCHandle _handle;

    private WgpuOwnedString(byte[]? bytes, bool isNull)
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
            Length = (nuint)bytes.Length,
        };
    }

    public WGPUStringView View { get; }

    public static WgpuOwnedString Create(string? value) =>
        value is null
            ? new WgpuOwnedString(null, isNull: true)
            : value.Length == 0
                ? new WgpuOwnedString(null, isNull: false)
                : new WgpuOwnedString(Encoding.UTF8.GetBytes(value), isNull: false);

    public void Dispose()
    {
        if (_handle.IsAllocated) {
            _handle.Free();
            _handle = default;
        }
    }
}
