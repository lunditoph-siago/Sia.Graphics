#define_import_path sia::ui_node

const BORDER_LEFT: u32 = 1u;
const BORDER_TOP: u32 = 2u;
const BORDER_RIGHT: u32 = 4u;
const BORDER_BOTTOM: u32 = 8u;
const BORDER_ANY: u32 = BORDER_LEFT + BORDER_TOP + BORDER_RIGHT + BORDER_BOTTOM;

fn enabled(flags: u32, mask: u32) -> bool {
    return (flags & mask) != 0u;
}

struct ViewUniform {
    clip_from_world: mat4x4<f32>,
}

@group(0) @binding(0) var<uniform> view: ViewUniform;

struct UiPrimitive {
    transform: vec4<f32>,
    translation_top_left: vec4<f32>,
    size_uv_min: vec4<f32>,
    uv_max_color_rg: vec4<f32>,
    color_ba_radius_tl_tr: vec4<f32>,
    radius_br_bl_border_lt: vec4<f32>,
    border_rb_clip_min: vec4<f32>,
    clip_max_flags_layer: vec4<f32>,
}

@group(0) @binding(1) var<storage, read> primitives: array<UiPrimitive>;
@group(0) @binding(2) var sprite_texture: texture_2d_array<f32>;
@group(0) @binding(3) var sprite_sampler: sampler;

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
fn vertex(
    @builtin(vertex_index) vertex_index: u32,
    @builtin(instance_index) instance_index: u32,
) -> VertexOutput {
    let primitive = primitives[instance_index];
    let corners = array<vec2<f32>, 6>(
        vec2(0.0, 0.0),
        vec2(1.0, 0.0),
        vec2(1.0, 1.0),
        vec2(0.0, 0.0),
        vec2(1.0, 1.0),
        vec2(0.0, 1.0),
    );
    let corner = corners[vertex_index];
    let size = primitive.size_uv_min.xy;
    let local_position = primitive.translation_top_left.zw + corner * size;
    let transform = mat2x2<f32>(
        primitive.transform.xy,
        primitive.transform.zw,
    );
    let world_position = transform * local_position + primitive.translation_top_left.xy;
    var out: VertexOutput;
    out.uv = mix(primitive.size_uv_min.zw, primitive.uv_max_color_rg.xy, corner);
    out.position = view.clip_from_world * vec4(world_position, 0.0, 1.0);
    out.color = vec4(primitive.uv_max_color_rg.zw, primitive.color_ba_radius_tl_tr.xy);
    out.flags = bitcast<u32>(primitive.clip_max_flags_layer.z);
    out.radius = vec4(
        primitive.color_ba_radius_tl_tr.zw,
        primitive.radius_br_bl_border_lt.xy,
    );
    out.size = size;
    out.border = vec4(
        primitive.radius_br_bl_border_lt.zw,
        primitive.border_rb_clip_min.xy,
    );
    out.point = (corner - vec2(0.5)) * size;
    out.clip = vec4(primitive.border_rb_clip_min.zw, primitive.clip_max_flags_layer.xy);
    out.world_position = world_position;
    out.texture_layer = bitcast<u32>(primitive.clip_max_flags_layer.w);
    return out;
}

// Returns the radius of the corner closest to the given point.
fn select_corner_radius(
    point: vec2<f32>,
    corner_radii_x: vec4<f32>,
    corner_radii_y: vec4<f32>,
) -> vec2<f32> {
    let rxs = select(corner_radii_x.xy, corner_radii_x.wz, 0.0 < point.y);
    let rys = select(corner_radii_y.xy, corner_radii_y.wz, 0.0 < point.y);
    return vec2(select(rxs.x, rxs.y, 0.0 < point.x), select(rys.x, rys.y, 0.0 < point.x));
}

// One iteration of Newton's method approximating the distance from a point
// to an ellipse (see Taubin, "Distance Approximations for Rasterizing
// Implicit Curves", section 3).
fn distance_to_ellipse_approx(p: vec2<f32>, inv_radii_sq: vec2<f32>, scale: f32) -> f32 {
    let p_r = p * inv_radii_sq;
    let g = dot(p, p_r) - scale;
    let dG = (1.0 + scale) * p_r;
    return g * inverseSqrt(dot(dG, dG));
}

