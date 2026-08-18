#define_import_path scene::shadows

struct ShadowConfig {
    cascade_splits: vec4<f32>,
    params: vec4<u32>,
};

fn shadow_select_cascade(config: ShadowConfig, view_z: f32) -> i32 {
    let depth = -view_z;
    if (depth < config.cascade_splits.x) {
        return 0;
    }
    if (depth < config.cascade_splits.y) {
        return 1;
    }
    if (depth < config.cascade_splits.z) {
        return 2;
    }
    return 3;
}

fn shadow_sample_pcf(
    atlas: texture_depth_2d_array,
    layer: i32,
    view_proj: mat4x4<f32>,
    world_position: vec3<f32>,
) -> f32 {
    let clip = view_proj * vec4<f32>(world_position, 1.0);
    if (clip.w <= 0.0) {
        return 1.0;
    }
    let ndc = clip.xyz / clip.w;
    if (ndc.x < -1.0 || ndc.x > 1.0 || ndc.y < -1.0 || ndc.y > 1.0 || ndc.z < 0.0 || ndc.z > 1.0) {
        return 1.0;
    }
    let uv = vec2<f32>(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
    let dims = vec2<i32>(textureDimensions(atlas));
    let center = vec2<i32>(uv * vec2<f32>(dims));
    let depth_ref = ndc.z - 0.0015;

    var lit = 0.0;
    var samples = 0.0;
    for (var dy = -1; dy <= 1; dy = dy + 1) {
        for (var dx = -1; dx <= 1; dx = dx + 1) {
            let coord = clamp(center + vec2<i32>(dx, dy), vec2<i32>(0, 0), dims - vec2<i32>(1, 1));
            let stored_depth = textureLoad(atlas, coord, layer, 0);
            lit += select(0.0, 1.0, depth_ref <= stored_depth);
            samples += 1.0;
        }
    }
    return lit / samples;
}
