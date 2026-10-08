
@compute
@workgroup_size(1u, 1u, 1u)
fn main() {
    let sia_naga_temp0: f32 = select(1f, 2f, false);
    var x0: vec2<i32> = vec2<i32>(1i, 2i);
    let sia_naga_temp1: i32 = x0[0u];
    let sia_naga_temp2: i32 = x0[1u];
    var i1: vec2<f32> = select(vec2<f32>(1f, 0f), vec2<f32>(0f, 1f), (sia_naga_temp1 < sia_naga_temp2));
}
