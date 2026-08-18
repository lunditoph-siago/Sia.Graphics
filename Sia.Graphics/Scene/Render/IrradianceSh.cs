using Sia.Math;

namespace Sia.Graphics.Scene;

public static class IrradianceSh
{
    public const int CoefficientCount = 9;

    private static readonly double _goldenAngle = System.Math.PI * (3.0 - System.Math.Sqrt(5.0));

    public static float4[] Project(Func<float3, float3> radiance, int sampleCount = 4096)
    {
        var coefficients = new float3[CoefficientCount];
        var weightSum = 0.0;

        for (var i = 0; i < sampleCount; i++) {
            var direction = FibonacciSphereDirection(i, sampleCount);
            var sample = radiance(direction);
            var basis = EvaluateBasis(direction);
            for (var lm = 0; lm < CoefficientCount; lm++) {
                coefficients[lm] += sample * basis[lm];
            }
            weightSum += 1.0;
        }

        var solidAngle = (float)(4.0 * System.Math.PI / weightSum);
        var result = new float4[CoefficientCount];
        for (var lm = 0; lm < CoefficientCount; lm++) {
            var c = coefficients[lm] * solidAngle;
            result[lm] = new float4(c, 0.0f);
        }
        return result;
    }

    public static float3 Evaluate(float4[] coefficients, float3 normal)
    {
        const float a0 = 3.14159265359f;
        const float a1 = 2.09439510239f;
        const float a2 = 0.78539816339f;
        const float y00 = 0.282095f;
        const float y1 = 0.488603f;
        const float y2mn = 1.092548f;
        const float y20 = 0.315392f;
        const float y22 = 0.546274f;

        var result = coefficients[0].xyz * (a0 * y00);
        result += coefficients[1].xyz * (a1 * y1 * normal.y);
        result += coefficients[2].xyz * (a1 * y1 * normal.z);
        result += coefficients[3].xyz * (a1 * y1 * normal.x);
        result += coefficients[4].xyz * (a2 * y2mn * normal.x * normal.y);
        result += coefficients[5].xyz * (a2 * y2mn * normal.y * normal.z);
        result += coefficients[6].xyz * (a2 * y20 * (3.0f * normal.z * normal.z - 1.0f));
        result += coefficients[7].xyz * (a2 * y2mn * normal.x * normal.z);
        result += coefficients[8].xyz * (a2 * y22 * (normal.x * normal.x - normal.y * normal.y));
        return result;
    }

    private static float3 FibonacciSphereDirection(int index, int count)
    {
        var t = (index + 0.5) / count;
        var y = 1.0 - 2.0 * t;
        var radius = System.Math.Sqrt(System.Math.Max(0.0, 1.0 - y * y));
        var theta = _goldenAngle * index;
        var x = System.Math.Cos(theta) * radius;
        var z = System.Math.Sin(theta) * radius;
        return new float3((float)x, (float)y, (float)z);
    }

    private static float[] EvaluateBasis(float3 d)
    {
        const float y00 = 0.282095f;
        const float y1 = 0.488603f;
        const float y2mn = 1.092548f;
        const float y20 = 0.315392f;
        const float y22 = 0.546274f;

        return [
            y00,
            y1 * d.y,
            y1 * d.z,
            y1 * d.x,
            y2mn * d.x * d.y,
            y2mn * d.y * d.z,
            y20 * (3.0f * d.z * d.z - 1.0f),
            y2mn * d.x * d.z,
            y22 * (d.x * d.x - d.y * d.y),
        ];
    }
}
