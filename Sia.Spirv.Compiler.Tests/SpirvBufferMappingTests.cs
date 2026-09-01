using System.Buffers.Binary;
using Sia.Math;
using Sia.Spirv.Compiler.Legalization;
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
        var layout = Assert.IsType<PhysicalStructLayout>(kernel.Parameters[0].PhysicalLayout);
        var resource = CreateResource(layout);
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

    [Fact]
    public void MappingUsesReflectionForInternalAndTrailingPadding()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(ComputeShaders),
            nameof(ComputeShaders.CopyAlignedStructs));
        var layout = Assert.IsType<PhysicalStructLayout>(kernel.Parameters[0].PhysicalLayout);
        var resource = CreateResource(layout);
        var mapping = SpirvBufferMapping<ComputeShaders.AlignedParticle>.Create(resource);
        ComputeShaders.AlignedParticle[] source = [
            new(7u, new float3(1.0f, 2.0f, 3.0f))
        ];

        var packed = mapping.Pack(source);

        Assert.Equal(32, mapping.GpuStride);
        Assert.Equal(32, packed.Length);
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(packed));
        Assert.Equal(1.0f, ReadSingle(packed, 16));
        Assert.Equal(2.0f, ReadSingle(packed, 20));
        Assert.Equal(3.0f, ReadSingle(packed, 24));
        Assert.All(packed[4..16], static value => Assert.Equal(0, value));
        Assert.All(packed[28..32], static value => Assert.Equal(0, value));
    }

    [Fact]
    public void MappingBridgesExplicitCpuLayoutToLegalizedGpuLayout()
    {
        var kernel = SpirvTestAssembly.GetKernel(
            typeof(ComputeShaders),
            nameof(ComputeShaders.CopyLogicalStructs));
        var layout = Assert.IsType<PhysicalStructLayout>(kernel.Parameters[0].PhysicalLayout);
        var mapping = SpirvBufferMapping<ComputeShaders.LogicalParticle>.Create(
            CreateResource(layout));
        ComputeShaders.LogicalParticle[] source = [
            new(new float3(1.0f, 2.0f, 3.0f), 7u)
        ];

        var packed = mapping.Pack(source);

        Assert.Equal(80, mapping.CpuStride);
        Assert.Equal(16, mapping.GpuStride);
        Assert.Equal(1.0f, ReadSingle(packed, 0));
        Assert.Equal(2.0f, ReadSingle(packed, 4));
        Assert.Equal(3.0f, ReadSingle(packed, 8));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(packed.AsSpan(12)));

        var roundTrip = new ComputeShaders.LogicalParticle[1];
        mapping.Unpack(packed, roundTrip);
        Assert.Equal(source[0].Position.x, roundTrip[0].Position.x);
        Assert.Equal(source[0].Position.y, roundTrip[0].Position.y);
        Assert.Equal(source[0].Position.z, roundTrip[0].Position.z);
        Assert.Equal(source[0].Id, roundTrip[0].Id);
    }

    private static float ReadSingle(byte[] bytes, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset)));

    private static SpirvResourceBinding CreateResource(PhysicalStructLayout layout) =>
        new(
            "source",
            "storage-buffer",
            "read-only",
            layout.LogicalType.Name,
            0,
            0,
            layout.Alignment,
            layout.Size,
            layout.ArrayStride,
            layout.LogicalType.Fields.Select((field, logicalFieldIndex) => {
                var member = layout.GetLogicalMember(logicalFieldIndex);
                return new SpirvStructFieldLayout(
                    field.Name,
                    field.Type.ToString(),
                    member.Offset,
                    member.Alignment,
                    member.Size);
            }).ToArray());
}
