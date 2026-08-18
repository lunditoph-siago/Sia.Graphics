#define_import_path scene::clustered_forward

struct ClusterConfig {
    view: mat4x4<f32>,
    inv_proj: mat4x4<f32>,
    tiles: vec4<u32>,
    z_factors: vec4<f32>,
    screen: vec4<f32>,
};

fn cluster_view_z_at_slice(config: ClusterConfig, slice: f32) -> f32 {
    return -exp((slice + config.z_factors.y) / config.z_factors.x);
}

fn cluster_z_slice_from_view_z(config: ClusterConfig, view_z: f32) -> u32 {
    let raw = log(max(-view_z, 0.00001)) * config.z_factors.x - config.z_factors.y;
    return min(u32(max(raw, 0.0)), config.tiles.z - 1u);
}

fn cluster_tile_from_screen(config: ClusterConfig, frag_xy: vec2<f32>) -> vec2<u32> {
    let tx = u32(frag_xy.x / max(config.screen.x, 1.0) * f32(config.tiles.x));
    let ty = u32(frag_xy.y / max(config.screen.y, 1.0) * f32(config.tiles.y));
    return vec2<u32>(min(tx, config.tiles.x - 1u), min(ty, config.tiles.y - 1u));
}

fn cluster_index(config: ClusterConfig, tile: vec2<u32>, slice: u32) -> u32 {
    return tile.x + tile.y * config.tiles.x + slice * config.tiles.x * config.tiles.y;
}

fn cluster_screen_to_view_ray(config: ClusterConfig, ndc_xy: vec2<f32>) -> vec3<f32> {
    let clip = vec4<f32>(ndc_xy, 0.0, 1.0);
    let view = config.inv_proj * clip;
    return view.xyz / view.w;
}

fn cluster_line_intersection_to_z_plane(ray: vec3<f32>, z: f32) -> vec3<f32> {
    return ray * (z / ray.z);
}
