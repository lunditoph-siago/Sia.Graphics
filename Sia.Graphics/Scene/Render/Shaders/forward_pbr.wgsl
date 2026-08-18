#import scene::common::{CameraUniform, InstanceData, VertexInput}
#import scene::clustered_forward::{ClusterConfig, cluster_z_slice_from_view_z, cluster_tile_from_screen, cluster_index}
#import scene::pbr::{direct_lighting, indirect_lighting}
#import scene::shadows::{ShadowConfig, shadow_select_cascade, shadow_sample_pcf}

struct ClusteredLight {
    position_range: vec4<f32>,
    direction_kind: vec4<f32>,
    color_intensity: vec4<f32>,
    spot_angles: vec4<f32>,
};

struct DirectionalLight {
    direction_pad: vec4<f32>,
    color_intensity: vec4<f32>,
};

struct DirectionalLightBuffer {
    count: vec4<u32>,
    lights: array<DirectionalLight, 4>,
};

@group(0) @binding(0) var<uniform> camera: CameraUniform;
@group(0) @binding(1) var<storage, read> instances: array<InstanceData>;

@group(1) @binding(0) var<uniform> cluster_config: ClusterConfig;
@group(1) @binding(1) var<storage, read> clustered_lights: array<ClusteredLight>;
@group(1) @binding(2) var<storage, read> light_grid: array<vec2<u32>>;
@group(1) @binding(3) var<storage, read> light_index_list: array<u32>;
@group(1) @binding(4) var<uniform> directional_lights: DirectionalLightBuffer;
@group(1) @binding(5) var shadow_atlas: texture_depth_2d_array;
@group(1) @binding(6) var shadow_sampler: sampler_comparison;
@group(1) @binding(7) var<storage, read> shadow_layers: array<mat4x4<f32>>;
@group(1) @binding(8) var<uniform> shadow_config: ShadowConfig;

struct IblSh {
    coefficients: array<vec4<f32>, 9>,
};

@group(2) @binding(0) var<uniform> ibl_sh: IblSh;
@group(2) @binding(1) var ibl_prefiltered: texture_cube<f32>;
@group(2) @binding(2) var ibl_prefiltered_sampler: sampler;
@group(2) @binding(3) var ibl_brdf_lut: texture_2d<f32>;
@group(2) @binding(4) var ibl_brdf_lut_sampler: sampler;

const IBL_PREFILTERED_MIP_COUNT: f32 = 7.0;

struct VertexOutput {
    @builtin(position) clip_position: vec4<f32>,
    @location(0) world_position: vec3<f32>,
    @location(1) world_normal: vec3<f32>,
    @location(2) base_color: vec4<f32>,
    @location(3) material_params: vec4<f32>,
    @location(4) emissive: vec4<f32>,
};

@vertex
fn vertex(input: VertexInput, @builtin(instance_index) instance_index: u32) -> VertexOutput {
    let instance = instances[instance_index];
    let world_position = instance.world_matrix * vec4<f32>(input.position, 1.0);
    let world_normal = normalize((instance.normal_matrix * vec4<f32>(input.normal, 0.0)).xyz);

    var output: VertexOutput;
    output.clip_position = camera.view_proj * world_position;
    output.world_position = world_position.xyz;
    output.world_normal = world_normal;
    output.base_color = instance.base_color;
    output.material_params = instance.material_params;
    output.emissive = instance.emissive;
    return output;
}

@fragment
fn fragment(input: VertexOutput) -> @location(0) vec4<f32> {
    let normal = normalize(input.world_normal);
    let view_dir = normalize(camera.world_position.xyz - input.world_position);
    let base_color = input.base_color.rgb;
    let metallic = input.material_params.x;
    let roughness = clamp(input.material_params.y, 0.045, 1.0);

    var accumulated = vec3<f32>(0.0);

    let view_z = (cluster_config.view * vec4<f32>(input.world_position, 1.0)).z;

    let directional_count = directional_lights.count.x;
    for (var i = 0u; i < directional_count; i = i + 1u) {
        let light = directional_lights.lights[i];
        let light_dir = normalize(-light.direction_pad.xyz);
        let radiance = light.color_intensity.rgb * light.color_intensity.a;

        var visibility = 1.0;
        if (i == 0u && shadow_config.params.y != 0u) {
            let cascade = shadow_select_cascade(shadow_config, view_z);
            visibility = shadow_sample_pcf(
                shadow_atlas, cascade, shadow_layers[cascade], input.world_position);
        }

        accumulated += visibility * direct_lighting(
            normal, view_dir, light_dir, radiance, base_color, metallic, roughness);
    }

    let slice = cluster_z_slice_from_view_z(cluster_config, view_z);
    let tile = cluster_tile_from_screen(cluster_config, input.clip_position.xy);
    let cell = cluster_index(cluster_config, tile, slice);
    let cell_info = light_grid[cell];
    let offset = cell_info.x;
    let count = cell_info.y;

    for (var i = 0u; i < count; i = i + 1u) {
        let light_index = light_index_list[offset + i];
        let light = clustered_lights[light_index];
        let to_light = light.position_range.xyz - input.world_position;
        let distance = length(to_light);
        let range = light.position_range.w;
        if (distance >= range) {
            continue;
        }
        let light_dir = to_light / max(distance, 1e-4);

        var attenuation = 1.0 / max(distance * distance, 1e-4);
        let window = clamp(1.0 - pow(distance / range, 4.0), 0.0, 1.0);
        attenuation *= window * window;

        let is_spot = light.direction_kind.w > 0.5;
        if (is_spot) {
            let spot_dir = normalize(light.direction_kind.xyz);
            let cos_angle = dot(-light_dir, spot_dir);
            let inner_cos = light.spot_angles.x;
            let outer_cos = light.spot_angles.y;
            let spot_factor = clamp((cos_angle - outer_cos) / max(inner_cos - outer_cos, 1e-4), 0.0, 1.0);
            attenuation *= spot_factor * spot_factor;
        }

        var visibility = 1.0;
        let shadow_layer = i32(light.spot_angles.z);
        if (is_spot && shadow_layer >= 0) {
            visibility = shadow_sample_pcf(
                shadow_atlas, shadow_layer, shadow_layers[shadow_layer], input.world_position);
        }

        let radiance = light.color_intensity.rgb * light.color_intensity.a * attenuation;
        accumulated += visibility * direct_lighting(
            normal, view_dir, light_dir, radiance, base_color, metallic, roughness);
    }

    let ambient = indirect_lighting(
        normal, view_dir, base_color, metallic, roughness, ibl_sh.coefficients,
        ibl_prefiltered, ibl_prefiltered_sampler, IBL_PREFILTERED_MIP_COUNT,
        ibl_brdf_lut, ibl_brdf_lut_sampler);
    let color = accumulated + ambient + input.emissive.rgb * input.emissive.a;
    return vec4<f32>(color, input.base_color.a);
}
