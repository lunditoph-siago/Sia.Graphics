using Sia.Math;
using Sia.Spirv;

namespace Sia.Spirv.Compiler.Tests;

internal static class MathShaders
{
    [SpirvFragmentShader]
    public static void IntegerAndBooleanVectors(float value)
    {
        var signed = new int2((int)value, 2) + new int2(1);
        var unsigned = new uint3(1u, 2u, 3u) * new uint3(2u);
        var flags = new bool4(
            signed.x > 0,
            unsigned.y == 4u,
            signed.y < 8,
            unsigned.z > 0u);
        var reduced = math.any(flags) && math.all(flags);
        var selected = math.select((float)signed.y, (float)signed.x, reduced);

        Gpu.SetOutput(
            0,
            (float)signed.x,
            (float)unsigned.y,
            selected,
            math.select(0.0f, 1.0f, reduced));
    }

    [SpirvFragmentShader]
    public static void VectorBitcasts(float value)
    {
        var signedBits = new int2((int)value, 1065353216);
        var unsignedBits = new uint3(1065353216u, 1073741824u, 1077936128u);
        var fromSigned = math.asfloat(signedBits);
        var fromUnsigned = math.asfloat(unsignedBits);

        Gpu.SetOutput(0, fromSigned.x, fromSigned.y, fromUnsigned.y, fromUnsigned.z);
    }

    [SpirvFragmentShader]
    public static void VectorHalfConversion(float value)
    {
        var fromHalf = math.f16tof32(new uint2((uint)value, 0x4000u));

        Gpu.SetOutput(0, fromHalf.x, fromHalf.y, 0.0f, 1.0f);
    }

    [SpirvFragmentShader]
    public static void VectorSelect(float value)
    {
        var selected = math.select(
            new float4(value, 2.0f, 3.0f, 4.0f),
            new float4(5.0f, 6.0f, 7.0f, 8.0f),
            new bool4(false, true, value > 0.0f, true));

        Gpu.SetOutput(0, selected.x, selected.y, selected.z, selected.w);
    }

    [SpirvFragmentShader]
    public static void Vectors(float value)
    {
        var a = new float2(value, 2.0f);
        var b = math.saturate(math.sin(a) + math.cos(a));
        var c = new float4(value, 2.0f, 3.0f, 4.0f);
        var d = math.normalize(math.reflect(c, new float4(0.0f, 1.0f, 0.0f, 0.0f)));
        var powered = math.pow(c, new float4(2.0f));

        Gpu.SetOutput(0, b.x, b.y, math.dot(d, c), powered.w);
    }

    [SpirvFragmentShader]
    public static void SquareMatrices(float value)
    {
        var m2 = new float2x2(value, 2.0f, 3.0f, 4.0f);
        var m3 = new float3x3(
            value, 2.0f, 3.0f,
            4.0f, 5.0f, 6.0f,
            7.0f, 8.0f, 9.0f);
        var m4 = new float4x4(
            value, 2.0f, 3.0f, 4.0f,
            5.0f, 6.0f, 7.0f, 8.0f,
            9.0f, 10.0f, 11.0f, 12.0f,
            13.0f, 14.0f, 15.0f, 16.0f);
        var r2 = math.mul(math.transpose(m2), new float2(1.0f, 2.0f));
        var r3 = math.mul(math.transpose(m3), new float3(1.0f, 2.0f, 3.0f));
        var r4 = math.mul(math.transpose(m4), new float4(1.0f, 2.0f, 3.0f, 4.0f));

        Gpu.SetOutput(0, r2.x, r3.y, r4.z, r4.w);
    }

    [SpirvFragmentShader]
    public static void RectangularMatrices(float value)
    {
        var m2x3 = new float2x3(value, 2.0f, 3.0f, 4.0f, 5.0f, 6.0f);
        var m2x4 = new float2x4(value, 2.0f, 3.0f, 4.0f, 5.0f, 6.0f, 7.0f, 8.0f);
        var m3x2 = math.transpose(m2x3);
        var m3x4 = new float3x4(
            value, 2.0f, 3.0f, 4.0f,
            5.0f, 6.0f, 7.0f, 8.0f,
            9.0f, 10.0f, 11.0f, 12.0f);
        var m4x2 = math.transpose(m2x4);
        var m4x3 = math.transpose(m3x4);
        var r2 = math.mul(m2x3, new float3(1.0f, 2.0f, 3.0f));
        var r3 = math.mul(m3x2, new float2(1.0f, 2.0f));
        var r4a = math.mul(m4x2, new float2(1.0f, 2.0f));
        var r4b = math.mul(m4x3, new float3(1.0f, 2.0f, 3.0f));

        Gpu.SetOutput(0, r2.x, r3.y, r4a.z, r4b.w);
    }
}
