#define_import_path sia::ui_node_vertex_buffer

const BORDER_LEFT: u32 = 1u;
const BORDER_TOP: u32 = 2u;
const BORDER_RIGHT: u32 = 4u;
const BORDER_BOTTOM: u32 = 8u;
const ATLAS_SIZE: f32 = 1024.0;

struct ViewUniform {
    clip_from_world: mat4x4<f32>,
}

@group(0) @binding(0) var<uniform> view: ViewUniform;

struct VertexInput {
    @builtin(vertex_index) vertex_index: u32,
    @location(0) transform: vec4<f32>,
    @location(1) translation_top_left: vec4<f32>,
    @location(2) size_uv_min_layer: vec4<f32>,
    @location(3) radius: vec2<u32>,
    @location(4) border: vec2<u32>,
    @location(5) clip: vec4<f32>,
    @location(6) color: u32,
}

struct VertexOutput {
    @location(0) uv: vec2<f32>,
    @location(1) color: vec4<f32>,
    @location(2) @interpolate(flat) size: vec2<f32>,
    @location(3) @interpolate(flat) flags: u32,
    @location(4) @interpolate(flat) radius: vec4<f32>,
    @location(5) @interpolate(flat) border: vec4<f32>,
    @location(6) point: vec2<f32>,
    @location(7) @interpolate(flat) clip: vec4<f32>,
    @location(8) world_position: vec2<f32>,
    @location(9) @interpolate(flat) texture_layer: u32,
    @builtin(position) position: vec4<f32>,
};

@vertex
fn vertex(primitive: VertexInput) -> VertexOutput {
    let corners = array<vec2<f32>, 6>(
        vec2(0.0, 0.0),
        vec2(1.0, 0.0),
        vec2(1.0, 1.0),
        vec2(0.0, 0.0),
        vec2(1.0, 1.0),
        vec2(0.0, 1.0),
    );
    let corner = corners[primitive.vertex_index];
    let size = primitive.size_uv_min_layer.xy;
    let local_position = primitive.translation_top_left.zw + corner * size;
    let transform = mat2x2<f32>(
        primitive.transform.xy,
        primitive.transform.zw,
    );
    let world_position = transform * local_position + primitive.translation_top_left.xy;
    var out: VertexOutput;
    let texture_layer = u32(primitive.size_uv_min_layer.z);
    let uv_min = vec2(fract(primitive.size_uv_min_layer.z), primitive.size_uv_min_layer.w);
    out.uv = uv_min + corner * size / ATLAS_SIZE;
    out.position = view.clip_from_world * vec4(world_position, 0.0, 1.0);
    out.color = unpack4x8unorm(primitive.color);
    let radius_top = unpack2x16float(primitive.radius.x);
    let radius_bottom = unpack2x16float(primitive.radius.y);
    out.radius = vec4(radius_top, radius_bottom);
    out.size = size;
    let border_left_top = unpack2x16float(primitive.border.x);
    let border_right_bottom = unpack2x16float(primitive.border.y);
    out.border = vec4(border_left_top, border_right_bottom);
    var flags = 0u;
    if out.border.x > 0.0 {
        flags |= BORDER_LEFT;
    }
    if out.border.y > 0.0 {
        flags |= BORDER_TOP;
    }
    if out.border.z > 0.0 {
        flags |= BORDER_RIGHT;
    }
    if out.border.w > 0.0 {
        flags |= BORDER_BOTTOM;
    }
    out.flags = flags;
    out.point = (corner - vec2(0.5)) * size;
    out.clip = primitive.clip;
    out.world_position = world_position;
    out.texture_layer = texture_layer;
    return out;
}
