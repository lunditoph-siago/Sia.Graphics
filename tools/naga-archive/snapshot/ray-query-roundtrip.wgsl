enable wgpu_ray_query;
struct n_Output_9 {
    m0: u32,
    m1: vec3<f32>,
}

struct n_SpirvBlock_output_8 {
    m0: n_Output_9,
}

struct n_RayIntersection_15 {
    m0: u32,
    m1: f32,
    m2: u32,
    m3: u32,
    m4: u32,
    m5: u32,
    m6: u32,
    m7: vec2<f32>,
    m8: bool,
    m9: mat4x3<f32>,
    m10: mat4x3<f32>,
}

struct n_RayDesc_39 {
    m0: u32,
    m1: u32,
    m2: f32,
    m3: f32,
    m4: vec3<f32>,
    m5: vec3<f32>,
}

@group(0) @binding(0) var n_acc_struct_7: acceleration_structure;
@group(0) @binding(1) var<storage, read_write> n_output_14: n_SpirvBlock_output_8;

fn n_query_loop_1(n_pos_20: vec3<f32>, n_dir_21: vec3<f32>, n_acs_22: acceleration_structure) -> n_RayIntersection_15 {
    var n_rq_24: ray_query;
    var r27: u32;
    var r29: f32;
    var r31: f32;
    var r162: bool;
    var r189: n_RayIntersection_15;
    var r38: n_RayDesc_39;
    var r40: u32;
    var r42: u32;
    var r43: bool;
    var r44: u32;
    var r45: bool;
    var r46: bool;
    var r47: bool;
    var r48: u32;
    var r49: u32;
    var r50: f32;
    var r51: f32;
    var r52: vec3<f32>;
    var r53: vec3<f32>;
    var r54: bool;
    var r55: bool;
    var r56: bool;
    var r57: vec3<bool>;
    var r59: bool;
    var r60: vec3<bool>;
    var r61: bool;
    var r62: bool;
    var r63: bool;
    var r64: bool;
    var r65: vec3<bool>;
    var r66: bool;
    var r67: vec3<bool>;
    var r68: bool;
    var r69: bool;
    var r70: bool;
    var r71: bool;
    var r73: u32;
    var r74: bool;
    var r76: u32;
    var r77: bool;
    var r78: bool;
    var r79: bool;
    var r80: bool;
    var r81: u32;
    var r82: bool;
    var r84: u32;
    var r85: bool;
    var r86: bool;
    var r87: bool;
    var r88: bool;
    var r89: u32;
    var r90: bool;
    var r92: u32;
    var r93: bool;
    var r94: bool;
    var r95: bool;
    var r96: bool;
    var r97: u32;
    var r98: bool;
    var r99: u32;
    var r100: bool;
    var r101: bool;
    var r102: bool;
    var r103: bool;
    var r105: u32;
    var r106: bool;
    var r107: u32;
    var r108: bool;
    var r109: bool;
    var r110: bool;
    var r111: bool;
    var r112: u32;
    var r113: bool;
    var r115: u32;
    var r116: bool;
    var r117: bool;
    var r118: bool;
    var r119: bool;
    var r120: u32;
    var r121: bool;
    var r123: u32;
    var r124: bool;
    var r125: bool;
    var r126: bool;
    var r127: bool;
    var r128: u32;
    var r129: bool;
    var r130: u32;
    var r131: bool;
    var r132: bool;
    var r133: bool;
    var r134: bool;
    var r135: u32;
    var r136: bool;
    var r137: u32;
    var r138: bool;
    var r139: bool;
    var r140: bool;
    var r141: bool;
    var r142: u32;
    var r143: bool;
    var r144: u32;
    var r145: bool;
    var r146: bool;
    var r147: bool;
    var r148: bool;
    var r155: u32;
    var r156: u32;
    var r157: bool;
    var r158: u32;
    var r159: bool;
    var r160: bool;
    var r161: bool;
    var r165: u32;
    var r166: bool;
    var r167: u32;
    var r168: bool;
    var r169: bool;
    var r170: bool;
    var r173: bool;
    var r175: u32;
    var r176: u32;
    var r177: bool;
    var r178: bool;
    var r182: u32;
    var r183: u32;
    var r184: bool;
    var r185: u32;
    var r186: bool;
    var r187: bool;
    var r188: bool;
    var r192: u32;
    var r193: bool;
    var r194: u32;
    var r195: bool;
    var r196: bool;
    var r199: u32;
    var r201: bool;
    var r204: u32;
    var r206: u32;
    var r209: u32;
    var r211: u32;
    var r214: u32;
    var r216: mat4x3<f32>;
    var r220: mat4x3<f32>;
    var r223: f32;
    var r225: bool;
    var r228: vec2<f32>;
    var r232: bool;
    var r235: n_RayIntersection_15;
    r27 = 0u;
    r29 = f32();
    r31 = f32();
    r38 = n_RayDesc_39(4u, 255u, 0.1f, 100f, n_pos_20, n_dir_21);
    r40 = r27;
    r42 = (r40 & 2u);
    r43 = (r42 != 0u);
    r44 = (r40 & 4u);
    r45 = (r44 != 0u);
    r46 = (!r45);
    r47 = (r43 && r46);
    r48 = r38.m0;
    r49 = r38.m1;
    r50 = r38.m2;
    r51 = r38.m3;
    r52 = r38.m4;
    r53 = r38.m5;
    r54 = (r50 <= r51);
    r55 = (r50 >= f32());
    r56 = (r54 && r55);
    r57 = ((bitcast<vec3<u32>>(r52) & vec3<u32>(2147483647u)) > vec3<u32>(2139095040u));
    r59 = any(r57);
    r60 = ((bitcast<vec3<u32>>(r52) & vec3<u32>(2147483647u)) == vec3<u32>(2139095040u));
    r61 = any(r60);
    r62 = (r59 || r61);
    r63 = (!r62);
    r64 = (r56 && r63);
    r65 = ((bitcast<vec3<u32>>(r53) & vec3<u32>(2147483647u)) > vec3<u32>(2139095040u));
    r66 = any(r65);
    r67 = ((bitcast<vec3<u32>>(r53) & vec3<u32>(2147483647u)) == vec3<u32>(2139095040u));
    r68 = any(r67);
    r69 = (r66 || r68);
    r70 = (!r69);
    r71 = (r64 && r70);
    r73 = (r48 & 256u);
    r74 = (r73 != 0u);
    r76 = (r48 & 512u);
    r77 = (r76 != 0u);
    r78 = (r74 && r77);
    r79 = (!r78);
    r80 = (r71 && r79);
    r81 = (r48 & 256u);
    r82 = (r81 != 0u);
    r84 = (r48 & 16u);
    r85 = (r84 != 0u);
    r86 = (r82 && r85);
    r87 = (!r86);
    r88 = (r80 && r87);
    r89 = (r48 & 256u);
    r90 = (r89 != 0u);
    r92 = (r48 & 32u);
    r93 = (r92 != 0u);
    r94 = (r90 && r93);
    r95 = (!r94);
    r96 = (r88 && r95);
    r97 = (r48 & 16u);
    r98 = (r97 != 0u);
    r99 = (r48 & 32u);
    r100 = (r99 != 0u);
    r101 = (r98 && r100);
    r102 = (!r101);
    r103 = (r96 && r102);
    r105 = (r48 & 1u);
    r106 = (r105 != 0u);
    r107 = (r48 & 2u);
    r108 = (r107 != 0u);
    r109 = (r106 && r108);
    r110 = (!r109);
    r111 = (r103 && r110);
    r112 = (r48 & 1u);
    r113 = (r112 != 0u);
    r115 = (r48 & 64u);
    r116 = (r115 != 0u);
    r117 = (r113 && r116);
    r118 = (!r117);
    r119 = (r111 && r118);
    r120 = (r48 & 1u);
    r121 = (r120 != 0u);
    r123 = (r48 & 128u);
    r124 = (r123 != 0u);
    r125 = (r121 && r124);
    r126 = (!r125);
    r127 = (r119 && r126);
    r128 = (r48 & 2u);
    r129 = (r128 != 0u);
    r130 = (r48 & 64u);
    r131 = (r130 != 0u);
    r132 = (r129 && r131);
    r133 = (!r132);
    r134 = (r127 && r133);
    r135 = (r48 & 2u);
    r136 = (r135 != 0u);
    r137 = (r48 & 128u);
    r138 = (r137 != 0u);
    r139 = (r136 && r138);
    r140 = (!r139);
    r141 = (r134 && r140);
    r142 = (r48 & 64u);
    r143 = (r142 != 0u);
    r144 = (r48 & 128u);
    r145 = (r144 != 0u);
    r146 = (r143 && r145);
    r147 = (!r146);
    r148 = (r141 && r147);
    r31 = r51;
    if (r148) {
        rayQueryInitialize((&n_rq_24), n_acs_22, RayDesc(r48, r49, r50, r51, r52, r53));
        r29 = r50;
        r27 = 1u;
    }
    loop {
        r155 = r27;
        r156 = (r155 & 2u);
        r157 = (r156 != 0u);
        r158 = (r155 & 4u);
        r159 = (r158 != 0u);
        r160 = (!r159);
        r161 = (r157 && r160);
        r162 = false;
        r165 = (r155 & 1u);
        r166 = (r165 != 0u);
        r167 = (r155 & 4u);
        r168 = (r167 != 0u);
        r169 = (!r168);
        r170 = (r166 && r169);
        if (r170) {
            r173 = rayQueryProceed((&n_rq_24));
            r162 = r173;
            r175 = select(6u, 2u, r173);
            r176 = (r155 | r175);
            r27 = r176;
        }
        r177 = r162;
        r178 = (!r177);
        if (r178) {
            break;
        }
        continue;
    }
    r182 = r27;
    r183 = (r182 & 2u);
    r184 = (r183 != 0u);
    r185 = (r182 & 4u);
    r186 = (r185 != 0u);
    r187 = (!r186);
    r188 = (r184 && r187);
    r189 = n_RayIntersection_15();
    r192 = (r182 & 2u);
    r193 = (r192 != 0u);
    r194 = (r182 & 4u);
    r195 = (r194 != 0u);
    r196 = (r193 && r195);
    if (r196) {
        r199 = rayQueryGetCommittedIntersection((&n_rq_24)).kind;
        r189.m0 = r199;
        r201 = (r199 != 0u);
        if (r201) {
            r204 = rayQueryGetCommittedIntersection((&n_rq_24)).instance_custom_data;
            r189.m2 = r204;
            r206 = rayQueryGetCommittedIntersection((&n_rq_24)).instance_index;
            r189.m3 = r206;
            r209 = rayQueryGetCommittedIntersection((&n_rq_24)).sbt_record_offset;
            r189.m4 = r209;
            r211 = rayQueryGetCommittedIntersection((&n_rq_24)).geometry_index;
            r189.m5 = r211;
            r214 = rayQueryGetCommittedIntersection((&n_rq_24)).primitive_index;
            r189.m6 = r214;
            r216 = rayQueryGetCommittedIntersection((&n_rq_24)).object_to_world;
            r189.m9 = r216;
            r220 = rayQueryGetCommittedIntersection((&n_rq_24)).world_to_object;
            r189.m10 = r220;
            r223 = rayQueryGetCommittedIntersection((&n_rq_24)).t;
            r189.m1 = r223;
            r225 = (r199 == 1u);
            if (r225) {
                r228 = rayQueryGetCommittedIntersection((&n_rq_24)).barycentrics;
                r189.m7 = r228;
                r232 = rayQueryGetCommittedIntersection((&n_rq_24)).front_face;
                r189.m8 = r232;
            }
        }
    }
    r235 = r189;
    return r235;
}

