enable wgpu_ray_query;
struct Output {
    visible: u32,
    normal: vec3<f32>,
}

@group(0) @binding(0) var acc_struct: acceleration_structure;
@group(0) @binding(1) var<storage, read_write> output: Output;

fn query_loop(pos: vec3<f32>, dir: vec3<f32>, acs: acceleration_structure) -> RayIntersection {
    var rq: ray_query;
    rayQueryInitialize((&rq), acs, RayDesc(4u, 255u, 0.1f, 100f, pos, dir));
    loop {
        let sia_naga_temp0: bool = rayQueryProceed((&rq));
        if ((!sia_naga_temp0)) {
            break;
        }
         {
        }
    }
    let sia_naga_temp1 = rayQueryGetCommittedIntersection((&rq));
    return sia_naga_temp1;
}

fn get_torus_normal(world_point: vec3<f32>, intersection: RayIntersection) -> vec3<f32> {
    let local_point: vec3<f32> = (intersection.world_to_object * vec4<f32>(world_point, 1f));
    let point_on_guiding_line: vec2<f32> = (normalize(local_point.xy) * 2.4f);
    let world_point_on_guiding_line: vec3<f32> = (intersection.object_to_world * vec4<f32>(point_on_guiding_line, 0f, 1f));
    return normalize((world_point - world_point_on_guiding_line));
}

@compute
@workgroup_size(1i, 1u, 1u)
fn main() {
    let pos: vec3<f32> = vec3<f32>(0f);
    let dir: vec3<f32> = vec3<f32>(0f, 1f, 0f);
    let sia_naga_temp2 = query_loop(pos, dir, acc_struct);
    let intersection = sia_naga_temp2;
    output.visible = u32((intersection.kind == 0u));
    let sia_naga_temp3: vec3<f32> = get_torus_normal((dir * intersection.t), intersection);
    output.normal = sia_naga_temp3;
}

@compute
@workgroup_size(1i, 1u, 1u)
fn main_candidate() {
    let pos: vec3<f32> = vec3<f32>(0f);
    let dir: vec3<f32> = vec3<f32>(0f, 1f, 0f);
    var rq: ray_query;
    rayQueryInitialize((&rq), acc_struct, RayDesc(4u, 255u, 0.1f, 100f, pos, dir));
    let sia_naga_temp4 = rayQueryGetCandidateIntersection((&rq));
    let intersection = sia_naga_temp4;
    if ((intersection.kind == 3u)) {
        rayQueryGenerateIntersection((&rq), 10f);
    }
    else {
        if ((intersection.kind == 1u)) {
            rayQueryConfirmIntersection((&rq));
        }
        else {
            rayQueryTerminate((&rq));
        }
    }
}
