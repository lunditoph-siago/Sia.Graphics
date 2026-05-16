namespace Sia.WebGPU;

public static class WgpuConstants
{
    public const uint ArrayLayerCountUndefined = uint.MaxValue;
    public const uint CopyStrideUndefined = uint.MaxValue;
    public const ulong WholeMapSize = ulong.MaxValue;
    public const ulong WholeSize = ulong.MaxValue;
    public const uint MipLevelCountUndefined = uint.MaxValue;
    public const uint QuerySetIndexUndefined = uint.MaxValue;
    public const ulong LimitU64Undefined = ulong.MaxValue;
    public const uint LimitU32Undefined = uint.MaxValue;

    /// <summary>WGPU_STRLEN: marks a string view as null-terminated.</summary>
    public static readonly nuint StrLen = nuint.MaxValue;
}
