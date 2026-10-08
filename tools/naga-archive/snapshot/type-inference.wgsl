const g0 = 1;
const g1: u32 = 1u;
const g2 = 1.0;
const g3: f32 = 1f;
const g4: vec4<i32> = vec4<i32>(0i, 0i, 0i, 0i);
const g5: vec4<i32> = vec4<i32>(1i, 1i, 1i, 1i);
const g6: mat2x2<f32> = mat2x2<f32>(vec2<f32>(0f, 0f), vec2<f32>(0f, 0f));
const g7 = mat2x2(vec2(1.0, 1.0), vec2(1.0, 1.0));

@compute
@workgroup_size(1u, 1u, 1u)
fn main() {
    var g0x: i32 = 1i;
    var g2x: f32 = 1f;
    var g7x: mat2x2<f32> = mat2x2<f32>(vec2<f32>(1f, 1f), vec2<f32>(1f, 1f));
    var c0x: i32 = 1i;
    var c1x: u32 = 1u;
    var c2x: f32 = 1f;
    var c3x: f32 = 1f;
    var c4x: vec4<i32> = vec4<i32>(0i, 0i, 0i, 0i);
    var c5x: vec4<i32> = vec4<i32>(1i, 1i, 1i, 1i);
    var c6x: mat2x2<f32> = mat2x2<f32>(vec2<f32>(0f, 0f), vec2<f32>(0f, 0f));
    var c7x: mat2x2<f32> = mat2x2<f32>(vec2<f32>(1f, 1f), vec2<f32>(1f, 1f));
    let l0: i32 = 1i;
    let l1: u32 = 1u;
    let l2: f32 = 1f;
    let l3: f32 = 1f;
    let l4: vec4<i32> = vec4<i32>();
    let l5: vec4<i32> = vec4<i32>(1i);
    let l6: mat2x2<f32> = mat2x2<f32>(vec2<f32>(0f, 0f), vec2<f32>(0f, 0f));
    let l7: mat2x2<f32> = mat2x2<f32>(vec2<f32>(1f, 1f), vec2<f32>(1f, 1f));
    var l0x: i32 = l0;
    var l1x: u32 = l1;
    var l2x: f32 = l2;
    var l3x: f32 = l3;
    var l4x: vec4<i32> = l4;
    var v0: i32 = 1i;
    var v1: u32 = 1u;
    var v2: f32 = 1f;
    var v3: f32 = 1f;
    var v4: vec4<i32> = vec4<i32>();
    var v5: vec4<i32> = vec4<i32>(1i);
    var v6: mat2x2<f32> = mat2x2<f32>(vec2<f32>(0f, 0f), vec2<f32>(0f, 0f));
    var v7: mat2x2<f32> = mat2x2<f32>(vec2<f32>(1f, 1f), vec2<f32>(1f, 1f));
}
