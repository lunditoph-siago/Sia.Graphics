struct S9 {
    m0: array<vec4<u32>>,
}

struct S12 {
    m0: array<u32>,
}

@group(0) @binding(0) var<storage, read> g8: S9;
@group(0) @binding(1) var<storage, read_write> g11: S12;
var<private> g63: vec3<u32>;

fn naga_fn15(a17: i32, a18: i32) -> i32 {
    var r22: bool;
    var r25: bool;
    var r26: bool;
    var r27: bool;
    var r28: bool;
    var r30: i32;
    var r31: i32;
    r22 = (a18 == 0i);
    r25 = (a17 == (-2147483647i - 1i));
    r26 = (a18 == -1i);
    r27 = (r25 && r26);
    r28 = (r22 || r27);
    r30 = select(a18, 1i, r28);
    r31 = (a17 / r30);
    return r31;
}

fn naga_fn32(a33: i32, a34: i32) -> i32 {
    var r36: bool;
    var r37: bool;
    var r38: bool;
    var r39: bool;
    var r40: bool;
    var r41: i32;
    var r42: i32;
    var r43: i32;
    var r44: i32;
    r36 = (a34 == 0i);
    r37 = (a33 == (-2147483647i - 1i));
    r38 = (a34 == -1i);
    r39 = (r37 && r38);
    r40 = (r36 || r39);
    r41 = select(a34, 1i, r40);
    r42 = (a33 / r41);
    r43 = (r42 * r41);
    r44 = (a33 - r43);
    return r44;
}

fn naga_fn45(a47: u32, a48: u32) -> u32 {
    var r51: bool;
    var r53: u32;
    var r54: u32;
    r51 = (a48 == 0u);
    r53 = select(a48, 1u, r51);
    r54 = (a47 / r53);
    return r54;
}

fn naga_fn55(a56: u32, a57: u32) -> u32 {
    var r59: bool;
    var r60: u32;
    var r61: u32;
    r59 = (a57 == 0u);
    r60 = select(a57, 1u, r59);
    r61 = (a56 % r60);
    return r61;
}

fn naga_fn66() {
    var r65: vec3<u32>;
    var r88: u32;
    var r89: bool;
    var r92: u32;
    var r95: vec4<u32>;
    var r96: u32;
    var r97: u32;
    var r98: i32;
    var r99: i32;
    var r100: u32;
    var r101: u32;
    var r105: u32;
    var r103: u32;
    var r107: u32;
    var r110: u32;
    var r108: u32;
    var r112: u32;
    var r113: u32;
    var r115: u32;
    var r116: u32;
    var r118: u32;
    var r119: u32;
    var r121: u32;
    var r122: u32;
    var r124: u32;
    var r125: u32;
    var r126: u32;
    var r128: u32;
    var r129: u32;
    var r130: u32;
    var r127: u32;
    var r132: u32;
    var r133: u32;
    var r134: u32;
    var r136: u32;
    var r137: u32;
    var r138: u32;
    var r135: u32;
    var r140: u32;
    var r141: u32;
    var r142: u32;
    var r144: u32;
    var r145: u32;
    var r146: u32;
    var r143: i32;
    var r147: u32;
    var r149: u32;
    var r150: i32;
    var r151: u32;
    var r153: u32;
    var r154: i32;
    var r155: u32;
    var r157: u32;
    var r158: u32;
    var r160: u32;
    var r161: u32;
    var r163: u32;
    var r164: i32;
    var r165: u32;
    var r167: u32;
    r65 = g63;
    r88 = r65[0u];
    r89 = (r88 >= 257u);
    if (r89) {
        return;
    }
    r92 = r65[0u];
    r95 = g8.m0[r92];
    r96 = r95[0u];
    r97 = r95[1u];
    r98 = bitcast<i32>(r96);
    r99 = bitcast<i32>(r97);
    r100 = r65[0u];
    r101 = (r100 * 15u);
    r105 = firstLeadingBit(r96);
    r103 = (31u - r105);
    g11.m0[r101] = r103;
    r107 = (r101 + 1u);
    r110 = firstTrailingBit(r96);
    r108 = min(32u, r110);
    g11.m0[r107] = r108;
    r112 = (r101 + 2u);
    r113 = firstLeadingBit(r96);
    g11.m0[r112] = r113;
    r115 = (r101 + 3u);
    r116 = firstTrailingBit(r96);
    g11.m0[r115] = r116;
    r118 = (r101 + 4u);
    r119 = countOneBits(r96);
    g11.m0[r118] = r119;
    r121 = (r101 + 5u);
    r122 = reverseBits(r96);
    g11.m0[r121] = r122;
    r124 = (r101 + 6u);
    r125 = r95[2u];
    r126 = r95[3u];
    r128 = min(r125, 32u);
    r129 = (32u - r128);
    r130 = min(r126, r129);
    r127 = extractBits(r96, u32(r128), u32(r130));
    g11.m0[r124] = r127;
    r132 = (r101 + 7u);
    r133 = r95[2u];
    r134 = r95[3u];
    r136 = min(r133, 32u);
    r137 = (32u - r136);
    r138 = min(r134, r137);
    r135 = insertBits(r96, r97, u32(r136), u32(r138));
    g11.m0[r132] = r135;
    r140 = (r101 + 8u);
    r141 = r95[2u];
    r142 = r95[3u];
    r144 = min(r141, 32u);
    r145 = (32u - r144);
    r146 = min(r142, r145);
    r143 = extractBits(r98, u32(r144), u32(r146));
    r147 = bitcast<u32>(r143);
    g11.m0[r140] = r147;
    r149 = (r101 + 9u);
    r150 = naga_fn15(r98, r99);
    r151 = bitcast<u32>(r150);
    g11.m0[r149] = r151;
    r153 = (r101 + 10u);
    r154 = naga_fn32(r98, r99);
    r155 = bitcast<u32>(r154);
    g11.m0[r153] = r155;
    r157 = (r101 + 11u);
    r158 = naga_fn45(r96, r97);
    g11.m0[r157] = r158;
    r160 = (r101 + 12u);
    r161 = naga_fn55(r96, r97);
    g11.m0[r160] = r161;
    r163 = (r101 + 13u);
    r164 = firstLeadingBit(r98);
    r165 = bitcast<u32>(r164);
    g11.m0[r163] = r165;
    r167 = (r101 + 14u);
    g11.m0[r167] = r97;
    return;
}

@compute
@workgroup_size(64u, 1u, 1u)
fn main(@builtin(global_invocation_id) g63_input: vec3<u32>) {
    g63 = g63_input;
    naga_fn66();
}
