#define_import_path scene::ibl

const IBL_PI: f32 = 3.14159265359;
const IBL_SH_A0: f32 = 3.14159265359;
const IBL_SH_A1: f32 = 2.09439510239;
const IBL_SH_A2: f32 = 0.78539816339;
const IBL_SH_Y00: f32 = 0.282095;
const IBL_SH_Y1: f32 = 0.488603;
const IBL_SH_Y2MN: f32 = 1.092548;
const IBL_SH_Y20: f32 = 0.315392;
const IBL_SH_Y22: f32 = 0.546274;
const IBL_SKY_EXPOSURE: f32 = 0.25;

fn sky_color(dir: vec3<f32>, sun_dir: vec3<f32>, sun_color: vec3<f32>) -> vec3<f32> {
    let horizon = vec3<f32>(0.55, 0.6, 0.68);
    let zenith = vec3<f32>(0.12, 0.24, 0.55);
    let ground = vec3<f32>(0.08, 0.08, 0.07);
    let up = clamp(dir.y, -1.0, 1.0);
    let sky = mix(horizon, zenith, clamp(up, 0.0, 1.0));
    let base = mix(ground, sky, smoothstep(-0.15, 0.05, up));
    let sun_amount = max(dot(dir, sun_dir), 0.0);
    let sun_glow = sun_color * pow(sun_amount, 256.0) * 8.0;
    return (base + sun_glow) * IBL_SKY_EXPOSURE;
}

fn evaluate_sh_irradiance(sh: array<vec4<f32>, 9>, n: vec3<f32>) -> vec3<f32> {
    var result = sh[0].rgb * (IBL_SH_A0 * IBL_SH_Y00);
    result += sh[1].rgb * (IBL_SH_A1 * IBL_SH_Y1 * n.y);
    result += sh[2].rgb * (IBL_SH_A1 * IBL_SH_Y1 * n.z);
    result += sh[3].rgb * (IBL_SH_A1 * IBL_SH_Y1 * n.x);
    result += sh[4].rgb * (IBL_SH_A2 * IBL_SH_Y2MN * n.x * n.y);
    result += sh[5].rgb * (IBL_SH_A2 * IBL_SH_Y2MN * n.y * n.z);
    result += sh[6].rgb * (IBL_SH_A2 * IBL_SH_Y20 * (3.0 * n.z * n.z - 1.0));
    result += sh[7].rgb * (IBL_SH_A2 * IBL_SH_Y2MN * n.x * n.z);
    result += sh[8].rgb * (IBL_SH_A2 * IBL_SH_Y22 * (n.x * n.x - n.y * n.y));
    return max(result, vec3<f32>(0.0));
}

fn sample_prefiltered_specular(
    env: texture_cube<f32>, samp: sampler, reflect_dir: vec3<f32>, roughness: f32, mip_count: f32,
) -> vec3<f32> {
    return textureSampleLevel(env, samp, reflect_dir, roughness * (mip_count - 1.0)).rgb;
}

fn sample_brdf_lut(lut: texture_2d<f32>, samp: sampler, n_dot_v: f32, roughness: f32) -> vec2<f32> {
    let uv = vec2<f32>(clamp(n_dot_v, 0.0, 1.0), clamp(roughness, 0.0, 1.0));
    return textureSampleLevel(lut, samp, uv, 0.0).rg;
}

fn fresnel_schlick_roughness(cos_theta: f32, f0: vec3<f32>, roughness: f32) -> vec3<f32> {
    let max_reflectance = max(vec3<f32>(1.0 - roughness), f0);
    return f0 + (max_reflectance - f0) * pow(clamp(1.0 - cos_theta, 0.0, 1.0), 5.0);
}

fn ibl_fullscreen_ndc(vertex_index: u32) -> vec2<f32> {
    let x = f32((vertex_index << 1u) & 2u) * 2.0 - 1.0;
    let y = f32(vertex_index & 2u) * 2.0 - 1.0;
    return vec2<f32>(x, y);
}

fn radical_inverse_vdc(bits_in: u32) -> f32 {
    var bits = bits_in;
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
    bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
    bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
    return f32(bits) * 2.3283064365386963e-10;
}

fn hammersley(i: u32, n: u32) -> vec2<f32> {
    return vec2<f32>(f32(i) / f32(n), radical_inverse_vdc(i));
}

fn importance_sample_ggx(xi: vec2<f32>, roughness: f32, normal: vec3<f32>) -> vec3<f32> {
    let a = roughness * roughness;
    let phi = 2.0 * IBL_PI * xi.x;
    let cos_theta = sqrt((1.0 - xi.y) / (1.0 + (a * a - 1.0) * xi.y));
    let sin_theta = sqrt(max(1.0 - cos_theta * cos_theta, 0.0));
    let h_tangent = vec3<f32>(cos(phi) * sin_theta, sin(phi) * sin_theta, cos_theta);

    let up = select(vec3<f32>(1.0, 0.0, 0.0), vec3<f32>(0.0, 0.0, 1.0), abs(normal.z) < 0.999);
    let tangent_x = normalize(cross(up, normal));
    let tangent_y = cross(normal, tangent_x);
    return normalize(tangent_x * h_tangent.x + tangent_y * h_tangent.y + normal * h_tangent.z);
}

fn cube_face_direction(face: u32, uv: vec2<f32>) -> vec3<f32> {
    if (face == 0u) {
        return normalize(vec3<f32>(1.0, -uv.y, -uv.x));
    }
    if (face == 1u) {
        return normalize(vec3<f32>(-1.0, -uv.y, uv.x));
    }
    if (face == 2u) {
        return normalize(vec3<f32>(uv.x, 1.0, uv.y));
    }
    if (face == 3u) {
        return normalize(vec3<f32>(uv.x, -1.0, -uv.y));
    }
    if (face == 4u) {
        return normalize(vec3<f32>(uv.x, -uv.y, 1.0));
    }
    return normalize(vec3<f32>(-uv.x, -uv.y, -1.0));
}
