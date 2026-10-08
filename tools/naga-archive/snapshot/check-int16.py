import pathlib, subprocess, json
root=pathlib.Path(__file__).resolve().parents[2]
work=root/'.work/naga-csharp/int16-checks'; work.mkdir(exist_ok=True)
dotnet=root/'.dotnet/dotnet.exe'; harness=root/'.work/naga-csharp/harness/bin/Release/net10.0/harness.dll'
oracle=root/'.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe'
validator=root/'.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe'
prefix='enable wgpu_int16; enable f16; struct Data { s: i16, u: u16, v: vec2<u16>, } @group(0) @binding(0) var<storage,read_write> data: Data; '
cases={
 'upstream':(root/'.reference/wgpu/naga/tests/in/wgsl/int16.wgsl').read_text(),
 'bits':prefix+'@compute @workgroup_size(1) fn main(@builtin(local_invocation_index) i: u32) { let v = u16(i); data.u = countLeadingZeros(v) + countTrailingZeros(v) + firstLeadingBit(v) + firstTrailingBit(v) + countOneBits(v) + reverseBits(v); data.s = firstLeadingBit(i16(i)); }',
 'casts':prefix+'@compute @workgroup_size(1) fn main(@builtin(local_invocation_index) i: u32) { data.s = i16(f16(i)); data.u = u16(f32(i)); data.v = vec2<u16>(u16(i), u16(65535)); }',
 'bitcast':prefix+'@compute @workgroup_size(1) fn main(@builtin(local_invocation_index) i: u32) { data.v = bitcast<vec2<u16>>(i); data.u = bitcast<u16>(i16(i)); }',
 'division':prefix+'@compute @workgroup_size(1) fn main(@builtin(local_invocation_index) i: u32) { data.s = i16(i) / i16(-1); data.u = u16(i) / u16(i); }',
 'fields':prefix+'@compute @workgroup_size(1) fn main(@builtin(local_invocation_index) i: u32) { data.s = extractBits(i16(i), i, i); data.v = insertBits(vec2<u16>(u16(i)), vec2<u16>(u16(3)), i, i); }',
 'io': 'enable wgpu_int16; enable f16; @fragment fn main(@location(0) @interpolate(flat) value: u16, @location(1) half: f16) -> @location(0) vec4f { return vec4f(f32(value) + f32(half)); }',
 'wide': 'struct Data { s: i64, u: u64, v: vec2<u64>, } @group(0) @binding(0) var<storage,read_write> data: Data; @compute @workgroup_size(1) fn main(@builtin(local_invocation_index) i: u32) { let v = u64(i); data.u = countLeadingZeros(v) + countTrailingZeros(v) + firstLeadingBit(v) + firstTrailingBit(v) + countOneBits(v) + reverseBits(v); data.s = extractBits(i64(i), i, i); data.v = insertBits(vec2<u64>(u64(i)), vec2<u64>(3lu), i, i); }',
}
records=[]
def run(name,step,args):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=30)
 log=p.stdout+p.stderr; (work/f'{name}-{step}.log').write_text(log,encoding='utf-8')
 records.append({'case':name,'step':step,'code':p.returncode,'message':log.splitlines()[0] if log else ''})
 return p.returncode==0
for name,source in cases.items():
 path=work/f'{name}.wgsl'; path.write_text(source,encoding='utf-8')
 run(name,'input-reference',[oracle,path])
 wgsl=work/f'{name}-managed.wgsl'; spv=work/f'{name}-managed.spv'
 if run(name,'managed-wgsl',[dotnet,harness,path,wgsl]): run(name,'output-reference',[oracle,wgsl])
 if run(name,'managed-spv',[dotnet,harness,path,spv]):
  run(name,'native',[validator,'--target-env','vulkan1.1',spv]); run(name,'spirv-reference',[oracle,spv])
  roundtrip=work/f'{name}-roundtrip.wgsl'
  if run(name,'roundtrip',[dotnet,harness,spv,roundtrip]): run(name,'roundtrip-reference',[oracle,roundtrip])
(work/'manifest.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
print(f'{sum(r["code"]==0 for r in records)}/{len(records)} checks passed')
print(json.dumps([r for r in records if r['code']!=0],indent=2))
raise SystemExit(0 if all(r['code']==0 for r in records) else 1)
