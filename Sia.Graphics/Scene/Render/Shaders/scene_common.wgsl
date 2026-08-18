#define_import_path scene::common

struct CameraUniform {
    view_proj: mat4x4<f32>,
    world_position: vec4<f32>,
};

struct InstanceData {
    world_matrix: mat4x4<f32>,
    normal_matrix: mat4x4<f32>,
    base_color: vec4<f32>,
};

struct VertexInput {
    @location(0) position: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) uv: vec2<f32>,
};
