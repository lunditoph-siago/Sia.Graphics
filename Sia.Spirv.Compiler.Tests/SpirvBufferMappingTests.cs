using System.Buffers.Binary;
using Sia.Math;
using Sia.Spirv.Compiler.Model;
using Sia.Spirv.Runtime;

namespace Sia.Spirv.Compiler.Tests;

public sealed class SpirvBufferMappingTests
{
    [Fact]
    public void MappingAutomaticallyReordersAndAlignsCpuStructFields()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(ComputeShaders),
            nameof(ComputeShaders.CopyPackedStructs));
        var layout = Assert.IsType<SpirvStructLayout>(kernel.Parameters[0].StructLayout);
        var resource = new SpirvResourceBinding(
            "source",
            "storage-buffer",
            "read-only",
            layout.Name,
            0,
            0,
            layout.Alignment,
            layout.Size,
            layout.ArrayStride,
            layout.Fields.Select(static field => new SpirvStructFieldLayout(
                field.Name,
                field.Type.ToString(),
                field.Offset,
                field.Alignment,
                field.Size)).ToArray());
        var mapping = SpirvBufferMapping<ComputeShaders.PackedParticle>.Create(resource);
        ComputeShaders.PackedParticle[] source = [
            new(new float3(1.0f, 2.0f, 3.0f), 7u),
            new(new float3(4.0f, 5.0f, 6.0f), 11u)
        ];

        var packed = mapping.Pack(source);

        Assert.Equal(16, mapping.GpuStride);
        Assert.Equal(32, packed.Length);
        Assert.Equal(1.0f, ReadSingle(packed, 0));
        Assert.Equal(2.0f, ReadSingle(packed, 4));
        Assert.Equal(3.0f, ReadSingle(packed, 8));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(packed.AsSpan(12)));
        Assert.Equal(4.0f, ReadSingle(packed, 16));
        Assert.Equal(11u, BinaryPrimitives.ReadUInt32LittleEndian(packed.AsSpan(28)));

        var roundTrip = new ComputeShaders.PackedParticle[source.Length];
        mapping.Unpack(packed, roundTrip);
        Assert.Equal(source[0].Position.x, roundTrip[0].Position.x);
        Assert.Equal(source[0].Position.y, roundTrip[0].Position.y);
        Assert.Equal(source[0].Position.z, roundTrip[0].Position.z);
        Assert.Equal(source[0].Id, roundTrip[0].Id);
        Assert.Equal(source[1].Position.x, roundTrip[1].Position.x);
        Assert.Equal(source[1].Position.y, roundTrip[1].Position.y);
        Assert.Equal(source[1].Position.z, roundTrip[1].Position.z);
        Assert.Equal(source[1].Id, roundTrip[1].Id);
    }

    private static float ReadSingle(byte[] bytes, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset)));
}
