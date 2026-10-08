enable wgpu_binding_array;
struct n_Inner_5 {
    m0: u32,
}

struct n_Foo_3 {
    m0: u32,
    m1: n_Inner_5,
    m2: array<i32>,
}

struct n_PlainData_10 {
    m0: array<u32>,
}

struct n_UniformIndex_15 {
    m0: u32,
}

struct n_SpirvBlock_uni_14 {
    m0: n_UniformIndex_15,
}

struct n_FragmentIn_18 {
    m0: u32,
}

struct Output_138 {
    @location(0) n_main_output_141_output: u32,
}

@group(0) @binding(0) var<storage, read> n_storage_array_9: binding_array<n_Foo_3>;
@group(0) @binding(1) var<storage, read> n_plain_storage_13: n_PlainData_10;
@group(0) @binding(10) var<uniform> n_uni_17: n_SpirvBlock_uni_14;
var<private> n_main_output_141: u32;
var<private> n_index_143: u32;

fn n_main_1(n_fragment_in_20: n_FragmentIn_18) -> u32 {
    var n_u1_29: u32;
    var r27: u32;
    var r28: u32;
    var r31: u32;
    var r37: u32;
    var r38: u32;
    var r39: u32;
    var r42: u32;
    var r43: u32;
    var r44: u32;
    var r47: u32;
    var r48: u32;
    var r49: u32;
    var r53: u32;
    var r54: u32;
    var r55: u32;
    var r61: u32;
    var r62: u32;
    var r63: u32;
    var r67: u32;
    var r68: u32;
    var r69: u32;
    var r73: u32;
    var r74: u32;
    var r75: u32;
    var r79: u32;
    var r80: u32;
    var r81: u32;
    var r83: u32;
    var r84: u32;
    var r85: u32;
    var r87: u32;
    var r88: u32;
    var r89: u32;
    var r91: u32;
    var r92: u32;
    var r93: u32;
    var r95: u32;
    var r96: u32;
    var r97: u32;
    var r104: i32;
    var r105: u32;
    var r106: u32;
    var r107: u32;
    var r111: i32;
    var r112: u32;
    var r113: u32;
    var r114: u32;
    var r118: i32;
    var r119: u32;
    var r120: u32;
    var r121: u32;
    var r125: i32;
    var r126: u32;
    var r127: u32;
    var r128: u32;
    var r132: u32;
    var r133: u32;
    var r134: u32;
    var r135: u32;
    var r136: u32;
    var r137: u32;
    r27 = n_uni_17.m0.m0;
    r28 = n_fragment_in_20.m0;
    n_u1_29 = 0u;
    r31 = n_u1_29;
    r37 = n_storage_array_9[0i].m0;
    r38 = (r31 + r37);
    n_u1_29 = r38;
    r39 = n_u1_29;
    r42 = n_storage_array_9[r27].m0;
    r43 = (r39 + r42);
    n_u1_29 = r43;
    r44 = n_u1_29;
    r47 = n_storage_array_9[r28].m0;
    r48 = (r44 + r47);
    n_u1_29 = r48;
    r49 = n_u1_29;
    r53 = n_storage_array_9[7i].m0;
    r54 = (r49 + r53);
    n_u1_29 = r54;
    r55 = n_u1_29;
    r61 = n_storage_array_9[0i].m1.m0;
    r62 = (r55 + r61);
    n_u1_29 = r62;
    r63 = n_u1_29;
    r67 = n_storage_array_9[r27].m1.m0;
    r68 = (r63 + r67);
    n_u1_29 = r68;
    r69 = n_u1_29;
    r73 = n_storage_array_9[r28].m1.m0;
    r74 = (r69 + r73);
    n_u1_29 = r74;
    r75 = n_u1_29;
    r79 = n_storage_array_9[7i].m1.m0;
    r80 = (r75 + r79);
    n_u1_29 = r80;
    r81 = n_u1_29;
    r83 = arrayLength((&n_storage_array_9[0i].m2));
    r84 = (r81 + r83);
    n_u1_29 = r84;
    r85 = n_u1_29;
    r87 = arrayLength((&n_storage_array_9[r27].m2));
    r88 = (r85 + r87);
    n_u1_29 = r88;
    r89 = n_u1_29;
    r91 = arrayLength((&n_storage_array_9[r28].m2));
    r92 = (r89 + r91);
    n_u1_29 = r92;
    r93 = n_u1_29;
    r95 = arrayLength((&n_storage_array_9[7i].m2));
    r96 = (r93 + r95);
    n_u1_29 = r96;
    r97 = n_u1_29;
    r104 = n_storage_array_9[0i].m2[0i];
    r105 = bitcast<u32>(r104);
    r106 = (r97 + r105);
    n_u1_29 = r106;
    r107 = n_u1_29;
    r111 = n_storage_array_9[r27].m2[0i];
    r112 = bitcast<u32>(r111);
    r113 = (r107 + r112);
    n_u1_29 = r113;
    r114 = n_u1_29;
    r118 = n_storage_array_9[r28].m2[0i];
    r119 = bitcast<u32>(r118);
    r120 = (r114 + r119);
    n_u1_29 = r120;
    r121 = n_u1_29;
    r125 = n_storage_array_9[7i].m2[0i];
    r126 = bitcast<u32>(r125);
    r127 = (r121 + r126);
    n_u1_29 = r127;
    r128 = n_u1_29;
    r132 = n_plain_storage_13.m0[0i];
    r133 = (r128 + r132);
    n_u1_29 = r133;
    r134 = n_u1_29;
    r135 = arrayLength((&n_plain_storage_13.m0));
    r136 = (r134 + r135);
    n_u1_29 = r136;
    r137 = n_u1_29;
    return r137;
}

fn naga_fn138() {
    var r145: u32;
    var r146: n_FragmentIn_18;
    var r147: u32;
    r145 = n_index_143;
    r146 = n_FragmentIn_18(r145);
    r147 = n_main_1(r146);
    n_main_output_141 = r147;
    return;
}

@fragment
fn main(@location(0) @interpolate(flat) n_index_143_input: u32) -> Output_138 {
    n_index_143 = n_index_143_input;
    naga_fn138();
    return Output_138(n_main_output_141);
}
