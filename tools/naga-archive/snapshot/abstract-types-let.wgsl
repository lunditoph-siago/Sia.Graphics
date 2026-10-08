
fn all_constant_arguments() {
    let xvipaiai: vec2<i32> = vec2<i32>(42i, 43i);
    let xvupaiai: vec2<u32> = vec2<u32>(44u, 45u);
    let xvfpaiai: vec2<f32> = vec2<f32>(46f, 47f);
    let xvfpafaf: vec2<f32> = vec2<f32>(48f, 49f);
    let xvfpaiaf: vec2<f32> = vec2<f32>(48f, 49f);
    let xvupuai: vec2<u32> = vec2<u32>(42u, 43u);
    let xvupaiu: vec2<u32> = vec2<u32>(42u, 43u);
    let xvuuai: vec2<u32> = vec2<u32>(42u, 43u);
    let xvuaiu: vec2<u32> = vec2<u32>(42u, 43u);
    let xvip____: vec2<i32> = vec2<i32>(0i, 0i);
    let xvup____: vec2<u32> = vec2<u32>(0u, 0u);
    let xvfp____: vec2<f32> = vec2<f32>(0f, 0f);
    let xmfp____: mat2x2<f32> = mat2x2<f32>(vec2<f32>(0f, 0f), vec2<f32>(0f, 0f));
    let xmfpaiaiaiai: mat2x2<f32> = mat2x2<f32>(vec2<f32>(1f, 2f), vec2<f32>(3f, 4f));
    let xmfpafaiaiai: mat2x2<f32> = mat2x2<f32>(vec2<f32>(1f, 2f), vec2<f32>(3f, 4f));
    let xmfpaiafaiai: mat2x2<f32> = mat2x2<f32>(vec2<f32>(1f, 2f), vec2<f32>(3f, 4f));
    let xmfpaiaiafai: mat2x2<f32> = mat2x2<f32>(vec2<f32>(1f, 2f), vec2<f32>(3f, 4f));
    let xmfpaiaiaiaf: mat2x2<f32> = mat2x2<f32>(vec2<f32>(1f, 2f), vec2<f32>(3f, 4f));
    let xmfp_faiaiai: mat2x2<f32> = mat2x2<f32>(vec2<f32>(1f, 2f), vec2<f32>(3f, 4f));
    let xmfpai_faiai: mat2x2<f32> = mat2x2<f32>(vec2<f32>(1f, 2f), vec2<f32>(3f, 4f));
    let xmfpaiai_fai: mat2x2<f32> = mat2x2<f32>(vec2<f32>(1f, 2f), vec2<f32>(3f, 4f));
    let xmfpaiaiai_f: mat2x2<f32> = mat2x2<f32>(vec2<f32>(1f, 2f), vec2<f32>(3f, 4f));
    let xvispai: vec2<i32> = vec2<i32>(1i, 1i);
    let xvfspaf: vec2<f32> = vec2<f32>(1f, 1f);
    let xvis_ai: vec2<i32> = vec2<i32>(1i);
    let xvus_ai: vec2<u32> = vec2<u32>(1u);
    let xvfs_ai: vec2<f32> = vec2<f32>(1f);
    let xvfs_af: vec2<f32> = vec2<f32>(1f);
    let xafafaf: array<f32, 2> = array<f32, 2>(1f, 2f);
    let xaf_faf: array<f32, 2> = array<f32, 2>(1f, 2f);
    let xafaf_f: array<f32, 2> = array<f32, 2>(1f, 2f);
    let xafaiai: array<f32, 2> = array<f32, 2>(1f, 2f);
    let xai_iai: array<i32, 2> = array<i32, 2>(1i, 2i);
    let xaiai_i: array<i32, 2> = array<i32, 2>(1i, 2i);
    let xaipaiai: array<i32, 2> = array<i32, 2>(1i, 2i);
    let xafpaiai: array<f32, 2> = array<f32, 2>(1f, 2f);
    let xafpaiaf: array<f32, 2> = array<f32, 2>(1f, 2f);
    let xafpafai: array<f32, 2> = array<f32, 2>(1f, 2f);
    let xafpafaf: array<f32, 2> = array<f32, 2>(1f, 2f);
    let xavipai: array<vec3<i32>, 1> = array<vec3<i32>, 1>(vec3<i32>(1i, 1i, 1i));
    let xavfpai: array<vec3<f32>, 1> = array<vec3<f32>, 1>(vec3<f32>(1f, 1f, 1f));
    let xavfpaf: array<vec3<f32>, 1> = array<vec3<f32>, 1>(vec3<f32>(1f, 1f, 1f));
    let xvisai: vec2<i32> = vec2<i32>(1i, 1i);
    let xvusai: vec2<u32> = vec2<u32>(1u, 1u);
    let xvfsai: vec2<f32> = vec2<f32>(1f, 1f);
    let xvfsaf: vec2<f32> = vec2<f32>(1f, 1f);
    let iaipaiai: array<i32, 2> = array<i32, 2>(1i, 2i);
    let iafpaiaf: array<f32, 2> = array<f32, 2>(1f, 2f);
    let iafpafai: array<f32, 2> = array<f32, 2>(1f, 2f);
    let iafpafaf: array<f32, 2> = array<f32, 2>(1f, 2f);
}