fn n_get_torus_normal_2(n_world_point_237: vec3<f32>, n_intersection_238: n_RayIntersection_15) -> vec3<f32> {
    var r240: mat4x3<f32>;
    var r242: f32;
    var r243: f32;
    var r244: f32;
    var r245: vec4<f32>;
    var r247: vec3<f32>;
    var r248: vec2<f32>;
    var r250: vec2<f32>;
    var r252: vec2<f32>;
    var r253: vec2<f32>;
    var r254: mat4x3<f32>;
    var r256: f32;
    var r257: f32;
    var r258: vec4<f32>;
    var r259: vec3<f32>;
    var r260: vec3<f32>;
    var r261: vec3<f32>;
    r240 = n_intersection_238.m10;
    r242 = n_world_point_237[0u];
    r243 = n_world_point_237[1u];
    r244 = n_world_point_237[2u];
    r245 = vec4<f32>(r242, r243, r244, 1f);
    r247 = (r240 * r245);
    r248 = vec2<f32>(r247[0u], r247[1u]);
    r250 = normalize(r248);
    r252 = vec2<f32>(2.4f, 2.4f);
    r253 = (r250 * r252);
    r254 = n_intersection_238.m9;
    r256 = r253[0u];
    r257 = r253[1u];
    r258 = vec4<f32>(r256, r257, 0f, 1f);
    r259 = (r254 * r258);
    r260 = (n_world_point_237 - r259);
    r261 = normalize(r260);
    return r261;
}

