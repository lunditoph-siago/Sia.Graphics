enable f16;
@group(0) @binding(0) var<storage, read> inputs: array<vec4u>;
@group(0) @binding(1) var<storage, read_write> outputs: array<u32>;
@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) id: vec3u) {
    if (id.x >= 257u) { return; }
    let packed = inputs[id.x].x;
    let value = bitcast<vec2<f16>>(packed);
    let base = id.x * 15u;
    outputs[base] = bitcast<u32>(value);
    outputs[base + 1u] = bitcast<u32>(f32(value.x));
    outputs[base + 2u] = bitcast<u32>(f32(value.y));
    for (var i = 3u; i < 15u; i++) { outputs[base + i] = 0u; }
}
