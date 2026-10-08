import json,pathlib,subprocess
root=pathlib.Path(__file__).resolve().parents[2]
work=root/'.work/naga-csharp/per-vertex-checks';work.mkdir(exist_ok=True)
dotnet=root/'.dotnet/dotnet.exe';harness=root/'.work/naga-csharp/harness/bin/Release/net10.0/harness.dll'
oracle=root/'.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe'
native=root/'.work/naga-csharp/spirv-tools-local/tools/Release'
records=[]
def check(name,step,args,expected=True):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=30)
 log=p.stdout+p.stderr;(work/f'{name}-{step}.log').write_text(log,encoding='utf-8')
 records.append(dict(case=name,step=step,expected_accept=expected,code=p.returncode,passed=(p.returncode==0)==expected,message=log.splitlines()[0] if log else ''))
 return p.returncode==0
cases=[]
for i,element in enumerate(['f32','vec3f','i32','vec2u','f16','vec2<u16>']):
 enable='enable f16;' if element=='f16' else 'enable wgpu_int16;' if 'u16' in element else ''
 for struct in [False,True]:
  name=f'type-{i}-struct-{struct}';field=f'@location(0) @interpolate(per_vertex) value:array<{element},3>'
  source=enable+'enable wgpu_per_vertex; '+(f'struct Inputs {{ {field} }} @fragment fn main(input:Inputs) -> @location(0) vec4f {{ _ = input.value[1]; return vec4f(1); }}' if struct else f'@fragment fn main({field}) -> @location(0) vec4f {{ _ = value[1]; return vec4f(1); }}')
  path=work/f'{name}.wgsl';path.write_text(source,encoding='utf-8');cases.append((name,path))
cases.append(('corpus-wgsl',root/'.reference/wgpu/naga/tests/in/wgsl/per-vertex.wgsl'))
assembly=root/'.reference/wgpu/naga/tests/in/spv/per-vertex.spvasm';spv=work/'reference-input.spv'
if check('corpus-spv','assemble',[native/'spirv-as.exe','--target-env','spv1.3',assembly,'-o',spv]):cases.append(('corpus-spv',spv))
for name,path in cases:
 check(name,'reference-input',[oracle,path])
 wgsl,spv,back=[work/f'{name}-{part}' for part in ['managed.wgsl','managed.spv','roundtrip.wgsl']]
 if check(name,'managed-wgsl',[dotnet,harness,path,wgsl]):check(name,'reference-output',[oracle,wgsl])
 if check(name,'managed-spv',[dotnet,harness,path,spv]):
  check(name,'native',[native/'spirv-val.exe','--target-env','vulkan1.1',spv]);check(name,'reference-spv',[oracle,spv])
  if check(name,'roundtrip',[dotnet,harness,spv,back]):check(name,'reference-roundtrip',[oracle,back])
invalid=[
 '@fragment fn main(@location(0) @interpolate(per_vertex) value:array<f32,3>) {}',
 'enable wgpu_per_vertex; @fragment fn main(@location(0) @interpolate(per_vertex) value:array<f32,2>) {}',
 'enable wgpu_per_vertex; @fragment fn main(@location(0) @interpolate(per_vertex) value:vec3f) {}',
 'enable wgpu_per_vertex; @fragment fn main(@location(0) @interpolate(per_vertex) value:array<bool,3>) {}',
 'enable wgpu_per_vertex; @fragment fn main(@location(0) @interpolate(per_vertex) value:array<mat2x2f,3>) {}',
 'enable wgpu_per_vertex; @vertex fn main(@location(0) @interpolate(per_vertex) value:array<f32,3>) -> @builtin(position) vec4f { return vec4f(1); }',
 'enable wgpu_per_vertex; @compute @workgroup_size(1) fn main(@location(0) @interpolate(per_vertex) value:array<f32,3>) {}',
 'enable wgpu_per_vertex; @fragment fn main(@location(0) @interpolate(per_vertex,centroid) value:array<f32,3>) {}',
 'enable wgpu_per_vertex; @fragment fn main(@location(0) @interpolate(per_vertex,sample) value:array<f32,3>) {}',
 'enable wgpu_per_vertex; @fragment fn main(@location(0) @interpolate(per_vertex,center) value:array<f32,3>) {}',
]
for i,source in enumerate(invalid):
 name=f'invalid-{i}';path=work/f'{name}.wgsl';path.write_text(source,encoding='utf-8')
 check(name,'reference-rejection',[oracle,path],False);check(name,'managed-rejection',[dotnet,harness,path,work/f'{name}.spv'],False)
(work/'manifest.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
print(f'{sum(r["passed"] for r in records)}/{len(records)} checks passed');print(json.dumps([r for r in records if not r['passed']],indent=2))
raise SystemExit(0 if all(r['passed'] for r in records) else 1)
