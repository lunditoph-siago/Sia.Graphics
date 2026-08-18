#import scene::common::{CameraUniform, InstanceData, VertexInput}

@group(0) @binding(0) var<uniform> camera: CameraUniform;
@group(0) @binding(1) var<storage, read> instances: array<InstanceData>;

@vertex
fn vertex(input: VertexInput, @builtin(instance_index) instance_index: u32) -> @builtin(position) vec4<f32> {
    let instance = instances[instance_index];
    let world_position = instance.world_matrix * vec4<f32>(input.position, 1.0);
    return camera.view_proj * world_position;
}
