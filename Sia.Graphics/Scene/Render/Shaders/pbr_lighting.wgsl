#define_import_path scene::pbr

const PBR_PI: f32 = 3.14159265359;

fn distribution_ggx(n_dot_h: f32, roughness: f32) -> f32 {
    let a = roughness * roughness;
    let a2 = a * a;
    let denom = (n_dot_h * n_dot_h) * (a2 - 1.0) + 1.0;
    return a2 / max(PBR_PI * denom * denom, 1e-6);
}

fn geometry_smith_ggx(n_dot_v: f32, n_dot_l: f32, roughness: f32) -> f32 {
    let r = roughness + 1.0;
    let k = (r * r) / 8.0;
    let ggx_v = n_dot_v / (n_dot_v * (1.0 - k) + k);
    let ggx_l = n_dot_l / (n_dot_l * (1.0 - k) + k);
    return ggx_v * ggx_l;
}

fn fresnel_schlick(cos_theta: f32, f0: vec3<f32>) -> vec3<f32> {
    return f0 + (vec3<f32>(1.0) - f0) * pow(clamp(1.0 - cos_theta, 0.0, 1.0), 5.0);
}

fn direct_lighting(
    normal: vec3<f32>,
    view_dir: vec3<f32>,
    light_dir: vec3<f32>,
    radiance: vec3<f32>,
    base_color: vec3<f32>,
    metallic: f32,
    roughness: f32,
) -> vec3<f32> {
    let half_dir = normalize(view_dir + light_dir);
    let n_dot_v = max(dot(normal, view_dir), 1e-4);
    let n_dot_l = max(dot(normal, light_dir), 0.0);
    let n_dot_h = max(dot(normal, half_dir), 0.0);
    let v_dot_h = max(dot(view_dir, half_dir), 0.0);

    if (n_dot_l <= 0.0) {
        return vec3<f32>(0.0);
    }

    let f0 = mix(vec3<f32>(0.04), base_color, metallic);
    let ndf = distribution_ggx(n_dot_h, roughness);
    let g = geometry_smith_ggx(n_dot_v, n_dot_l, roughness);
    let f = fresnel_schlick(v_dot_h, f0);

    let specular = (ndf * g * f) / max(4.0 * n_dot_v * n_dot_l, 1e-4);

    let k_s = f;
    let k_d = (vec3<f32>(1.0) - k_s) * (1.0 - metallic);
    let diffuse = k_d * base_color / PBR_PI;

    return (diffuse + specular) * radiance * n_dot_l;
}
