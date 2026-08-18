#import scene::clustered_forward::{ClusterConfig, cluster_view_z_at_slice, cluster_screen_to_view_ray, cluster_line_intersection_to_z_plane}

struct ClusteredLight {
    position_range: vec4<f32>,
    direction_kind: vec4<f32>,
    color_intensity: vec4<f32>,
    spot_angles: vec4<f32>,
};

const MAX_LOCAL_LIGHTS: u32 = 64u;

@group(0) @binding(0) var<uniform> config: ClusterConfig;
@group(0) @binding(1) var<storage, read> lights: array<ClusteredLight>;
@group(0) @binding(2) var<storage, read_write> light_grid: array<vec2<u32>>;
@group(0) @binding(3) var<storage, read_write> light_index_list: array<u32>;
@group(0) @binding(4) var<storage, read_write> cursor: atomic<u32>;

fn sphere_intersects_aabb(center: vec3<f32>, radius: f32, aabb_min: vec3<f32>, aabb_max: vec3<f32>) -> bool {
    let closest = clamp(center, aabb_min, aabb_max);
    let d = center - closest;
    return dot(d, d) <= radius * radius;
}

@compute @workgroup_size(64, 1, 1)
fn cull(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let cluster_count = config.tiles.x * config.tiles.y * config.tiles.z;
    let index = global_id.x;
    if (index >= cluster_count) {
        return;
    }

    let tiles_xy = config.tiles.x * config.tiles.y;
    let tx = index % config.tiles.x;
    let ty = (index / config.tiles.x) % config.tiles.y;
    let zs = index / tiles_xy;

    let ndc_min = vec2<f32>(
        f32(tx) / f32(config.tiles.x) * 2.0 - 1.0,
        f32(ty) / f32(config.tiles.y) * 2.0 - 1.0);
    let ndc_max = vec2<f32>(
        f32(tx + 1u) / f32(config.tiles.x) * 2.0 - 1.0,
        f32(ty + 1u) / f32(config.tiles.y) * 2.0 - 1.0);

    let ray_min = cluster_screen_to_view_ray(config, ndc_min);
    let ray_max = cluster_screen_to_view_ray(config, ndc_max);

    let z_near = cluster_view_z_at_slice(config, f32(zs));
    let z_far = cluster_view_z_at_slice(config, f32(zs + 1u));

    let p0 = cluster_line_intersection_to_z_plane(ray_min, z_near);
    let p1 = cluster_line_intersection_to_z_plane(ray_max, z_near);
    let p2 = cluster_line_intersection_to_z_plane(ray_min, z_far);
    let p3 = cluster_line_intersection_to_z_plane(ray_max, z_far);

    let aabb_min = min(min(p0, p1), min(p2, p3));
    let aabb_max = max(max(p0, p1), max(p2, p3));

    var matches: array<u32, MAX_LOCAL_LIGHTS>;
    var match_count = 0u;
    let light_count = u32(config.z_factors.z);

    for (var i = 0u; i < light_count && match_count < MAX_LOCAL_LIGHTS; i = i + 1u) {
        let light = lights[i];
        let view_position = (config.view * vec4<f32>(light.position_range.xyz, 1.0)).xyz;
        let radius = light.position_range.w;
        if (sphere_intersects_aabb(view_position, radius, aabb_min, aabb_max)) {
            matches[match_count] = i;
            match_count = match_count + 1u;
        }
    }

    var offset = 0u;
    if (match_count > 0u) {
        offset = atomicAdd(&cursor, match_count);
        let capacity = arrayLength(&light_index_list);
        for (var i = 0u; i < match_count; i = i + 1u) {
            let dst = offset + i;
            if (dst < capacity) {
                light_index_list[dst] = matches[i];
            }
        }
    }

    light_grid[index] = vec2<u32>(offset, match_count);
}
