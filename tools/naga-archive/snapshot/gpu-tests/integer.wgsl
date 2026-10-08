@group(0) @binding(0) var<storage, read> inputs: array<vec4u>;
@group(0) @binding(1) var<storage, read_write> outputs: array<u32>;
@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) id: vec3u) {
    if (id.x >= 257u) { return; }
    let data = inputs[id.x]; let v = data.x; let arg = data.y; let s = bitcast<i32>(v); let d = bitcast<i32>(arg);
    let base = id.x * 15u;
    outputs[base] = countLeadingZeros(v);
    outputs[base + 1u] = countTrailingZeros(v);
    outputs[base + 2u] = firstLeadingBit(v);
    outputs[base + 3u] = firstTrailingBit(v);
    outputs[base + 4u] = countOneBits(v);
    outputs[base + 5u] = reverseBits(v);
    outputs[base + 6u] = extractBits(v, data.z, data.w);
    outputs[base + 7u] = insertBits(v, arg, data.z, data.w);
    outputs[base + 8u] = bitcast<u32>(extractBits(s, data.z, data.w));
    outputs[base + 9u] = bitcast<u32>(s / d);
    outputs[base + 10u] = bitcast<u32>(s % d);
    outputs[base + 11u] = v / arg;
    outputs[base + 12u] = v % arg;
    outputs[base + 13u] = bitcast<u32>(firstLeadingBit(s));
    outputs[base + 14u] = arg;
}