fn n_main_3() {
    var r268: n_RayIntersection_15;
    var r273: u32;
    var r274: bool;
    var r276: u32;
    var r277: f32;
    var r278: vec3<f32>;
    var r279: vec3<f32>;
    var r280: vec3<f32>;
    r268 = n_query_loop_1(vec3<f32>(0f, 0f, 0f), vec3<f32>(0f, 1f, 0f), n_acc_struct_7);
    r273 = r268.m0;
    r274 = (r273 == 0u);
    r276 = select(u32(), 1u, r274);
    n_output_14.m0.m0 = r276;
    r277 = r268.m1;
    r278 = vec3<f32>(r277, r277, r277);
    r279 = (vec3<f32>(0f, 1f, 0f) * r278);
    r280 = n_get_torus_normal_2(r279, r268);
    n_output_14.m0.m1 = r280;
    return;
}

fn n_main_candidate_4() {
    var n_rq_285: ray_query;
    var r286: u32;
    var r287: f32;
    var r288: f32;
    var r400: n_RayIntersection_15;
    var r456: f32;
    var r290: n_RayDesc_39;
    var r291: u32;
    var r292: u32;
    var r293: bool;
    var r294: u32;
    var r295: bool;
    var r296: bool;
    var r297: bool;
    var r298: u32;
    var r299: u32;
    var r300: f32;
    var r301: f32;
    var r302: vec3<f32>;
    var r303: vec3<f32>;
    var r304: bool;
    var r305: bool;
    var r306: bool;
    var r307: vec3<bool>;
    var r308: bool;
    var r309: vec3<bool>;
    var r310: bool;
    var r311: bool;
    var r312: bool;
    var r313: bool;
    var r314: vec3<bool>;
    var r315: bool;
    var r316: vec3<bool>;
    var r317: bool;
    var r318: bool;
    var r319: bool;
    var r320: bool;
    var r321: u32;
    var r322: bool;
    var r323: u32;
    var r324: bool;
    var r325: bool;
    var r326: bool;
    var r327: bool;
    var r328: u32;
    var r329: bool;
    var r330: u32;
    var r331: bool;
    var r332: bool;
    var r333: bool;
    var r334: bool;
    var r335: u32;
    var r336: bool;
    var r337: u32;
    var r338: bool;
    var r339: bool;
    var r340: bool;
    var r341: bool;
    var r342: u32;
    var r343: bool;
    var r344: u32;
    var r345: bool;
    var r346: bool;
    var r347: bool;
    var r348: bool;
    var r349: u32;
    var r350: bool;
    var r351: u32;
    var r352: bool;
    var r353: bool;
    var r354: bool;
    var r355: bool;
    var r356: u32;
    var r357: bool;
    var r358: u32;
    var r359: bool;
    var r360: bool;
    var r361: bool;
    var r362: bool;
    var r363: u32;
    var r364: bool;
    var r365: u32;
    var r366: bool;
    var r367: bool;
    var r368: bool;
    var r369: bool;
    var r370: u32;
    var r371: bool;
    var r372: u32;
    var r373: bool;
    var r374: bool;
    var r375: bool;
    var r376: bool;
    var r377: u32;
    var r378: bool;
    var r379: u32;
    var r380: bool;
    var r381: bool;
    var r382: bool;
    var r383: bool;
    var r384: u32;
    var r385: bool;
    var r386: u32;
    var r387: bool;
    var r388: bool;
    var r389: bool;
    var r390: bool;
    var r393: u32;
    var r394: u32;
    var r395: bool;
    var r396: u32;
    var r397: bool;
    var r398: bool;
    var r399: bool;
    var r401: u32;
    var r402: bool;
    var r403: u32;
    var r404: bool;
    var r405: bool;
    var r406: bool;
    var r409: u32;
    var r410: bool;
    var r411: u32;
    var r413: bool;
    var r416: u32;
    var r418: u32;
    var r420: u32;
    var r422: u32;
    var r424: u32;
    var r426: mat4x3<f32>;
    var r428: mat4x3<f32>;
    var r430: bool;
    var r433: f32;
    var r435: vec2<f32>;
    var r437: bool;
    var r439: n_RayIntersection_15;
    var r440: u32;
    var r441: bool;
    var r446: u32;
    var r447: u32;
    var r448: bool;
    var r449: u32;
    var r450: bool;
    var r451: bool;
    var r452: bool;
    var r455: u32;
    var r457: f32;
    var r458: u32;
    var r459: bool;
    var r462: f32;
    var r463: f32;
    var r464: bool;
    var r465: f32;
    var r466: bool;
    var r467: bool;
    var r468: bool;
    var r469: bool;
    var r472: u32;
    var r473: bool;
    var r477: u32;
    var r478: u32;
    var r479: bool;
    var r480: u32;
    var r481: bool;
    var r482: bool;
    var r483: bool;
    var r486: u32;
    var r487: bool;
    var r490: u32;
    var r491: u32;
    var r492: bool;
    var r493: u32;
    var r494: bool;
    var r495: bool;
    var r496: bool;
    r286 = 0u;
    r287 = f32();
    r288 = f32();
    r290 = n_RayDesc_39(4u, 255u, 0.1f, 100f, vec3<f32>(0f, 0f, 0f), vec3<f32>(0f, 1f, 0f));
    r291 = r286;
    r292 = (r291 & 2u);
    r293 = (r292 != 0u);
    r294 = (r291 & 4u);
    r295 = (r294 != 0u);
    r296 = (!r295);
    r297 = (r293 && r296);
    r298 = r290.m0;
    r299 = r290.m1;
    r300 = r290.m2;
    r301 = r290.m3;
    r302 = r290.m4;
    r303 = r290.m5;
    r304 = (r300 <= r301);
    r305 = (r300 >= f32());
    r306 = (r304 && r305);
    r307 = ((bitcast<vec3<u32>>(r302) & vec3<u32>(2147483647u)) > vec3<u32>(2139095040u));
    r308 = any(r307);
    r309 = ((bitcast<vec3<u32>>(r302) & vec3<u32>(2147483647u)) == vec3<u32>(2139095040u));
    r310 = any(r309);
    r311 = (r308 || r310);
    r312 = (!r311);
    r313 = (r306 && r312);
    r314 = ((bitcast<vec3<u32>>(r303) & vec3<u32>(2147483647u)) > vec3<u32>(2139095040u));
    r315 = any(r314);
    r316 = ((bitcast<vec3<u32>>(r303) & vec3<u32>(2147483647u)) == vec3<u32>(2139095040u));
    r317 = any(r316);
    r318 = (r315 || r317);
    r319 = (!r318);
    r320 = (r313 && r319);
    r321 = (r298 & 256u);
    r322 = (r321 != 0u);
    r323 = (r298 & 512u);
    r324 = (r323 != 0u);
    r325 = (r322 && r324);
    r326 = (!r325);
    r327 = (r320 && r326);
    r328 = (r298 & 256u);
    r329 = (r328 != 0u);
    r330 = (r298 & 16u);
    r331 = (r330 != 0u);
    r332 = (r329 && r331);
    r333 = (!r332);
    r334 = (r327 && r333);
    r335 = (r298 & 256u);
    r336 = (r335 != 0u);
    r337 = (r298 & 32u);
    r338 = (r337 != 0u);
    r339 = (r336 && r338);
    r340 = (!r339);
    r341 = (r334 && r340);
    r342 = (r298 & 16u);
    r343 = (r342 != 0u);
    r344 = (r298 & 32u);
    r345 = (r344 != 0u);
    r346 = (r343 && r345);
    r347 = (!r346);
    r348 = (r341 && r347);
    r349 = (r298 & 1u);
    r350 = (r349 != 0u);
    r351 = (r298 & 2u);
    r352 = (r351 != 0u);
    r353 = (r350 && r352);
    r354 = (!r353);
    r355 = (r348 && r354);
    r356 = (r298 & 1u);
    r357 = (r356 != 0u);
    r358 = (r298 & 64u);
    r359 = (r358 != 0u);
    r360 = (r357 && r359);
    r361 = (!r360);
    r362 = (r355 && r361);
    r363 = (r298 & 1u);
    r364 = (r363 != 0u);
    r365 = (r298 & 128u);
    r366 = (r365 != 0u);
    r367 = (r364 && r366);
    r368 = (!r367);
    r369 = (r362 && r368);
    r370 = (r298 & 2u);
    r371 = (r370 != 0u);
    r372 = (r298 & 64u);
    r373 = (r372 != 0u);
    r374 = (r371 && r373);
    r375 = (!r374);
    r376 = (r369 && r375);
    r377 = (r298 & 2u);
    r378 = (r377 != 0u);
    r379 = (r298 & 128u);
    r380 = (r379 != 0u);
    r381 = (r378 && r380);
    r382 = (!r381);
    r383 = (r376 && r382);
    r384 = (r298 & 64u);
    r385 = (r384 != 0u);
    r386 = (r298 & 128u);
    r387 = (r386 != 0u);
    r388 = (r385 && r387);
    r389 = (!r388);
    r390 = (r383 && r389);
    r288 = r301;
    if (r390) {
        rayQueryInitialize((&n_rq_285), n_acc_struct_7, RayDesc(r298, r299, r300, r301, r302, r303));
        r287 = r300;
        r286 = 1u;
    }
    r393 = r286;
    r394 = (r393 & 2u);
    r395 = (r394 != 0u);
    r396 = (r393 & 4u);
    r397 = (r396 != 0u);
    r398 = (!r397);
    r399 = (r395 && r398);
    r400 = n_RayIntersection_15();
    r401 = (r393 & 2u);
    r402 = (r401 != 0u);
    r403 = (r393 & 4u);
    r404 = (r403 != 0u);
    r405 = (!r404);
    r406 = (r402 && r405);
    if (r406) {
        r409 = select(1u, 0u, (rayQueryGetCandidateIntersection((&n_rq_285)).kind == 1u));
        r410 = (r409 == 0u);
        r411 = select(3u, 1u, r410);
        r400.m0 = r411;
        r413 = (r411 != 0u);
        if (r413) {
            r416 = rayQueryGetCandidateIntersection((&n_rq_285)).instance_custom_data;
            r400.m2 = r416;
            r418 = rayQueryGetCandidateIntersection((&n_rq_285)).instance_index;
            r400.m3 = r418;
            r420 = rayQueryGetCandidateIntersection((&n_rq_285)).sbt_record_offset;
            r400.m4 = r420;
            r422 = rayQueryGetCandidateIntersection((&n_rq_285)).geometry_index;
            r400.m5 = r422;
            r424 = rayQueryGetCandidateIntersection((&n_rq_285)).primitive_index;
            r400.m6 = r424;
            r426 = rayQueryGetCandidateIntersection((&n_rq_285)).object_to_world;
            r400.m9 = r426;
            r428 = rayQueryGetCandidateIntersection((&n_rq_285)).world_to_object;
            r400.m10 = r428;
            r430 = (r411 == 1u);
            if (r430) {
                r433 = rayQueryGetCandidateIntersection((&n_rq_285)).t;
                r400.m1 = r433;
                r435 = rayQueryGetCandidateIntersection((&n_rq_285)).barycentrics;
                r400.m7 = r435;
                r437 = rayQueryGetCandidateIntersection((&n_rq_285)).front_face;
                r400.m8 = r437;
            }
        }
    }
    r439 = r400;
    r440 = r439.m0;
    r441 = (r440 == 3u);
    if (r441) {
        r446 = r286;
        r447 = (r446 & 2u);
        r448 = (r447 != 0u);
        r449 = (r446 & 4u);
        r450 = (r449 != 0u);
        r451 = (!r450);
        r452 = (r448 && r451);
        if (r452) {
            r455 = select(1u, 0u, (rayQueryGetCandidateIntersection((&n_rq_285)).kind == 1u));
            r457 = r288;
            r456 = r457;
            r458 = rayQueryGetCommittedIntersection((&n_rq_285)).kind;
            r459 = (r458 != 0u);
            if (r459) {
                r462 = rayQueryGetCommittedIntersection((&n_rq_285)).t;
                r456 = r462;
            }
            r463 = r287;
            r464 = (10f >= r463);
            r465 = r456;
            r466 = (10f <= r465);
            r467 = (r464 && r466);
            r468 = (r455 == 1u);
            r469 = (r467 && r468);
            if (r469) {
                rayQueryGenerateIntersection((&n_rq_285), 10f);
            }
        }
    }
    else {
        r472 = r439.m0;
        r473 = (r472 == 1u);
        if (r473) {
            r477 = r286;
            r478 = (r477 & 2u);
            r479 = (r478 != 0u);
            r480 = (r477 & 4u);
            r481 = (r480 != 0u);
            r482 = (!r481);
            r483 = (r479 && r482);
            if (r483) {
                r486 = select(1u, 0u, (rayQueryGetCandidateIntersection((&n_rq_285)).kind == 1u));
                r487 = (r486 == 0u);
                if (r487) {
                    rayQueryConfirmIntersection((&n_rq_285));
                }
            }
        }
        else {
            r490 = r286;
            r491 = (r490 & 2u);
            r492 = (r491 != 0u);
            r493 = (r490 & 4u);
            r494 = (r493 != 0u);
            r495 = (!r494);
            r496 = (r492 && r495);
            if (r496) {
                rayQueryTerminate((&n_rq_285));
            }
        }
    }
    return;
}

fn naga_fn499() {
    n_main_3();
    return;
}

fn naga_fn502() {
    n_main_candidate_4();
    return;
}

@compute
@workgroup_size(1u, 1u, 1u)
fn main() {
    naga_fn499();
}

@compute
@workgroup_size(1u, 1u, 1u)
fn main_candidate() {
    naga_fn502();
}
