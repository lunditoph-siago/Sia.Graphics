using Sia.Spirv;

namespace Sia.Spirv.Compiler.Tests.Fixtures;

internal static class MemorySemanticsShaders
{
    [SpirvKernel(1)]
    public static void SwapLoop(StorageBuffer<uint> data)
    {
        uint a = 1, b = 2, count = (data[0] & 3) + 1;
        for (uint i = 0; i < count; i++) { uint old = a; a = b; b = old; }
        data[0] = a; data[1] = b;
    }

    [SpirvKernel(1)]
    public static void SwitchAndContinue(StorageBuffer<uint> data)
    {
        uint total = 0;
        for (uint i = 0; i < (data[0] & 7); i++) {
            switch (i % 3) {
                case 0: total += 3; break;
                case 1: continue;
                default: total++; break;
            }
        }
        data[1] = total;
    }

    [SpirvKernel(1)]
    public static void CaptureBeforeWrite(StorageBuffer<uint> data)
    {
        uint old = data[0];
        uint changed = WriteAndReturn(data);
        data[1] = old + changed;
    }

    private static uint WriteAndReturn(StorageBuffer<uint> data)
    {
        data[0] = 10;
        return data[0] + 1;
    }

    [SpirvKernel(1)]
    public static void RepeatedHelperCalls(StorageBuffer<uint> data)
    {
        uint sum = 0;
        for (uint i = 0; i < 3; i++) sum += Twice(data);
        data[1] = sum;
    }

    private static uint Twice(StorageBuffer<uint> data)
    {
        uint first = WriteAndReturn(data);
        uint second = WriteAndReturn(data);
        return first + second;
    }
}
