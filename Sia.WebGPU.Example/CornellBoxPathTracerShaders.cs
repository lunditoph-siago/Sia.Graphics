using Sia.Math;
using Sia.Spirv;

namespace Sia.WebGPU.Example;

// A faithful, hand-inlined port of the original WGSL path tracer (see
// CornellBoxShaders.Source in git history at 7f1ba28) — CornellBoxShaders
// itself still carries the placeholder screen-space "painting" that
// replaced it; this class is additive on purpose so it does not disturb
// that wiring. Swapping it in (and retiring the placeholder) belongs to
// the separate example-migration pass. The Sia.Spirv compiler does not
// support calling user-defined helper methods — only recognized
// Gpu.*/Sia.Math.float3 intrinsics — so every WGSL helper function
// (random, rotate_y, commit_hit, intersect_room/box/sphere/scene,
// cosine_hemisphere, sample_direct_light, make_camera_ray, trace_path) is
// inlined directly into the two entry points below, exactly preserving
// the original algorithm and constants. `intersect_scene` in particular
// appears twice in full (primary rays in the bounce loop, shadow rays in
// direct light sampling) because there is no way to factor it out.
internal static class CornellBoxPathTracerShaders
{
    private const float Pi = 3.14159265358979f;
    private const float Epsilon = 0.001f;
    private const uint Diffuse = 0u;
    private const uint Mirror = 1u;

