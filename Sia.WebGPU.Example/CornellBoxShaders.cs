namespace Sia.WebGPU.Example;

internal static class CornellBoxShaders
{
    public const string Source = """
        struct Uniforms {
            camera_position_exposure: vec4f,
            camera_target_fov: vec4f,
            resolution_frame: vec4f,
            render_settings: vec4f,
            presentation_settings: vec4f,
        }

        struct VertexOutput {
            @builtin(position) position: vec4f,
            @location(0) uv: vec2f,
        }

        struct Ray {
            origin: vec3f,
            direction: vec3f,
        }

        struct Hit {
            t: f32,
            position: vec3f,
            normal: vec3f,
            albedo: vec3f,
            emission: vec3f,
            material: u32,
        }

        @group(0) @binding(0) var<uniform> uniforms: Uniforms;
        @group(0) @binding(1) var previous_accumulation: texture_2d<f32>;

        const PI: f32 = 3.141592653589793;
        const EPSILON: f32 = 0.001;
        const DIFFUSE: u32 = 0u;
        const MIRROR: u32 = 1u;

        @vertex
        fn vs_main(@builtin(vertex_index) vertex_index: u32) -> VertexOutput {
            var positions = array<vec2f, 3>(
                vec2f(-1.0, -1.0),
                vec2f(3.0, -1.0),
                vec2f(-1.0, 3.0),
            );

            var output: VertexOutput;
            let position = positions[vertex_index];
            output.position = vec4f(position, 0.0, 1.0);
            output.uv = position * 0.5 + vec2f(0.5);
            return output;
        }

        fn random(state: ptr<function, u32>) -> f32 {
            var value = *state;
            value = (value ^ 61u) ^ (value >> 16u);
            value = value * 9u;
            value = value ^ (value >> 4u);
            value = value * 0x27d4eb2du;
            value = value ^ (value >> 15u);
            *state = value;
            return f32(value) * (1.0 / 4294967296.0);
        }

        fn rotate_y(value: vec3f, angle: f32) -> vec3f {
            let cosine = cos(angle);
            let sine = sin(angle);
            return vec3f(
                cosine * value.x + sine * value.z,
                value.y,
                -sine * value.x + cosine * value.z,
            );
        }

        fn commit_hit(
            ray: Ray,
            t: f32,
            normal: vec3f,
            albedo: vec3f,
            emission: vec3f,
            material: u32,
            hit: ptr<function, Hit>,
        ) {
            if (t > EPSILON && t < (*hit).t) {
                (*hit).t = t;
                (*hit).position = ray.origin + ray.direction * t;
                (*hit).normal = normal;
                (*hit).albedo = albedo;
                (*hit).emission = emission;
                (*hit).material = material;
            }
        }

        fn intersect_room(ray: Ray, hit: ptr<function, Hit>) {
            let white = vec3f(0.76, 0.73, 0.68);

            var t = (-1.0 - ray.origin.x) / ray.direction.x;
            var point = ray.origin + ray.direction * t;
            if (point.y >= 0.0 && point.y <= 2.0 && point.z >= -1.0 && point.z <= 1.0) {
                commit_hit(ray, t, vec3f(1.0, 0.0, 0.0), vec3f(0.63, 0.065, 0.05), vec3f(0.0), DIFFUSE, hit);
            }

            t = (1.0 - ray.origin.x) / ray.direction.x;
            point = ray.origin + ray.direction * t;
            if (point.y >= 0.0 && point.y <= 2.0 && point.z >= -1.0 && point.z <= 1.0) {
                commit_hit(ray, t, vec3f(-1.0, 0.0, 0.0), vec3f(0.12, 0.45, 0.15), vec3f(0.0), DIFFUSE, hit);
            }

            t = (0.0 - ray.origin.y) / ray.direction.y;
            point = ray.origin + ray.direction * t;
            if (point.x >= -1.0 && point.x <= 1.0 && point.z >= -1.0 && point.z <= 1.0) {
                commit_hit(ray, t, vec3f(0.0, 1.0, 0.0), white, vec3f(0.0), DIFFUSE, hit);
            }

            t = (2.0 - ray.origin.y) / ray.direction.y;
            point = ray.origin + ray.direction * t;
            if (point.x >= -1.0 && point.x <= 1.0 && point.z >= -1.0 && point.z <= 1.0) {
                var albedo = white;
                var emission = vec3f(0.0);
                if (abs(point.x) < 0.36 && point.z > -0.42 && point.z < 0.24) {
                    albedo = vec3f(0.0);
                    emission = vec3f(18.0, 15.0, 10.5);
                }
                commit_hit(ray, t, vec3f(0.0, -1.0, 0.0), albedo, emission, DIFFUSE, hit);
            }

            t = (-1.0 - ray.origin.z) / ray.direction.z;
            point = ray.origin + ray.direction * t;
            if (point.x >= -1.0 && point.x <= 1.0 && point.y >= 0.0 && point.y <= 2.0) {
                commit_hit(ray, t, vec3f(0.0, 0.0, 1.0), white, vec3f(0.0), DIFFUSE, hit);
            }
        }

        fn intersect_box(
            ray: Ray,
            center: vec3f,
            half_extent: vec3f,
            angle: f32,
            albedo: vec3f,
            hit: ptr<function, Hit>,
        ) {
            let local_origin = rotate_y(ray.origin - center, -angle);
            let local_direction = rotate_y(ray.direction, -angle);
            let inverse_direction = vec3f(1.0) / local_direction;
            let first = (-half_extent - local_origin) * inverse_direction;
            let second = (half_extent - local_origin) * inverse_direction;
            let nearest = min(first, second);
            let farthest = max(first, second);
            let near_t = max(max(nearest.x, nearest.y), nearest.z);
            let far_t = min(min(farthest.x, farthest.y), farthest.z);

            if (near_t <= EPSILON || near_t >= far_t || near_t >= (*hit).t) {
                return;
            }

            var local_normal = vec3f(0.0);
            if (near_t == nearest.x) {
                local_normal.x = select(1.0, -1.0, local_direction.x > 0.0);
            } else if (near_t == nearest.y) {
                local_normal.y = select(1.0, -1.0, local_direction.y > 0.0);
            }
            else {
                local_normal.z = select(1.0, -1.0, local_direction.z > 0.0);
            }

            commit_hit(ray, near_t, rotate_y(local_normal, angle), albedo, vec3f(0.0), DIFFUSE, hit);
        }

        fn intersect_sphere(
            ray: Ray,
            center: vec3f,
            radius: f32,
            albedo: vec3f,
            material: u32,
            hit: ptr<function, Hit>,
        ) {
            let offset = ray.origin - center;
            let half_b = dot(offset, ray.direction);
            let c = dot(offset, offset) - radius * radius;
            let discriminant = half_b * half_b - c;
            if (discriminant <= 0.0) {
                return;
            }

            let root = -half_b - sqrt(discriminant);
            if (root > EPSILON && root < (*hit).t) {
                let position = ray.origin + ray.direction * root;
                commit_hit(ray, root, normalize(position - center), albedo, vec3f(0.0), material, hit);
            }
        }

        fn intersect_scene(ray: Ray) -> Hit {
            var hit = Hit(
                1e30,
                vec3f(0.0),
                vec3f(0.0),
                vec3f(0.0),
                vec3f(0.0),
                DIFFUSE,
            );

            intersect_room(ray, &hit);
            intersect_box(
                ray,
                vec3f(-0.38, 0.34, 0.15),
                vec3f(0.38, 0.34, 0.38),
                -0.24,
                vec3f(0.74, 0.70, 0.63),
                &hit,
            );
            intersect_box(
                ray,
                vec3f(0.39, 0.69, -0.29),
                vec3f(0.31, 0.69, 0.31),
                0.30,
                vec3f(0.70, 0.72, 0.69),
                &hit,
            );
            intersect_sphere(
                ray,
                vec3f(-0.38, 0.88, 0.15),
                0.22,
                vec3f(0.92, 0.76, 0.45),
                MIRROR,
                &hit,
            );
            return hit;
        }

        fn cosine_hemisphere(normal: vec3f, state: ptr<function, u32>) -> vec3f {
            let first = random(state);
            let second = random(state);
            let radius = sqrt(first);
            let angle = 2.0 * PI * second;
            let local = vec3f(radius * cos(angle), sqrt(max(0.0, 1.0 - first)), radius * sin(angle));
            let helper = select(vec3f(0.0, 1.0, 0.0), vec3f(1.0, 0.0, 0.0), abs(normal.y) > 0.999);
            let tangent = normalize(cross(helper, normal));
            let bitangent = cross(normal, tangent);
            return normalize(tangent * local.x + normal * local.y + bitangent * local.z);
        }

        fn sample_direct_light(hit: Hit, state: ptr<function, u32>) -> vec3f {
            let light_position = vec3f(
                mix(-0.36, 0.36, random(state)),
                1.999,
                mix(-0.42, 0.24, random(state)),
            );
            let to_light = light_position - hit.position;
            let distance_squared = dot(to_light, to_light);
            let distance = sqrt(distance_squared);
            let light_direction = to_light / distance;
            let surface_cosine = max(0.0, dot(hit.normal, light_direction));
            let light_cosine = max(0.0, light_direction.y);
            if (surface_cosine <= 0.0 || light_cosine <= 0.0) {
                return vec3f(0.0);
            }

            let shadow_ray = Ray(hit.position + hit.normal * EPSILON * 2.0, light_direction);
            let blocker = intersect_scene(shadow_ray);
            if (blocker.t < distance - 0.01) {
                return vec3f(0.0);
            }

            let light_area = 0.72 * 0.66;
            let light_emission = vec3f(18.0, 15.0, 10.5);
            return hit.albedo * light_emission * (surface_cosine * light_cosine * light_area / (PI * distance_squared));
        }

        fn make_camera_ray(pixel: vec2u, state: ptr<function, u32>) -> Ray {
            let resolution = uniforms.resolution_frame.xy;
            let jitter = vec2f(random(state), random(state));
            let screen = ((vec2f(pixel) + jitter) / resolution) * 2.0 - vec2f(1.0);
            let camera_position = uniforms.camera_position_exposure.xyz;
            let forward = normalize(uniforms.camera_target_fov.xyz - camera_position);
            let right = normalize(cross(forward, vec3f(0.0, 1.0, 0.0)));
            let up = cross(right, forward);
            let aspect = resolution.x / resolution.y;
            let scale = tan(uniforms.camera_target_fov.w * 0.5);
            var direction = normalize(
                forward
                    + right * screen.x * aspect * scale
                    - up * screen.y * scale,
            );

            let aperture = uniforms.render_settings.z;
            let focus_distance = uniforms.render_settings.w;
            let disk_radius = sqrt(random(state)) * aperture;
            let disk_angle = random(state) * 2.0 * PI;
            let lens_offset = right * (cos(disk_angle) * disk_radius) + up * (sin(disk_angle) * disk_radius);
            let focus_point = camera_position + direction * focus_distance;
            let origin = camera_position + lens_offset;
            direction = normalize(focus_point - origin);
            return Ray(origin, direction);
        }

        fn trace_path(initial_ray: Ray, state: ptr<function, u32>) -> vec3f {
            var ray = initial_ray;
            var throughput = vec3f(1.0);
            var radiance = vec3f(0.0);
            var previous_was_specular = true;
            let bounce_limit = u32(uniforms.render_settings.y);

            for (var bounce = 0u; bounce < 12u; bounce++) {
                if (bounce >= bounce_limit) {
                    break;
                }

                let hit = intersect_scene(ray);
                if (hit.t >= 1e29) {
                    break;
                }

                if (dot(hit.emission, hit.emission) > 0.0) {
                    if (previous_was_specular) {
                        radiance += throughput * hit.emission;
                    }
                    break;
                }

                if (hit.material == MIRROR) {
                    throughput *= hit.albedo;
                    ray = Ray(hit.position + hit.normal * EPSILON * 2.0, reflect(ray.direction, hit.normal));
                    previous_was_specular = true;
                    continue;
                }

                radiance += throughput * sample_direct_light(hit, state);
                throughput *= hit.albedo;
                previous_was_specular = false;

                if (bounce >= 3u) {
                    let survival = clamp(max(max(throughput.r, throughput.g), throughput.b), 0.1, 0.95);
                    if (random(state) > survival) {
                        break;
                    }
                    throughput /= survival;
                }

                let direction = cosine_hemisphere(hit.normal, state);
                ray = Ray(hit.position + hit.normal * EPSILON * 2.0, direction);
            }

            return radiance;
        }

        @fragment
        fn path_main(input: VertexOutput) -> @location(0) vec4f {
            let pixel = vec2u(input.position.xy);
            let frame = u32(uniforms.resolution_frame.z);
            let sample_count = u32(uniforms.render_settings.x);
            var sample_sum = vec3f(0.0);

            for (var sample = 0u; sample < 8u; sample++) {
                if (sample >= sample_count) {
                    break;
                }
                var state = (pixel.x * 1973u + pixel.y * 9277u + frame * 26699u + sample * 17431u) | 1u;
                let ray = make_camera_ray(pixel, &state);
                sample_sum += trace_path(ray, &state);
            }

            let sample_average = sample_sum / f32(max(sample_count, 1u));
            var previous = vec3f(0.0);
            if (frame > 0u) {
                previous = textureLoad(previous_accumulation, vec2i(pixel), 0).rgb;
            }
            let accumulated = (previous * f32(frame) + sample_average) / f32(frame + 1u);
            return vec4f(accumulated, 1.0);
        }

        fn aces_filmic(color: vec3f) -> vec3f {
            let numerator = color * (2.51 * color + vec3f(0.03));
            let denominator = color * (2.43 * color + vec3f(0.59)) + vec3f(0.14);
            return clamp(numerator / denominator, vec3f(0.0), vec3f(1.0));
        }

        @fragment
        fn present_main(input: VertexOutput) -> @location(0) vec4f {
            let pixel = vec2i(input.position.xy);
            var color = textureLoad(previous_accumulation, pixel, 0).rgb;
            color = aces_filmic(color * uniforms.camera_position_exposure.w);
            if (uniforms.presentation_settings.x < 0.5) {
                color = pow(color, vec3f(1.0 / 2.2));
            }

            let centered = input.uv * 2.0 - vec2f(1.0);
            let vignette = 1.0 - 0.12 * dot(centered, centered);
            return vec4f(color * vignette, 1.0);
        }
        """;
}