fn mixed_constant_and_runtime_arguments() {
    var u: u32;
    var i: i32;
    var f: f32;
    let sia_naga_temp0: u32 = u;
    let xvupuai: vec2<u32> = vec2<u32>(sia_naga_temp0, 43u);
    let sia_naga_temp1: u32 = u;
    let xvupaiu: vec2<u32> = vec2<u32>(42u, sia_naga_temp1);
    let sia_naga_temp2: f32 = f;
    let xvfpfai: vec2<f32> = vec2<f32>(sia_naga_temp2, 47f);
    let sia_naga_temp3: f32 = f;
    let xvfpfaf: vec2<f32> = vec2<f32>(sia_naga_temp3, 49f);
    let sia_naga_temp4: u32 = u;
    let xvuuai: vec2<u32> = vec2<u32>(sia_naga_temp4, 43u);
    let sia_naga_temp5: u32 = u;
    let xvuaiu: vec2<u32> = vec2<u32>(42u, sia_naga_temp5);
    let sia_naga_temp6: f32 = f;
    let xmfp_faiaiai: mat2x2<f32> = mat2x2<f32>(vec2<f32>(sia_naga_temp6, 2f), vec2<f32>(3f, 4f));
    let sia_naga_temp7: f32 = f;
    let xmfpai_faiai: mat2x2<f32> = mat2x2<f32>(vec2<f32>(1f, sia_naga_temp7), vec2<f32>(3f, 4f));
    let sia_naga_temp8: f32 = f;
    let xmfpaiai_fai: mat2x2<f32> = mat2x2<f32>(vec2<f32>(1f, 2f), vec2<f32>(sia_naga_temp8, 4f));
    let sia_naga_temp9: f32 = f;
    let xmfpaiaiai_f: mat2x2<f32> = mat2x2<f32>(vec2<f32>(1f, 2f), vec2<f32>(3f, sia_naga_temp9));
    let sia_naga_temp10: f32 = f;
    let xaf_faf: array<f32, 2> = array<f32, 2>(sia_naga_temp10, 2f);
    let sia_naga_temp11: f32 = f;
    let xafaf_f: array<f32, 2> = array<f32, 2>(1f, sia_naga_temp11);
    let sia_naga_temp12: f32 = f;
    let xaf_fai: array<f32, 2> = array<f32, 2>(sia_naga_temp12, 2f);
    let sia_naga_temp13: f32 = f;
    let xafai_f: array<f32, 2> = array<f32, 2>(1f, sia_naga_temp13);
    let sia_naga_temp14: i32 = i;
    let xai_iai: array<i32, 2> = array<i32, 2>(sia_naga_temp14, 2i);
    let sia_naga_temp15: i32 = i;
    let xaiai_i: array<i32, 2> = array<i32, 2>(1i, sia_naga_temp15);
    let sia_naga_temp16: f32 = f;
    let xafp_faf: array<f32, 2> = array<f32, 2>(sia_naga_temp16, 2f);
    let sia_naga_temp17: f32 = f;
    let xafpaf_f: array<f32, 2> = array<f32, 2>(1f, sia_naga_temp17);
    let sia_naga_temp18: f32 = f;
    let xafp_fai: array<f32, 2> = array<f32, 2>(sia_naga_temp18, 2f);
    let sia_naga_temp19: f32 = f;
    let xafpai_f: array<f32, 2> = array<f32, 2>(1f, sia_naga_temp19);
    let sia_naga_temp20: i32 = i;
    let xaip_iai: array<i32, 2> = array<i32, 2>(sia_naga_temp20, 2i);
    let sia_naga_temp21: i32 = i;
    let xaipai_i: array<i32, 2> = array<i32, 2>(1i, sia_naga_temp21);
    let sia_naga_temp22: i32 = i;
    let xvisi: vec2<i32> = vec2<i32>(sia_naga_temp22);
    let sia_naga_temp23: u32 = u;
    let xvusu: vec2<u32> = vec2<u32>(sia_naga_temp23);
    let sia_naga_temp24: f32 = f;
    let xvfsf: vec2<f32> = vec2<f32>(sia_naga_temp24);
}

@compute
@workgroup_size(1u, 1u, 1u)
fn main() {
    all_constant_arguments();
    mixed_constant_and_runtime_arguments();
}
