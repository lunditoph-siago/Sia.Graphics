import hashlib,json,pathlib,struct,subprocess
from vulkan_compute import Vulkan
root=pathlib.Path(__file__).resolve().parents[2];task=root/'.work/naga-csharp';work=task/'unresolved-array-gpu';work.mkdir(exist_ok=True)
dotnet=root/'.dotnet/dotnet.exe';harness=task/'harness/bin/Release/net10.0/harness.dll';oracle=task/'oracle-target/release/sia-naga-reference-oracle.exe';validator=task/'spirv-tools-local/tools/Release/spirv-val.exe'
records=[]
def run(name,args):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=60)
 (work/f'{name}.log').write_text(p.stdout+p.stderr,encoding='utf-8');records.append(dict(check=name,passed=p.returncode==0,message=p.stdout+p.stderr));print(name,p.returncode,flush=True)
 if p.returncode:raise RuntimeError(p.stdout+p.stderr)
for mode in ['value','atomic','byvalue']:
 atomic=mode=='atomic';length='n' if mode=='byvalue' else 'n+1u';element='atomic<u32>' if atomic else 'u32'
 helper='fn sum(p:array<u32,n>)->u32 { var total=0u; for(var j=0u;j<n;j++){total+=p[j];} return total; }' if mode=='byvalue' else ''
 total='sum(data)' if mode=='byvalue' else '+'.join(f'atomicLoad(&data[{i}])' if atomic else f'data[{i}]' for i in range(3 if atomic else 4))
 store='atomicAdd(&data[lane%3u],v);' if atomic else 'data[lane]=v;'
 last='atomicLoad(&data[n])' if atomic else 'data[n-1u]' if mode=='byvalue' else 'data[n]'
 source=f'''@id(7) override n=5u;
@group(0) @binding(0) var<storage,read> inputs:array<u32>;
@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;
var<workgroup> data:array<{element},{length}>;
{helper}
@compute @workgroup_size(4) fn main(@builtin(local_invocation_index) lane:u32,@builtin(workgroup_id) group:vec3u) {{
 let v=inputs[group.x*4u+lane]^(group.x*17u+lane); {store} workgroupBarrier();
 if lane==0u {{ outputs[group.x]={total}; outputs[5u+group.x]={last}; }}
}}'''
 path=work/f'{mode}.wgsl';path.write_text(source,encoding='utf-8');direct=work/f'{mode}.spv';roundtrip=work/f'{mode}-roundtrip.spv'
 run(mode+'-unresolved',[dotnet,harness,path,direct]);run(mode+'-unresolved-validate',[validator,'--target-env','vulkan1.1',direct])
 run(mode+'-roundtrip',[dotnet,harness,direct,roundtrip]);run(mode+'-roundtrip-validate',[validator,'--target-env','vulkan1.1',roundtrip])
 for n in [5,17,32]:
  inputs=[(j*0x9e3779b9+0x12345678)&0xffffffff for j in range(20)]
  expected=[sum(inputs[g*4+i]^(g*17+i) for i in range(4))&0xffffffff for g in range(5)]+[0]*5+[0xdeadbeef]*4
  resolved=work/f'{mode}-{n}-resolved.spv';run(f'{mode}-{n}-resolved',[dotnet,harness,path,resolved,f'7={n}']);run(f'{mode}-{n}-resolved-validate',[validator,'--target-env','vulkan1.1',resolved])
  for label,spv,specialize in [('unresolved',direct,True),('roundtrip',roundtrip,True),('resolved',resolved,False)]:
   with Vulkan() as gpu:
    actual=struct.unpack('<14I',gpu.execute(spv.read_bytes(),struct.pack('<20I',*inputs),56,5,{7:struct.pack('<I',n)} if specialize else None))
    errors=[dict(index=i,expected=e,actual=a) for i,(e,a) in enumerate(zip(expected,actual)) if e!=a]
    records.append(dict(check=f'{mode}-{n}-{label}-gpu',passed=not errors,device=gpu.name,sha256=hashlib.sha256(spv.read_bytes()).hexdigest(),expected=expected,actual=actual,mismatches=errors));print(mode,n,label,len(errors),'mismatches',flush=True)
    (work/'report.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
(work/'report.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
raise SystemExit(0 if all(r['passed'] for r in records) else 1)
