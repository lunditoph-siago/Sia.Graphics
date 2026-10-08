const const_af = 0;
const const_ai = 0;
const const_vec_af = vec2(0.0, 0.0);
const const_vec_ai = vec2(0, 0);
const const_mat_af = mat2x2(vec2(0.0, 0.0), vec2(0.0, 0.0));
const const_arr_af = array(0.0, 0.0);
const const_arr_ai = array(0, 0);

fn func_f(a: f32) {
}

fn func_i(a: i32) {
}

fn func_u(a: u32) {
}

fn func_vf(a: vec2<f32>) {
}

fn func_vi(a: vec2<i32>) {
}

fn func_vu(a: vec2<u32>) {
}

fn func_mf(a: mat2x2<f32>) {
}

fn func_af(a: array<f32, 2>) {
}

fn func_ai(a: array<i32, 2>) {
}

fn func_au(a: array<u32, 2>) {
}

fn func_f_i(a: f32, b: i32) {
}

@compute
@workgroup_size(1u, 1u, 1u)
fn main() {
    func_f(0f);
    func_f(0f);
    func_i(0i);
    func_u(0u);
    func_f(0f);
    func_f(0f);
    func_i(0i);
    func_u(0u);
    func_vf(vec2<f32>(0f, 0f));
    func_vf(vec2<f32>(0f, 0f));
    func_vi(vec2<i32>(0i, 0i));
    func_vu(vec2<u32>(0u, 0u));
    func_vf(vec2<f32>(0f, 0f));
    func_vf(vec2<f32>(0f, 0f));
    func_vi(vec2<i32>(0i, 0i));
    func_vu(vec2<u32>(0u, 0u));
    func_mf(mat2x2<f32>(vec2<f32>(0f, 0f), vec2<f32>(0f, 0f)));
    func_mf(mat2x2<f32>(vec2<f32>(0f, 0f), vec2<f32>(0f, 0f)));
    func_mf(mat2x2<f32>(vec2<f32>(0f, 0f), vec2<f32>(0f, 0f)));
    func_af(array<f32, 2>(0f, 0f));
    func_af(array<f32, 2>(0f, 0f));
    func_ai(array<i32, 2>(0i, 0i));
    func_au(array<u32, 2>(0u, 0u));
    func_af(array<f32, 2>(0f, 0f));
    func_af(array<f32, 2>(0f, 0f));
    func_ai(array<i32, 2>(0i, 0i));
    func_au(array<u32, 2>(0u, 0u));
    func_f_i(0f, 0i);
    func_f_i(0f, 0i);
    func_f_i(0f, 0i);
    func_f_i(0f, 0i);
}
