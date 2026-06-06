using System.Text;

namespace Sia.WebGPU;

internal static unsafe class WgpuStringViewText
{
    public static string ToString(WGPUStringView view)
    {
        if (view.Data == null || view.Length == 0) {
            return string.Empty;
        }

        if (view.Length == WgpuConstants.StrLen) {
            return Encoding.UTF8.GetString(view.Data, GetNullTerminatedLength(view.Data));
        }

        return Encoding.UTF8.GetString(view.Data, checked((int)view.Length));
    }

    private static int GetNullTerminatedLength(byte* data)
    {
        var length = 0;
        while (data[length] != 0) {
            checked { length++; }
        }

        return length;
    }
}
