#import scene::common::{CameraUniform, InstanceData, VertexInput}

@group(0) @binding(0) var<uniform> camera: CameraUniform;
@group(0) @binding(1) var<storage, read> instances: array<InstanceData>;

struct VertexOutput {
    @builtin(position) clip_position: vec4<f32>,
    @location(0) base_color: vec4<f32>,
};

@vertex
fn vertex(input: VertexInput, @builtin(instance_index) instance_index: u32) -> VertexOutput {
    let instance = instances[instance_index];
    let world_position = instance.world_matrix * vec4<f32>(input.position, 1.0);

    var output: VertexOutput;
    output.clip_position = camera.view_proj * world_position;
    output.base_color = instance.base_color;
    return output;
}

@fragment
fn fragment(input: VertexOutput) -> @location(0) vec4<f32> {
    return input.base_color;
}
