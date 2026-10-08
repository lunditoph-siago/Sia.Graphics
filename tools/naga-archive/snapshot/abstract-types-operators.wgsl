const plus_fafaf: f32 = 3f;
const plus_fafai: f32 = 3f;
const plus_faf_f: f32 = 3f;
const plus_faiaf: f32 = 3f;
const plus_faiai: f32 = 3f;
const plus_fai_f: f32 = 3f;
const plus_f_faf: f32 = 3f;
const plus_f_fai: f32 = 3f;
const plus_f_f_f: f32 = 3f;
const plus_iaiai: i32 = 3i;
const plus_iai_i: i32 = 3i;
const plus_i_iai: i32 = 3i;
const plus_i_i_i: i32 = 3i;
const plus_uaiai: u32 = 3u;
const plus_uai_u: u32 = 3u;
const plus_u_uai: u32 = 3u;
const plus_u_u_u: u32 = 3u;
const bitflip_u_u: u32 = 0u;
const bitflip_uai: u32 = 0u;
const least_i32: i32 = (-2147483647i - 1i);
const least_f32: f32 = -3.4028235E+38f;
const shl_iaiai: i32 = 4i;
const shl_iai_u: i32 = 4i;
const shl_uaiai: u32 = 4u;
const shl_uai_u: u32 = 4u;
const shlaiaiai = 4;
const shlaiai_u = 4;
const shr_iaiai: i32 = 0i;
const shr_iai_u: i32 = 0i;
const shr_uaiai: u32 = 0u;
const shr_uai_u: u32 = 0u;
const shraiaiai = 0;
const shraiai_u = 0;
const wgpu_4492: i32 = (-2147483647i - 1i);
const wgpu_4492_2 = -2147483648;
var<workgroup> a: array<u32, 64>;

fn runtime_values() {
    var f: f32 = 42f;
    var i: i32 = 43i;
    var u: u32 = 44u;
    var plus_fafaf: f32 = 3f;
    var plus_fafai: f32 = 3f;
    let sia_naga_temp0: f32 = f;
    var plus_faf_f: f32 = (1f + sia_naga_temp0);
    var plus_faiaf: f32 = 3f;
    var plus_faiai: f32 = 3f;
    let sia_naga_temp1: f32 = f;
    var plus_fai_f: f32 = (1f + sia_naga_temp1);
    let sia_naga_temp2: f32 = f;
    var plus_f_faf: f32 = (sia_naga_temp2 + 2f);
    let sia_naga_temp3: f32 = f;
    var plus_f_fai: f32 = (sia_naga_temp3 + 2f);
    let sia_naga_temp4: f32 = f;
    let sia_naga_temp5: f32 = f;
    var plus_f_f_f: f32 = (sia_naga_temp4 + sia_naga_temp5);
    var plus_iaiai: i32 = 3i;
    let sia_naga_temp6: i32 = i;
    var plus_iai_i: i32 = (1i + sia_naga_temp6);
    let sia_naga_temp7: i32 = i;
    var plus_i_iai: i32 = (sia_naga_temp7 + 2i);
    let sia_naga_temp8: i32 = i;
    let sia_naga_temp9: i32 = i;
    var plus_i_i_i: i32 = (sia_naga_temp8 + sia_naga_temp9);
    var plus_uaiai: u32 = 3u;
    let sia_naga_temp10: u32 = u;
    var plus_uai_u: u32 = (1u + sia_naga_temp10);
    let sia_naga_temp11: u32 = u;
    var plus_u_uai: u32 = (sia_naga_temp11 + 2u);
    let sia_naga_temp12: u32 = u;
    let sia_naga_temp13: u32 = u;
    var plus_u_u_u: u32 = (sia_naga_temp12 + sia_naga_temp13);
    let sia_naga_temp14: u32 = u;
    var shl_iai_u: i32 = (1i << sia_naga_temp14);
    let sia_naga_temp15: u32 = u;
    var shr_iai_u: i32 = (1i << sia_naga_temp15);
}

fn wgpu_4445() {
    let a: f32 = 5f;
    let b: f32 = 7f;
    let c: f32 = 5f;
}

fn wgpu_4435() {
    let x: i32 = 1i;
    let sia_naga_temp16: u32 = a[(x - 1i)];
    let y: u32 = sia_naga_temp16;
}

@compute
@workgroup_size(1u, 1u, 1u)
fn main() {
    runtime_values();
    wgpu_4445();
    wgpu_4435();
}
