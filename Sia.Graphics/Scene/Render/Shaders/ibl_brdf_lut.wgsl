#import scene::ibl::{ibl_fullscreen_ndc, hammersley, importance_sample_ggx}

struct VertexOutput {
    @builtin(position) clip_position: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex
fn vertex(@builtin(vertex_index) vertex_index: u32) -> VertexOutput {
    let ndc = ibl_fullscreen_ndc(vertex_index);
    var output: VertexOutput;
    output.clip_position = vec4<f32>(ndc, 0.0, 1.0);
    output.uv = vec2<f32>(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
    return output;
}

fn geometry_schlick_ggx_ibl(n_dot_v: f32, roughness: f32) -> f32 {
    let k = (roughness * roughness) / 2.0;
    return n_dot_v / (n_dot_v * (1.0 - k) + k);
}

fn geometry_smith_ibl(n_dot_v: f32, n_dot_l: f32, roughness: f32) -> f32 {
    return geometry_schlick_ggx_ibl(n_dot_v, roughness) * geometry_schlick_ggx_ibl(n_dot_l, roughness);
}

const BRDF_LUT_SAMPLE_COUNT: u32 = 256u;

@fragment
fn fragment(input: VertexOutput) -> @location(0) vec4<f32> {
    let n_dot_v = clamp(input.uv.x, 0.001, 1.0);
    let roughness = clamp(input.uv.y, 0.001, 1.0);

    let v = vec3<f32>(sqrt(1.0 - n_dot_v * n_dot_v), 0.0, n_dot_v);
    let n = vec3<f32>(0.0, 0.0, 1.0);

    var scale = 0.0;
    var bias = 0.0;
    for (var i = 0u; i < BRDF_LUT_SAMPLE_COUNT; i = i + 1u) {
        let xi = hammersley(i, BRDF_LUT_SAMPLE_COUNT);
        let h = importance_sample_ggx(xi, roughness, n);
        let l = normalize(2.0 * dot(v, h) * h - v);

        let n_dot_l = max(l.z, 0.0);
        let n_dot_h = max(h.z, 0.0);
        let v_dot_h = max(dot(v, h), 0.0);

        if (n_dot_l > 0.0) {
            let g = geometry_smith_ibl(n_dot_v, n_dot_l, roughness);
            let g_vis = (g * v_dot_h) / max(n_dot_h * n_dot_v, 1e-4);
            let fc = pow(1.0 - v_dot_h, 5.0);
            scale += (1.0 - fc) * g_vis;
            bias += fc * g_vis;
        }
    }

    let inv_count = 1.0 / f32(BRDF_LUT_SAMPLE_COUNT);
    return vec4<f32>(scale * inv_count, bias * inv_count, 0.0, 1.0);
}
