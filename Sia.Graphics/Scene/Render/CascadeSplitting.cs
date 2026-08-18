using Sia.Math;

namespace Sia.Graphics.Scene;

public static class CascadeSplitting
{
    public static float[] ComputeSplitDistances(float near, float far, int cascadeCount, float lambda)
    {
        var splits = new float[cascadeCount + 1];
        splits[0] = near;
        for (var i = 1; i < cascadeCount; i++) {
            var t = (float)i / cascadeCount;
            var logSplit = near * MathF.Pow(far / near, t);
            var uniformSplit = near + (far - near) * t;
            splits[i] = lambda * logSplit + (1.0f - lambda) * uniformSplit;
        }
        splits[cascadeCount] = far;
        return splits;
    }

    public static float4x4 ComputeCascadeViewProj(
        in float4x4 cameraWorldMatrix,
        float verticalFov,
        float aspect,
        float splitNear,
        float splitFar,
        float3 lightDirection,
        float pullbackMultiplier)
    {
        var corners = ComputeFrustumCorners(in cameraWorldMatrix, verticalFov, aspect, splitNear, splitFar);

        var center = float3.zero;
        for (var i = 0; i < corners.Length; i++) {
            center += corners[i];
        }
        center /= corners.Length;

        var radius = 0.01f;
        for (var i = 0; i < corners.Length; i++) {
            radius = MathF.Max(radius, math.length(corners[i] - center));
        }

        var up = MathF.Abs(lightDirection.y) > 0.99f ? new float3(0, 0, 1) : new float3(0, 1, 0);
        var lightRotation = quaternion.LookRotation(-lightDirection, up);
        var pullback = radius * pullbackMultiplier;
        var eye = center - lightDirection * pullback;
        var lightWorld = float4x4.TRS(eye, lightRotation, float3.one);
        var view = math.inverse(lightWorld);
        var proj = float4x4.Ortho(radius * 2.0f, radius * 2.0f, 0.01f, pullback + radius);
        return math.mul(proj, view);
    }

    public static float4x4 ComputeSpotViewProj(in float4x4 lightWorldMatrix, float outerAngle, float range)
    {
        var view = math.inverse(lightWorldMatrix);
        var fov = System.Math.Clamp(outerAngle * 2.0f, 0.05f, MathF.PI - 0.05f);
        var proj = float4x4.PerspectiveFov(fov, 1.0f, 0.05f, range);
        return math.mul(proj, view);
    }

    private static float3[] ComputeFrustumCorners(
        in float4x4 cameraWorldMatrix, float verticalFov, float aspect, float near, float far)
    {
        var position = cameraWorldMatrix.c3.xyz;
        var forward = -math.normalize(cameraWorldMatrix.c2.xyz);
        var up = math.normalize(cameraWorldMatrix.c1.xyz);
        var right = math.normalize(cameraWorldMatrix.c0.xyz);

        var halfVNear = near * MathF.Tan(verticalFov * 0.5f);
        var halfHNear = halfVNear * aspect;
        var halfVFar = far * MathF.Tan(verticalFov * 0.5f);
        var halfHFar = halfVFar * aspect;

        var centerNear = position + forward * near;
        var centerFar = position + forward * far;

        return [
            centerNear + up * halfVNear + right * halfHNear,
            centerNear + up * halfVNear - right * halfHNear,
            centerNear - up * halfVNear + right * halfHNear,
            centerNear - up * halfVNear - right * halfHNear,
            centerFar + up * halfVFar + right * halfHFar,
            centerFar + up * halfVFar - right * halfHFar,
            centerFar - up * halfVFar + right * halfHFar,
            centerFar - up * halfVFar - right * halfHFar,
        ];
    }
}
