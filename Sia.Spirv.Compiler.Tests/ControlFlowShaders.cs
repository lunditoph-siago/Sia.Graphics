using Sia.Spirv;

namespace Sia.Spirv.Compiler.Tests;

internal static class ControlFlowShaders
{
    [SpirvKernel(64)]
    public static void IntegerControlFlow(
        StorageBuffer<int> signedOutput,
        StorageBuffer<uint> unsignedOutput,
        int value,
        uint unsignedValue,
        int count,
        uint iterations,
        int selector)
    {
        var sum = 0u;
        for (var index = 0u; index < iterations; index++) {
            sum += index;
        }

        var selected = selector;
        switch (selector) {
            case 0:
                selected = value;
                break;
            case 1:
                selected = ~value;
                break;
            case 2:
                selected = int.MinValue;
                break;
        }

        signedOutput[0] = selected ^ (value >> count) ^ int.MinValue;
        unsignedOutput[0] = (unsignedValue << count) ^
            (unsignedValue >> count) ^ sum;
        unsignedOutput[1] = unsignedValue << 40;
    }
}
