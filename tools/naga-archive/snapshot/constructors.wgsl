struct Foo {
    a: vec4<f32>,
    b: i32,
}

const const1: vec3<f32> = vec3<f32>(0f, 0f, 0f);
const const2 = vec3(0.0, 1.0, 2.0);
const const3: mat2x2<f32> = mat2x2<f32>(vec2<f32>(0f, 1f), vec2<f32>(2f, 3f));
const const4: array<mat2x2<f32>, 1> = array<mat2x2<f32>, 1>(mat2x2<f32>(vec2<f32>(0f, 1f), vec2<f32>(2f, 3f)));
const cz0: bool = false;
const cz1: i32 = 0i;
const cz2: u32 = 0u;
const cz3: f32 = 0f;
const cz4: vec2<u32> = vec2<u32>(0u, 0u);
const cz5: mat2x2<f32> = mat2x2<f32>(vec2<f32>(0f, 0f), vec2<f32>(0f, 0f));
const cz6: array<Foo, 3> = array<Foo, 3>(Foo(vec4<f32>(0f, 0f, 0f, 0f), 0i), Foo(vec4<f32>(0f, 0f, 0f, 0f), 0i), Foo(vec4<f32>(0f, 0f, 0f, 0f), 0i));
const cz7: Foo = Foo(vec4<f32>(0f, 0f, 0f, 0f), 0i);
const cp1: vec2<u32> = vec2<u32>(0u, 0u);
const cp2 = mat2x2(vec2(0.0, 0.0), vec2(0.0, 0.0));
const cp3 = array(0, 1, 2, 3);

@compute
@workgroup_size(1u, 1u, 1u)
fn main() {
    var foo: Foo;
    foo = Foo(vec4<f32>(1f), 1i);
    let m0: mat2x2<f32> = mat2x2<f32>(vec2<f32>(1f, 0f), vec2<f32>(0f, 1f));
    let m1: mat4x4<f32> = mat4x4<f32>(vec4<f32>(1f, 0f, 0f, 0f), vec4<f32>(0f, 1f, 0f, 0f), vec4<f32>(0f, 0f, 1f, 0f), vec4<f32>(0f, 0f, 0f, 1f));
    let zvc0: bool = bool();
    let zvc1: i32 = i32();
    let zvc2: u32 = u32();
    let zvc3: f32 = f32();
    let zvc4: vec2<u32> = vec2<u32>();
    let zvc5: mat2x2<f32> = mat2x2<f32>();
    let zvc6: array<Foo, 3> = array<Foo, 3>();
    let zvc7: Foo = Foo();
    let zvc8: vec2<u32> = vec2<u32>(0u, 0u);
    let zvc9: vec2<f32> = vec2<f32>(0f, 0f);
    let cit0: vec2<u32> = vec2<u32>(0u);
    let cit1: mat2x2<f32> = mat2x2<f32>(vec2<f32>(0f, 0f), vec2<f32>(0f, 0f));
    let cit2: array<i32, 4> = array<i32, 4>(0i, 1i, 2i, 3i);
    let ic0: bool = bool(bool());
    let ic1: i32 = i32(i32());
    let ic2: u32 = u32(u32());
    let ic3: f32 = f32(f32());
    let ic4: vec2<u32> = vec2<u32>(vec2<u32>());
    let ic5: mat2x3<f32> = mat2x3<f32>(mat2x3<f32>());
    let ic6: vec2<u32> = vec2<u32>(vec2<u32>());
    let ic7: mat2x3<f32> = mat2x3<f32>(mat2x3<f32>());
    let cc00: i32 = i32(1u);
    let cc01: i32 = i32(1f);
    let cc02: i32 = i32(1);
    let cc03: i32 = i32(1.0);
    let cc04: i32 = i32(true);
    let cc05: u32 = u32(1i);
    let cc06: u32 = u32(1f);
    let cc07: u32 = u32(1);
    let cc08: u32 = u32(1.0);
    let cc09: u32 = u32(true);
    let cc10: f32 = f32(1i);
    let cc11: f32 = f32(1u);
    let cc12: f32 = f32(1);
    let cc13: f32 = f32(1.0);
    let cc14: f32 = f32(true);
    let cc15: bool = bool(1i);
    let cc16: bool = bool(1u);
    let cc17: bool = bool(1f);
    let cc18: bool = bool(1);
    let cc19: bool = bool(1.0);
}
