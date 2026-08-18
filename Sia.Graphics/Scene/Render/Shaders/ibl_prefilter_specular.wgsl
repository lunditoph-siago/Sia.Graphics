#import scene::ibl::{sky_color, ibl_fullscreen_ndc, hammersley, importance_sample_ggx, cube_face_direction}

struct IblPrefilterParams {
    params: vec4<f32>,
    sun_dir: vec4<f32>,
    sun_color: vec4<f32>,
};

@group(0) @binding(0) var<uniform> prefilter: IblPrefilterParams;

struct VertexOutput {
    @builtin(position) clip_position: vec4<f32>,
    @location(0) ndc: vec2<f32>,
};

@vertex
fn vertex(@builtin(vertex_index) vertex_index: u32) -> VertexOutput {
    let ndc = ibl_fullscreen_ndc(vertex_index);
    var output: VertexOutput;
    output.clip_position = vec4<f32>(ndc, 0.0, 1.0);
    output.ndc = ndc;
    return output;
}

@fragment
fn fragment(input: VertexOutput) -> @location(0) vec4<f32> {
    let face = u32(prefilter.params.z);
    let roughness = prefilter.params.x;
    let sample_count = max(u32(prefilter.params.y), 1u);

    let n = cube_face_direction(face, input.ndc);
    let v = n;

    var accumulated = vec3<f32>(0.0);
    var total_weight = 0.0;
    for (var i = 0u; i < sample_count; i = i + 1u) {
        let xi = hammersley(i, sample_count);
        let h = importance_sample_ggx(xi, roughness, n);
        let l = normalize(2.0 * dot(v, h) * h - v);
        let n_dot_l = dot(n, l);
        if (n_dot_l > 0.0) {
            accumulated += sky_color(l, prefilter.sun_dir.xyz, prefilter.sun_color.xyz) * n_dot_l;
            total_weight += n_dot_l;
        }
    }

    if (total_weight <= 0.0) {
        return vec4<f32>(sky_color(n, prefilter.sun_dir.xyz, prefilter.sun_color.xyz), 1.0);
    }
    return vec4<f32>(accumulated / total_weight, 1.0);
}
