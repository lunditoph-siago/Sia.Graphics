using System.Runtime.InteropServices;
using Sia.Math;
using Sia.Spirv;

namespace Sia.Spirv.Compiler.Tests;

internal static class ComputeShaders
{
    internal struct Particle
    {
        public float4 Position;
        public uint Id;

        public Particle(float4 position, uint id)
        {
            Position = position;
            Id = id;
        }
    }

    internal struct PackedParticle
    {
        public float3 Position;
        public uint Id;

        public PackedParticle(float3 position, uint id)
        {
            Position = position;
            Id = id;
        }
    }

    internal struct AlignedParticle
    {
        public uint Id;
        public float3 Position;

        public AlignedParticle(uint id, float3 position)
        {
            Id = id;
            Position = position;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct LogicalParticle
    {
        [FieldOffset(64)]
        public float3 Position;

        [FieldOffset(0)]
        public uint Id;

        public LogicalParticle(float3 position, uint id)
        {
            Position = position;
            Id = id;
        }
    }

    [SpirvKernel(8, 4, 2)]
    public static void Synchronize(
        StorageBuffer<float> values,
        uint count)
    {
        Gpu.Barrier();

        var index = Gpu.GlobalInvocationId.X;
        if (index < count) {
            values[index] += 1.0f;
        }
    }

    [SpirvKernel(64)]
    public static void CopyVectors(
        ReadOnlyStorageBuffer<float4> source,
        StorageBuffer<float4> destination)
    {
        var index = Gpu.GlobalInvocationId.X;
        destination[index] = source[index] + new float4(1.0f);
    }

    [SpirvKernel(64)]
    public static void UseHelpers(StorageBuffer<float> values)
    {
        var index = Gpu.GlobalInvocationId.X;
        values[index] = AddBias(Square(values[index]));
    }

    private static float Square(float value) => value * value;

    private static float AddBias(float value) => value + 1.0f;

    [SpirvKernel(32)]
    public static void AtomicWorkgroup(
        StorageBuffer<uint> counters,
        WorkgroupMemory<uint> shared)
    {
        var localIndex = Gpu.LocalInvocationId.X;
        shared[localIndex] = 1u;
        Gpu.Barrier();

        var previous = shared.AtomicAdd(0u, 1u);
        counters.AtomicAdd(0u, previous);
        counters.AtomicExchange(1u, localIndex);
    }

    [SpirvKernel(64)]
    public static void CopyStructs(
        ReadOnlyStorageBuffer<Particle> source,
        StorageBuffer<Particle> destination)
    {
        var index = Gpu.GlobalInvocationId.X;
        destination[index] = source[index];
    }

    [SpirvKernel(64)]
    public static void CopyPackedStructs(
        ReadOnlyStorageBuffer<PackedParticle> source,
        StorageBuffer<PackedParticle> destination)
    {
        var index = Gpu.GlobalInvocationId.X;
        destination[index] = source[index];
    }

    [SpirvKernel(64)]
    public static void CopyAlignedStructs(
        ReadOnlyStorageBuffer<AlignedParticle> source,
        StorageBuffer<AlignedParticle> destination)
    {
        var index = Gpu.GlobalInvocationId.X;
        destination[index] = source[index];
    }

    [SpirvKernel(64)]
    public static void CopyLogicalStructs(
        ReadOnlyStorageBuffer<LogicalParticle> source,
        StorageBuffer<LogicalParticle> destination)
    {
        var index = Gpu.GlobalInvocationId.X;
        destination[index] = source[index];
    }

    [SpirvKernel(4)]
    public static void CopyBoundedStructs(
        [SpirvBufferLength(4)] ReadOnlyStorageBuffer<PackedParticle> source,
        StorageBuffer<PackedParticle> destination)
    {
        var index = Gpu.GlobalInvocationId.X;
        destination[index] = source[index];
    }
}