    [SpirvFragmentShader]
    public static void PathFragment(
        Texture2D previousAccumulation,
        float cameraX,
        float cameraY,
        float cameraZ,
        float exposure,
        float targetX,
        float targetY,
        float targetZ,
        float fieldOfView,
        float resolutionX,
        float resolutionY,
        float frame,
        float sampleCount,
        float bounceLimit,
        float aperture,
        float focusDistance)
    {
        var pixelX = (uint)Gpu.GetFragmentPosition(0);
        var pixelY = (uint)Gpu.GetFragmentPosition(1);
        var frameIndex = (uint)frame;
        var sampleTotal = (uint)sampleCount;
        var bounceTotal = (uint)bounceLimit;
        var cameraPosition = new float3(cameraX, cameraY, cameraZ);
        var cameraTarget = new float3(targetX, targetY, targetZ);
        var resolution = new float3(resolutionX, resolutionY, 0.0f);

        var sampleSumX = 0.0f;
        var sampleSumY = 0.0f;
        var sampleSumZ = 0.0f;

        for (uint sample = 0u; sample < 8u; sample++) {
            if (sample >= sampleTotal) {
                break;
            }

            var state = (pixelX * 1973u + pixelY * 9277u + frameIndex * 26699u + sample * 17431u) | 1u;

            // ---- make_camera_ray ----
            state = (state ^ 61u) ^ (state >> 16);
            state = state * 9u;
            state = state ^ (state >> 4);
            state = state * 0x27d4eb2du;
            state = state ^ (state >> 15);
            var jitterX = (float)state * (1.0f / 4294967296.0f);

            state = (state ^ 61u) ^ (state >> 16);
            state = state * 9u;
            state = state ^ (state >> 4);
            state = state * 0x27d4eb2du;
            state = state ^ (state >> 15);
            var jitterY = (float)state * (1.0f / 4294967296.0f);

            var screenX = (((float)pixelX + jitterX) / resolution.x) * 2.0f - 1.0f;
            var screenY = (((float)pixelY + jitterY) / resolution.y) * 2.0f - 1.0f;

            var forward = math.normalize(cameraTarget - cameraPosition);
            var right = math.normalize(math.cross(forward, new float3(0.0f, 1.0f, 0.0f)));
            var up = math.cross(right, forward);
            var aspect = resolution.x / resolution.y;
            var scale = Gpu.Sin(fieldOfView * 0.5f) / Gpu.Cos(fieldOfView * 0.5f);
            var rayDirection = math.normalize(
                forward + right * (screenX * aspect * scale) - up * (screenY * scale));

            state = (state ^ 61u) ^ (state >> 16);
            state = state * 9u;
            state = state ^ (state >> 4);
            state = state * 0x27d4eb2du;
            state = state ^ (state >> 15);
            var diskRadiusSeed = (float)state * (1.0f / 4294967296.0f);
            var diskRadius = Gpu.Sqrt(diskRadiusSeed) * aperture;

            state = (state ^ 61u) ^ (state >> 16);
            state = state * 9u;
            state = state ^ (state >> 4);
            state = state * 0x27d4eb2du;
            state = state ^ (state >> 15);
            var diskAngle = ((float)state * (1.0f / 4294967296.0f)) * 2.0f * Pi;

            var lensOffset = right * (Gpu.Cos(diskAngle) * diskRadius) + up * (Gpu.Sin(diskAngle) * diskRadius);
            var focusPoint = cameraPosition + rayDirection * focusDistance;
            var rayOrigin = cameraPosition + lensOffset;
            rayDirection = math.normalize(focusPoint - rayOrigin);

            // ---- trace_path ----
            var throughput = new float3(1.0f, 1.0f, 1.0f);
            var radianceX = 0.0f;
            var radianceY = 0.0f;
            var radianceZ = 0.0f;
            var previousWasSpecular = 1u;

            for (uint bounce = 0u; bounce < 12u; bounce++) {
                if (bounce >= bounceTotal) {
                    break;
                }

                // ---- intersect_scene(ray) ----
                var hitT = 1e30f;
                var hitPosition = new float3(0.0f, 0.0f, 0.0f);
                var hitNormal = new float3(0.0f, 0.0f, 0.0f);
                var hitAlbedo = new float3(0.0f, 0.0f, 0.0f);
                var hitEmission = new float3(0.0f, 0.0f, 0.0f);
                var hitMaterial = Diffuse;

                {
                    var white = new float3(0.76f, 0.73f, 0.68f);

                    var t = (-1.0f - rayOrigin.x) / rayDirection.x;
                    var point = rayOrigin + rayDirection * t;
                    if (point.y >= 0.0f && point.y <= 2.0f && point.z >= -1.0f && point.z <= 1.0f &&
                        t > Epsilon && t < hitT) {
                        hitT = t;
                        hitPosition = rayOrigin + rayDirection * t;
                        hitNormal = new float3(1.0f, 0.0f, 0.0f);
                        hitAlbedo = new float3(0.63f, 0.065f, 0.05f);
                        hitEmission = new float3(0.0f, 0.0f, 0.0f);
                        hitMaterial = Diffuse;
                    }

                    t = (1.0f - rayOrigin.x) / rayDirection.x;
                    point = rayOrigin + rayDirection * t;
                    if (point.y >= 0.0f && point.y <= 2.0f && point.z >= -1.0f && point.z <= 1.0f &&
                        t > Epsilon && t < hitT) {
                        hitT = t;
                        hitPosition = rayOrigin + rayDirection * t;
                        hitNormal = new float3(-1.0f, 0.0f, 0.0f);
                        hitAlbedo = new float3(0.12f, 0.45f, 0.15f);
                        hitEmission = new float3(0.0f, 0.0f, 0.0f);
                        hitMaterial = Diffuse;
                    }

                    t = (0.0f - rayOrigin.y) / rayDirection.y;
                    point = rayOrigin + rayDirection * t;
                    if (point.x >= -1.0f && point.x <= 1.0f && point.z >= -1.0f && point.z <= 1.0f &&
                        t > Epsilon && t < hitT) {
                        hitT = t;
                        hitPosition = rayOrigin + rayDirection * t;
                        hitNormal = new float3(0.0f, 1.0f, 0.0f);
                        hitAlbedo = white;
                        hitEmission = new float3(0.0f, 0.0f, 0.0f);
                        hitMaterial = Diffuse;
                    }

                    t = (2.0f - rayOrigin.y) / rayDirection.y;
                    point = rayOrigin + rayDirection * t;
                    if (point.x >= -1.0f && point.x <= 1.0f && point.z >= -1.0f && point.z <= 1.0f &&
                        t > Epsilon && t < hitT) {
                        var albedo = white;
                        var emission = new float3(0.0f, 0.0f, 0.0f);
                        if (Gpu.Abs(point.x) < 0.36f && point.z > -0.42f && point.z < 0.24f) {
                            albedo = new float3(0.0f, 0.0f, 0.0f);
                            emission = new float3(18.0f, 15.0f, 10.5f);
                        }
                        hitT = t;
                        hitPosition = rayOrigin + rayDirection * t;
                        hitNormal = new float3(0.0f, -1.0f, 0.0f);
                        hitAlbedo = albedo;
                        hitEmission = emission;
                        hitMaterial = Diffuse;
                    }

                    t = (-1.0f - rayOrigin.z) / rayDirection.z;
                    point = rayOrigin + rayDirection * t;
                    if (point.x >= -1.0f && point.x <= 1.0f && point.y >= 0.0f && point.y <= 2.0f &&
                        t > Epsilon && t < hitT) {
                        hitT = t;
                        hitPosition = rayOrigin + rayDirection * t;
                        hitNormal = new float3(0.0f, 0.0f, 1.0f);
                        hitAlbedo = white;
                        hitEmission = new float3(0.0f, 0.0f, 0.0f);
                        hitMaterial = Diffuse;
                    }
                }

                // intersect_box #1: center (-0.38, 0.34, 0.15), half-extent (0.38, 0.34, 0.38), angle -0.24
                {
                    var angle = -0.24f;
                    var cosine = Gpu.Cos(angle);
                    var sine = Gpu.Sin(angle);
                    var relativeOrigin = rayOrigin - new float3(-0.38f, 0.34f, 0.15f);
                    var localOrigin = new float3(
                        cosine * relativeOrigin.x + sine * relativeOrigin.z,
                        relativeOrigin.y,
                        -sine * relativeOrigin.x + cosine * relativeOrigin.z);
                    var localDirection = new float3(
                        cosine * rayDirection.x + sine * rayDirection.z,
                        rayDirection.y,
                        -sine * rayDirection.x + cosine * rayDirection.z);
                    var halfExtent = new float3(0.38f, 0.34f, 0.38f);
                    var inverseDirection = new float3(1.0f, 1.0f, 1.0f) / localDirection;
                    var first = (-halfExtent - localOrigin) * inverseDirection;
                    var second = (halfExtent - localOrigin) * inverseDirection;
                    var nearest = math.min(first, second);
                    var farthest = math.max(first, second);
                    var nearT = Gpu.Max(Gpu.Max(nearest.x, nearest.y), nearest.z);
                    var farT = Gpu.Min(Gpu.Min(farthest.x, farthest.y), farthest.z);

                    if (nearT > Epsilon && nearT < farT && nearT < hitT) {
                        var localNormalX = 0.0f;
                        var localNormalY = 0.0f;
                        var localNormalZ = 0.0f;
                        if (nearT == nearest.x) {
                            localNormalX = Gpu.Select(1.0f, -1.0f, Gpu.GreaterThan(localDirection.x, 0.0f));
                        }
                        else if (nearT == nearest.y) {
                            localNormalY = Gpu.Select(1.0f, -1.0f, Gpu.GreaterThan(localDirection.y, 0.0f));
                        }
                        else {
                            localNormalZ = Gpu.Select(1.0f, -1.0f, Gpu.GreaterThan(localDirection.z, 0.0f));
                        }
                        var localNormal = new float3(localNormalX, localNormalY, localNormalZ);
                        var normal = new float3(
                            cosine * localNormal.x - sine * localNormal.z,
                            localNormal.y,
                            sine * localNormal.x + cosine * localNormal.z);

                        hitT = nearT;
                        hitPosition = rayOrigin + rayDirection * nearT;
                        hitNormal = normal;
                        hitAlbedo = new float3(0.74f, 0.70f, 0.63f);
                        hitEmission = new float3(0.0f, 0.0f, 0.0f);
                        hitMaterial = Diffuse;
                    }
                }

                // intersect_box #2: center (0.39, 0.69, -0.29), half-extent (0.31, 0.69, 0.31), angle 0.30
                {
                    var angle = 0.30f;
                    var cosine = Gpu.Cos(angle);
                    var sine = Gpu.Sin(angle);
                    var relativeOrigin = rayOrigin - new float3(0.39f, 0.69f, -0.29f);
                    var localOrigin = new float3(
                        cosine * relativeOrigin.x + sine * relativeOrigin.z,
                        relativeOrigin.y,
                        -sine * relativeOrigin.x + cosine * relativeOrigin.z);
                    var localDirection = new float3(
                        cosine * rayDirection.x + sine * rayDirection.z,
                        rayDirection.y,
                        -sine * rayDirection.x + cosine * rayDirection.z);
                    var halfExtent = new float3(0.31f, 0.69f, 0.31f);
                    var inverseDirection = new float3(1.0f, 1.0f, 1.0f) / localDirection;
                    var first = (-halfExtent - localOrigin) * inverseDirection;
                    var second = (halfExtent - localOrigin) * inverseDirection;
                    var nearest = math.min(first, second);
                    var farthest = math.max(first, second);
                    var nearT = Gpu.Max(Gpu.Max(nearest.x, nearest.y), nearest.z);
                    var farT = Gpu.Min(Gpu.Min(farthest.x, farthest.y), farthest.z);

                    if (nearT > Epsilon && nearT < farT && nearT < hitT) {
                        var localNormalX = 0.0f;
                        var localNormalY = 0.0f;
                        var localNormalZ = 0.0f;
                        if (nearT == nearest.x) {
                            localNormalX = Gpu.Select(1.0f, -1.0f, Gpu.GreaterThan(localDirection.x, 0.0f));
                        }
                        else if (nearT == nearest.y) {
                            localNormalY = Gpu.Select(1.0f, -1.0f, Gpu.GreaterThan(localDirection.y, 0.0f));
                        }
                        else {
                            localNormalZ = Gpu.Select(1.0f, -1.0f, Gpu.GreaterThan(localDirection.z, 0.0f));
                        }
                        var localNormal = new float3(localNormalX, localNormalY, localNormalZ);
                        var normal = new float3(
                            cosine * localNormal.x - sine * localNormal.z,
                            localNormal.y,
                            sine * localNormal.x + cosine * localNormal.z);

                        hitT = nearT;
                        hitPosition = rayOrigin + rayDirection * nearT;
                        hitNormal = normal;
                        hitAlbedo = new float3(0.70f, 0.72f, 0.69f);
                        hitEmission = new float3(0.0f, 0.0f, 0.0f);
                        hitMaterial = Diffuse;
                    }
                }

                // intersect_sphere: center (-0.38, 0.88, 0.15), radius 0.22, mirror
                {
                    var center = new float3(-0.38f, 0.88f, 0.15f);
                    var radius = 0.22f;
                    var offset = rayOrigin - center;
                    var halfB = math.dot(offset, rayDirection);
                    var c = math.dot(offset, offset) - radius * radius;
                    var discriminant = halfB * halfB - c;
                    if (discriminant > 0.0f) {
                        var root = -halfB - Gpu.Sqrt(discriminant);
                        if (root > Epsilon && root < hitT) {
                            var position = rayOrigin + rayDirection * root;
                            hitT = root;
                            hitPosition = position;
                            hitNormal = math.normalize(position - center);
                            hitAlbedo = new float3(0.92f, 0.76f, 0.45f);
                            hitEmission = new float3(0.0f, 0.0f, 0.0f);
                            hitMaterial = Mirror;
                        }
                    }
                }
                // ---- end intersect_scene ----

                // The compiler's structured-control-flow lowering does not
                // tolerate break/continue nested several levels deep or
                // scattered mid-loop, and it fares far better with a
                // genuinely exclusive if/else-if/else chain than with
                // several independently `!shouldBreak`-guarded ifs (the
                // guarded-if version merges the CFG back together after
                // every single condition instead of once). shouldBreak is
                // still needed for the single break at the loop's end; the
                // "mirror bounce" shortcut (continue, in the WGSL original)
                // becomes this chain's own branch instead of an actual
                // continue.
                var shouldBreak = false;

                if (hitT >= 1e29f) {
                    shouldBreak = true;
                }
                else if (math.dot(hitEmission, hitEmission) > 0.0f) {
                    // previousWasSpecular is always 0u/1u: multiplying by it
                    // as a mask is exactly the original `if` guard, without
                    // a nested branch the structurizer chokes on here.
                    var specularMask = (float)previousWasSpecular;
                    radianceX += throughput.x * hitEmission.x * specularMask;
                    radianceY += throughput.y * hitEmission.y * specularMask;
                    radianceZ += throughput.z * hitEmission.z * specularMask;
                    shouldBreak = true;
                }
                else if (hitMaterial == Mirror) {
                    throughput = throughput * hitAlbedo;
                    rayOrigin = hitPosition + hitNormal * (Epsilon * 2.0f);
                    rayDirection = math.reflect(rayDirection, hitNormal);
                    previousWasSpecular = 1u;
                }
                else {
                // ---- sample_direct_light(hit, state) ----
                {
                    state = (state ^ 61u) ^ (state >> 16);
                    state = state * 9u;
                    state = state ^ (state >> 4);
                    state = state * 0x27d4eb2du;
                    state = state ^ (state >> 15);
                    var lightSeedX = (float)state * (1.0f / 4294967296.0f);
                    var lightPositionX = -0.36f + (0.36f - -0.36f) * lightSeedX;

                    state = (state ^ 61u) ^ (state >> 16);
                    state = state * 9u;
                    state = state ^ (state >> 4);
                    state = state * 0x27d4eb2du;
                    state = state ^ (state >> 15);
                    var lightSeedZ = (float)state * (1.0f / 4294967296.0f);
                    var lightPositionZ = -0.42f + (0.24f - -0.42f) * lightSeedZ;

                    var lightPosition = new float3(lightPositionX, 1.999f, lightPositionZ);
                    var toLight = lightPosition - hitPosition;
                    var distanceSquared = math.dot(toLight, toLight);
                    var distance = Gpu.Sqrt(distanceSquared);
                    var lightDirection = toLight / distance;
                    var surfaceCosine = Gpu.Max(0.0f, math.dot(hitNormal, lightDirection));
                    var lightCosine = Gpu.Max(0.0f, lightDirection.y);

                    if (surfaceCosine > 0.0f && lightCosine > 0.0f) {
                        var shadowOrigin = hitPosition + hitNormal * (Epsilon * 2.0f);
                        var shadowDirection = lightDirection;

                        // ---- intersect_scene(shadow_ray) ----
                        var blockerT = 1e30f;
                        {
                            // A shadow ray only needs the occlusion distance,
                            // never the surface's albedo/normal/material —
                            // unlike the primary-ray copy above, this pass
                            // never reads `white`.
                            var t = (-1.0f - shadowOrigin.x) / shadowDirection.x;
                            var point = shadowOrigin + shadowDirection * t;
                            if (point.y >= 0.0f && point.y <= 2.0f && point.z >= -1.0f && point.z <= 1.0f &&
                                t > Epsilon && t < blockerT) {
                                blockerT = t;
                            }

                            t = (1.0f - shadowOrigin.x) / shadowDirection.x;
                            point = shadowOrigin + shadowDirection * t;
                            if (point.y >= 0.0f && point.y <= 2.0f && point.z >= -1.0f && point.z <= 1.0f &&
                                t > Epsilon && t < blockerT) {
                                blockerT = t;
                            }

                            t = (0.0f - shadowOrigin.y) / shadowDirection.y;
                            point = shadowOrigin + shadowDirection * t;
                            if (point.x >= -1.0f && point.x <= 1.0f && point.z >= -1.0f && point.z <= 1.0f &&
                                t > Epsilon && t < blockerT) {
                                blockerT = t;
                            }

                            t = (2.0f - shadowOrigin.y) / shadowDirection.y;
                            point = shadowOrigin + shadowDirection * t;
                            if (point.x >= -1.0f && point.x <= 1.0f && point.z >= -1.0f && point.z <= 1.0f &&
                                t > Epsilon && t < blockerT) {
                                blockerT = t;
                            }

                            t = (-1.0f - shadowOrigin.z) / shadowDirection.z;
                            point = shadowOrigin + shadowDirection * t;
                            if (point.x >= -1.0f && point.x <= 1.0f && point.y >= 0.0f && point.y <= 2.0f &&
                                t > Epsilon && t < blockerT) {
                                blockerT = t;
                            }

                            // Shadow-ray box test #1 (same box as the primary-ray test above).
                            {
                                var angle = -0.24f;
                                var center = new float3(-0.38f, 0.34f, 0.15f);
                                var halfExtent = new float3(0.38f, 0.34f, 0.38f);
                                var cosine = Gpu.Cos(angle);
                                var sine = Gpu.Sin(angle);
                                var relativeOrigin = shadowOrigin - center;
                                var localOrigin = new float3(
                                    cosine * relativeOrigin.x + sine * relativeOrigin.z,
                                    relativeOrigin.y,
                                    -sine * relativeOrigin.x + cosine * relativeOrigin.z);
                                var localDirection = new float3(
                                    cosine * shadowDirection.x + sine * shadowDirection.z,
                                    shadowDirection.y,
                                    -sine * shadowDirection.x + cosine * shadowDirection.z);
                                var inverseDirection = new float3(1.0f, 1.0f, 1.0f) / localDirection;
                                var first = (-halfExtent - localOrigin) * inverseDirection;
                                var second = (halfExtent - localOrigin) * inverseDirection;
                                var nearest = math.min(first, second);
                                var farthest = math.max(first, second);
                                var nearT = Gpu.Max(Gpu.Max(nearest.x, nearest.y), nearest.z);
                                var farT = Gpu.Min(Gpu.Min(farthest.x, farthest.y), farthest.z);
                                if (nearT > Epsilon && nearT < farT && nearT < blockerT) {
                                    blockerT = nearT;
                                }
                            }

                            // Shadow-ray box test #2.
                            {
                                var angle = 0.30f;
                                var center = new float3(0.39f, 0.69f, -0.29f);
                                var halfExtent = new float3(0.31f, 0.69f, 0.31f);
                                var cosine = Gpu.Cos(angle);
                                var sine = Gpu.Sin(angle);
                                var relativeOrigin = shadowOrigin - center;
                                var localOrigin = new float3(
                                    cosine * relativeOrigin.x + sine * relativeOrigin.z,
                                    relativeOrigin.y,
                                    -sine * relativeOrigin.x + cosine * relativeOrigin.z);
                                var localDirection = new float3(
                                    cosine * shadowDirection.x + sine * shadowDirection.z,
                                    shadowDirection.y,
                                    -sine * shadowDirection.x + cosine * shadowDirection.z);
                                var inverseDirection = new float3(1.0f, 1.0f, 1.0f) / localDirection;
                                var first = (-halfExtent - localOrigin) * inverseDirection;
                                var second = (halfExtent - localOrigin) * inverseDirection;
                                var nearest = math.min(first, second);
                                var farthest = math.max(first, second);
                                var nearT = Gpu.Max(Gpu.Max(nearest.x, nearest.y), nearest.z);
                                var farT = Gpu.Min(Gpu.Min(farthest.x, farthest.y), farthest.z);
                                if (nearT > Epsilon && nearT < farT && nearT < blockerT) {
                                    blockerT = nearT;
                                }
                            }

                            var sphereCenter = new float3(-0.38f, 0.88f, 0.15f);
                            var sphereRadius = 0.22f;
                            var sphereOffset = shadowOrigin - sphereCenter;
                            var halfB = math.dot(sphereOffset, shadowDirection);
                            var sphereC = math.dot(sphereOffset, sphereOffset) - sphereRadius * sphereRadius;
                            var discriminant = halfB * halfB - sphereC;
                            if (discriminant > 0.0f) {
                                var root = -halfB - Gpu.Sqrt(discriminant);
                                if (root > Epsilon && root < blockerT) {
                                    blockerT = root;
                                }
                            }
                        }
                        // ---- end intersect_scene(shadow_ray) ----

                        if (blockerT >= distance - 0.01f) {
                            var lightArea = 0.72f * 0.66f;
                            var lightEmissionX = 18.0f;
                            var lightEmissionY = 15.0f;
                            var lightEmissionZ = 10.5f;
                            var factor = surfaceCosine * lightCosine * lightArea / (Pi * distanceSquared);
                            radianceX += throughput.x * hitAlbedo.x * lightEmissionX * factor;
                            radianceY += throughput.y * hitAlbedo.y * lightEmissionY * factor;
                            radianceZ += throughput.z * hitAlbedo.z * lightEmissionZ * factor;
                        }
                    }
                }
                // ---- end sample_direct_light ----

                throughput = throughput * hitAlbedo;
                previousWasSpecular = 0u;

                if (bounce >= 3u) {
                    var survival = Gpu.Max(0.1f, Gpu.Min(0.95f, Gpu.Max(Gpu.Max(throughput.x, throughput.y), throughput.z)));
                    state = (state ^ 61u) ^ (state >> 16);
                    state = state * 9u;
                    state = state ^ (state >> 4);
                    state = state * 0x27d4eb2du;
                    state = state ^ (state >> 15);
                    var survivalSeed = (float)state * (1.0f / 4294967296.0f);
                    if (survivalSeed > survival) {
                        shouldBreak = true;
                    }
                    else {
                        throughput = throughput / survival;
                    }
                }

                if (!shouldBreak) {
                    // ---- cosine_hemisphere(hit.normal, state) ----
                    state = (state ^ 61u) ^ (state >> 16);
                    state = state * 9u;
                    state = state ^ (state >> 4);
                    state = state * 0x27d4eb2du;
                    state = state ^ (state >> 15);
                    var hemisphereSeed0 = (float)state * (1.0f / 4294967296.0f);

                    state = (state ^ 61u) ^ (state >> 16);
                    state = state * 9u;
                    state = state ^ (state >> 4);
                    state = state * 0x27d4eb2du;
                    state = state ^ (state >> 15);
                    var hemisphereSeed1 = (float)state * (1.0f / 4294967296.0f);

                    var hemisphereRadius = Gpu.Sqrt(hemisphereSeed0);
                    var hemisphereAngle = 2.0f * Pi * hemisphereSeed1;
                    var localX = hemisphereRadius * Gpu.Cos(hemisphereAngle);
                    var localY = Gpu.Sqrt(Gpu.Max(0.0f, 1.0f - hemisphereSeed0));
                    var localZ = hemisphereRadius * Gpu.Sin(hemisphereAngle);
                    var helper = new float3(0.0f, 1.0f, 0.0f);
                    if (Gpu.Abs(hitNormal.y) > 0.999f) {
                        helper = new float3(1.0f, 0.0f, 0.0f);
                    }
                    var tangent = math.normalize(math.cross(helper, hitNormal));
                    var bitangent = math.cross(hitNormal, tangent);
                    var bounceDirection = math.normalize(tangent * localX + hitNormal * localY + bitangent * localZ);

                    rayOrigin = hitPosition + hitNormal * (Epsilon * 2.0f);
                    rayDirection = bounceDirection;
                }
                } // end diffuse-surface else branch

                if (shouldBreak) {
                    break;
                }
            }

            sampleSumX += radianceX;
            sampleSumY += radianceY;
            sampleSumZ += radianceZ;
        }

        var sampleDivisor = Gpu.Max(1.0f, sampleCount);
        var averageX = sampleSumX / sampleDivisor;
        var averageY = sampleSumY / sampleDivisor;
        var averageZ = sampleSumZ / sampleDivisor;

        var previousX = 0.0f;
        var previousY = 0.0f;
        var previousZ = 0.0f;
        if (frame > 0.0f) {
            previousX = previousAccumulation.Load((int)pixelX, (int)pixelY, 0);
            previousY = previousAccumulation.Load((int)pixelX, (int)pixelY, 1);
            previousZ = previousAccumulation.Load((int)pixelX, (int)pixelY, 2);
        }

        var accumulatedX = (previousX * frame + averageX) / (frame + 1.0f);
        var accumulatedY = (previousY * frame + averageY) / (frame + 1.0f);
        var accumulatedZ = (previousZ * frame + averageZ) / (frame + 1.0f);

        Gpu.SetOutput(0, accumulatedX, accumulatedY, accumulatedZ, 1.0f);
    }

