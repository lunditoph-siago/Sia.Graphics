using Sia.Spirv;

namespace Sia.Spirv.Compiler.Tests.Fixtures;

internal static class RefLocalShaders
{
    public static void UnusedAddress(StorageBuffer<uint> data)
    {
        var index = Gpu.GlobalInvocationId.X;
        ref var unused = ref data[index * 3u + 1u];
        data[0] = index;
    }

    public static void CapturedAlias(StorageBuffer<uint> data)
    {
        ref var first = ref data[0];
        ref var captured = ref first;
        first = ref data[1];
        captured = 8u;
        first = 9u;
    }

    public static void Loop(StorageBuffer<uint> data)
    {
        ref var target = ref data[0];
        for (uint i = 0; i < 3; i++) {
            if (i == 1u) target = ref data[1];
            target += i + 1u;
        }
    }

    public static void FunctionLocal(StorageBuffer<uint> data)
    {
        uint local = 7u;
        ref var target = ref local;
        target += 3u;
        data[0] = local;
        data[1] = target;
    }

    public static void AtomicStorage(StorageBuffer<uint> data)
    {
        ref var target = ref data[0];
        data.AtomicAdd(1u, 2u);
        target += 3u;
        data[1] = target;
    }

    public static void MixedAddressSpaces(StorageBuffer<uint> data)
    {
        uint local = 0u;
        scoped ref uint target = ref data[0];
        if (data[0] == 7u) target = ref local;
        target = 11u;
    }
}
