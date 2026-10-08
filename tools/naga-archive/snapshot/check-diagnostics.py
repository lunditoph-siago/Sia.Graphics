import json, pathlib, subprocess
root = pathlib.Path(__file__).resolve().parents[2]
work = root / '.work/naga-csharp/diagnostic-checks'; work.mkdir(exist_ok=True)
dotnet = root / '.dotnet/dotnet.exe'
harness = root / '.work/naga-csharp/harness/bin/Release/net10.0/harness.dll'
oracle = root / '.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe'
validator = root / '.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe'
main = '@compute @workgroup_size(4) fn main(@builtin(local_invocation_index) i: u32) { if i > 0u { workgroupBarrier(); } }'
valid = {
    'corpus': (root / '.reference/wgpu/naga/tests/in/wgsl/diagnostic-filter.wgsl').read_text(),
    'all-levels': 'diagnostic(off, derivative_uniformity); diagnostic(info, vendor.first,); diagnostic(warning, vendor.second); diagnostic(error, vendor.third); '+main,
    'identical-directives': 'diagnostic(off, derivative_uniformity); diagnostic(off, derivative_uniformity); '+main,
    'overrides': 'diagnostic(off, derivative_uniformity); @diagnostic(error, derivative_uniformity) @diagnostic(info, vendor.first) '+main,
    'unknown': 'diagnostic(warning, unknown_rule); @diagnostic(off, unknown_rule) '+main,
    'derivative-divergent': '@fragment fn main(@builtin(position) p: vec4f) -> @location(0) f32 { if p.x > 0.0 { return dpdx(p.y); } return 0.0; }',
    'helper-barrier-divergent': 'fn helper() { workgroupBarrier(); } @compute @workgroup_size(4) fn main(@builtin(local_invocation_index) i:u32) { if i > 0u { helper(); } }',
    'uniform-load-divergent': 'var<workgroup> x:u32; @compute @workgroup_size(4) fn main(@builtin(local_invocation_index) i:u32) { if i > 0u { _ = workgroupUniformLoad(&x); } }',
    'requirements': 'requires readonly_and_readwrite_storage_textures, packed_4x8_integer_dot_product, pointer_composite_access,; requires pointer_composite_access; '+main,
    'required-storage-texture': 'requires readonly_and_readwrite_storage_textures; @group(0) @binding(0) var image:texture_storage_2d<r32uint,read_write>; @compute @workgroup_size(1) fn main() { let value = textureLoad(image,vec2i(0)); textureStore(image,vec2i(0),value); }',
    'all-enables': 'enable f16, clip_distances, dual_source_blending, wgpu_mesh_shader, wgpu_ray_query, wgpu_ray_query_vertex_return, wgpu_ray_tracing_pipeline, wgpu_cooperative_matrix, draw_index, primitive_index, wgpu_per_vertex, wgpu_binding_array, wgpu_int16,; '+main,
}
invalid = [
    'diagnostic(fatal, derivative_uniformity); '+main,
    'diagnostic(off derivative_uniformity); '+main,
    'diagnostic(off, derivative_uniformity, vendor.rule); '+main,
    'diagnostic(off, vendor.rule.extra); '+main,
    'diagnostic(off, 1); '+main,
    'diagnostic(off, derivative_uniformity); diagnostic(error, derivative_uniformity); '+main,
    '@diagnostic(off, derivative_uniformity) @diagnostic(off, derivative_uniformity) '+main,
    '@diagnostic(off, derivative_uniformity) @diagnostic(error, derivative_uniformity) '+main,
    main+' diagnostic(off, derivative_uniformity);',
    '@diagnostic(off, derivative_uniformity) const x = 1; '+main,
    '@diagnostic(off, derivative_uniformity) var<private> x:u32; '+main,
    '@compute @workgroup_size(1) fn main() { @diagnostic(off, derivative_uniformity) {} }',
    '@compute @workgroup_size(1) fn main() @diagnostic(off, derivative_uniformity) {}',
    '@compute @workgroup_size(1) fn main() { @diagnostic(off, derivative_uniformity) if true {} }',
    'requires unknown_requirement; '+main,
    'requires unrestricted_pointer_parameters; '+main,
    'requires ; '+main,
    'requires pointer_composite_access,,; '+main,
    main+' requires pointer_composite_access;',
    '; requires pointer_composite_access; '+main,
    'enable unknown_extension; '+main,
    'enable subgroups; '+main,
    'enable ; '+main,
    'enable f16,,; '+main,
]
records=[]
def check(name,step,args,expected=True):
    p=subprocess.run(list(map(str,args)),capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=30)
    log=p.stdout+p.stderr; (work/f'{name}-{step}.log').write_text(log,encoding='utf-8')
    records.append(dict(case=name,step=step,expected_accept=expected,code=p.returncode,passed=(p.returncode==0)==expected,message=log.splitlines()[0] if log else ''))
    return p.returncode==0
for name,source in valid.items():
    path=work/f'{name}.wgsl'; path.write_text(source,encoding='utf-8')
    check(name,'reference-input',[oracle,path])
    wgsl,spv,back=[work/f'{name}-{part}' for part in ['managed.wgsl','managed.spv','roundtrip.wgsl']]
    if check(name,'managed-wgsl',[dotnet,harness,path,wgsl]): check(name,'reference-output',[oracle,wgsl])
    if check(name,'managed-spv',[dotnet,harness,path,spv]):
        check(name,'native',[validator,'--target-env','vulkan1.1',spv]); check(name,'reference-spv',[oracle,spv])
        if check(name,'roundtrip',[dotnet,harness,spv,back]): check(name,'reference-roundtrip',[oracle,back])
for index,source in enumerate(invalid):
    name=f'invalid-{index}'; path=work/f'{name}.wgsl'; path.write_text(source,encoding='utf-8')
    check(name,'reference-rejection',[oracle,path],False); check(name,'managed-rejection',[dotnet,harness,path,work/f'{name}.spv'],False)
(work/'manifest.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
print(f'{sum(r["passed"] for r in records)}/{len(records)} checks passed')
print(json.dumps([r for r in records if not r['passed']],indent=2))
raise SystemExit(0 if all(r['passed'] for r in records) else 1)