    [SpirvFragmentShader]
    public static void PresentFragment(
        Texture2D accumulation,
        float cameraX,
        float cameraY,
        float cameraZ,
        float exposure,
        float targetX,
        float targetY,
        float targetZ,
        float fieldOfView,
        float resolutionX,
        float resolutionY,
        float frame,
        float sampleCount,
        float bounceLimit,
        float aperture,
        float focusDistance,
        float gammaToggle)
    {
        var pixelX = (int)Gpu.GetFragmentPosition(0);
        var pixelY = (int)Gpu.GetFragmentPosition(1);
        var red = accumulation.Load(pixelX, pixelY, 0);
        var green = accumulation.Load(pixelX, pixelY, 1);
        var blue = accumulation.Load(pixelX, pixelY, 2);

        red *= exposure;
        green *= exposure;
        blue *= exposure;

        // aces_filmic
        var numeratorR = red * (2.51f * red + 0.03f);
        var numeratorG = green * (2.51f * green + 0.03f);
        var numeratorB = blue * (2.51f * blue + 0.03f);
        var denominatorR = red * (2.43f * red + 0.59f) + 0.14f;
        var denominatorG = green * (2.43f * green + 0.59f) + 0.14f;
        var denominatorB = blue * (2.43f * blue + 0.59f) + 0.14f;
        red = Gpu.Saturate(numeratorR / denominatorR);
        green = Gpu.Saturate(numeratorG / denominatorG);
        blue = Gpu.Saturate(numeratorB / denominatorB);

        if (gammaToggle < 0.5f) {
            var inverseGamma = 1.0f / 2.2f;
            red = Gpu.Pow(red, inverseGamma);
            green = Gpu.Pow(green, inverseGamma);
            blue = Gpu.Pow(blue, inverseGamma);
        }

        var u = Gpu.GetInput(0, 0) * 2.0f - 1.0f;
        var v = Gpu.GetInput(0, 1) * 2.0f - 1.0f;
        var vignette = 1.0f - 0.12f * (u * u + v * v);

        Gpu.SetOutput(0, red * vignette, green * vignette, blue * vignette, 1.0f);
    }
}
