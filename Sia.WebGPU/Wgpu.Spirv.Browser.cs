#if BROWSER
using System.Runtime.InteropServices.JavaScript;

namespace Sia.WebGPU;

public static unsafe partial class Wgpu
{
    [JSImport("translateSpirvToWgsl", "sia-spirv-polyfill.js")]
    private static partial string TranslateSpirvToWgsl(
        [JSMarshalAs<JSType.Array<JSType.Number>>] byte[] spirv);
}
#endif