// Shortest signed distance from `point` to the boundary of a rounded box:
// negative inside, positive outside, zero on the boundary.
fn sd_rounded_box(
    point: vec2<f32>,
    size: vec2<f32>,
    corner_radii_x: vec4<f32>,
    corner_radii_y: vec4<f32>,
) -> f32 {
    let radius = select_corner_radius(point, corner_radii_x, corner_radii_y);
    let corner_to_point = abs(point) - 0.5 * size;
    let straight_distance = max(corner_to_point.x, corner_to_point.y);
    if min(radius.x, radius.y) <= 0.0 {
        return straight_distance;
    }
    let q = corner_to_point + radius;
    let edge_distance = max(q.x - radius.x, q.y - radius.y);
    let inv_radii_sq = 1.0 / (radius * radius);
    let corner_distance = distance_to_ellipse_approx(q, inv_radii_sq, 1.0);
    return select(edge_distance, corner_distance, q.x > 0.0 && q.y > 0.0);
}

fn sd_inset_rounded_box(
    point: vec2<f32>,
    size: vec2<f32>,
    radius_x: vec4<f32>,
    radius_y: vec4<f32>,
    inset: vec4<f32>,
) -> f32 {
    let inner_size = size - inset.xy - inset.zw;
    let inner_center = inset.xy + 0.5 * inner_size - 0.5 * size;
    let inner_point = point - inner_center;

    var rx = radius_x;
    var ry = radius_y;

    rx.x = rx.x - inset.x;
    ry.x = ry.x - inset.y;
    rx.y = rx.y - inset.z;
    ry.y = ry.y - inset.y;
    rx.z = rx.z - inset.z;
    ry.z = ry.z - inset.w;
    rx.w = rx.w - inset.x;
    ry.w = ry.w - inset.w;

    let half_size = inner_size * 0.5;
    rx = min(max(rx, vec4(0.0)), vec4<f32>(half_size.x));
    ry = min(max(ry, vec4(0.0)), vec4<f32>(half_size.y));
    let is_zero_radius = min(rx, ry) <= vec4(0.0);
    rx = select(rx, vec4(0.0), is_zero_radius);
    ry = select(ry, vec4(0.0), is_zero_radius);

    return sd_rounded_box(inner_point, inner_size, rx, ry);
}

fn nearest_border_active(point_vs_mid: vec2<f32>, size: vec2<f32>, width: vec4<f32>, flags: u32) -> bool {
    if (flags & BORDER_ANY) == BORDER_ANY {
        return true;
    }
    let point = clamp(point_vs_mid + size * 0.49999, vec2(0.0), size);
    let left = point.x / width.x;
    let top = point.y / width.y;
    let right = (size.x - point.x) / width.z;
    let bottom = (size.y - point.y) / width.w;
    let min_dist = min(min(left, top), min(right, bottom));
    return (enabled(flags, BORDER_LEFT) && min_dist == left) ||
        (enabled(flags, BORDER_TOP) && min_dist == top) ||
        (enabled(flags, BORDER_RIGHT) && min_dist == right) ||
        (enabled(flags, BORDER_BOTTOM) && min_dist == bottom);
}

fn antialias(distance: f32) -> f32 {
    return saturate(0.5 - distance);
}

fn draw_uinode_border(
    color: vec4<f32>,
    point: vec2<f32>,
    size: vec2<f32>,
    radius_x: vec4<f32>,
    radius_y: vec4<f32>,
    border: vec4<f32>,
    flags: u32,
) -> vec4<f32> {
    let external_distance = sd_rounded_box(point, size, radius_x, radius_y);
    let internal_distance = sd_inset_rounded_box(point, size, radius_x, radius_y, border);
    let border_distance = max(external_distance, -internal_distance);
    let nearest_border = select(0.0, 1.0, nearest_border_active(point, size, border, flags));
    let t = select(1.0 - step(0.0, border_distance), antialias(border_distance), external_distance < internal_distance);
    return vec4(color.rgb, saturate(color.a * t * nearest_border));
}

fn draw_uinode_background(
    color: vec4<f32>,
    point: vec2<f32>,
    size: vec2<f32>,
    radius_x: vec4<f32>,
    radius_y: vec4<f32>,
    border: vec4<f32>,
    flags: u32,
) -> vec4<f32> {
    let internal_distance = sd_inset_rounded_box(point, size, radius_x, radius_y, border);
    let t = antialias(internal_distance);
    return vec4(color.rgb, saturate(color.a * t));
}

@fragment
fn fragment(in: VertexOutput) -> @location(0) vec4<f32> {
    if any(in.world_position < in.clip.xy) || any(in.world_position > in.clip.zw) {
        discard;
    }
    var color = in.color;
    if in.texture_layer != 0u {
        color *= textureSampleLevel(sprite_texture, sprite_sampler, in.uv, i32(in.texture_layer), 0.0);
    }

    if enabled(in.flags, BORDER_ANY) {
        return draw_uinode_border(color, in.point, in.size, in.radius, in.radius, in.border, in.flags);
    } else {
        return draw_uinode_background(color, in.point, in.size, in.radius, in.radius, in.border, in.flags);
    }
}
