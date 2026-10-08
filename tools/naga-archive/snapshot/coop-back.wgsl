enable wgpu_cooperative_matrix;
struct S21 {
    m0: array<f32>,
}

var<private> g14: coop_mat8x8<f32, A> = coop_mat8x8<f32, A>();
var<private> g17: coop_mat8x8<f32, B> = coop_mat8x8<f32, B>();
@group(0) @binding(0) var<storage, read_write> g20: S21;

fn naga_fn24() {
    var r28: coop_mat8x8<f32, C> = coop_mat8x8<f32, C>();
    var r31: coop_mat8x8<f32, C> = coop_mat8x8<f32, C>();
    var r37: coop_mat8x8<f32, C>;
    var r38: coop_mat8x8<f32, A>;
    var r39: coop_mat8x8<f32, B>;
    var r40: coop_mat8x8<f32, C>;
    var r41: coop_mat8x8<f32, C>;
    var r42: coop_mat8x8<f32, C>;
    var r44: coop_mat8x8<f32, C>;
    r37 = coopLoad<coop_mat8x8<f32, C>>((&g20.m0[4u]), 8u);
    r28 = r37;
    r38 = g14;
    r39 = g17;
    r40 = r28;
    r41 = coopMultiplyAdd(r38, r39, r40);
    r31 = r41;
    r42 = r31;
    coopStore(r42, (&g20.m0[0u]), 8u);
    r44 = r31;
    r28 = r44;
    return;
}

@compute
@workgroup_size(8u, 8u, 1u)
fn main() {
    naga_fn24();
}
