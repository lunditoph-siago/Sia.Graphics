import base64,hashlib,json,pathlib,struct,subprocess
from vulkan_compute import Vulkan
root=pathlib.Path(__file__).resolve().parents[2];work=root/'.work/naga-csharp/override-array-gpu';work.mkdir(exist_ok=True)
dotnet=root/'.dotnet/dotnet.exe';harness=root/'.work/naga-csharp/harness/bin/Release/net10.0/harness.dll';oracle=root/'.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe'
validator=root/'.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe'
records=[]
for atomic in [False,True]:
 element='atomic<u32>' if atomic else 'u32'
 helper='' if atomic else 'fn total(v:array<u32,count>)->u32 { var sum=0u; for(var j=0u;j<count;j++){sum+=v[j];} return sum; }'
 store='atomicAdd(&values[lane%3u],value);' if atomic else 'values[lane]=value;'
 total='atomicLoad(&values[0])+atomicLoad(&values[1])+atomicLoad(&values[2])' if atomic else 'total(values)'
 last='atomicLoad(&values[n])' if atomic else 'values[n]'
 source=f'''override n=4u; override count=n+1u;
@group(0) @binding(0) var<storage,read> inputs:array<u32>;
@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;
var<workgroup> values:array<{element},count>;
{helper}
@compute @workgroup_size(n) fn main(@builtin(local_invocation_index) lane:u32,@builtin(workgroup_id) group:vec3u) {{
 let value=inputs[group.x*n+lane]^(lane*17u+group.x); {store}
 workgroupBarrier();
 if lane==0u {{ outputs[group.x]={total}; outputs[5u+group.x]={last}; }}
}}'''
 path=work/f'{"atomic" if atomic else "value"}.wgsl';path.write_text(source,encoding='utf-8')
 for n in [3,17,32]:
  inputs=[((j*0x9e3779b9)^0xa5a5a5a5)&0xffffffff for j in range(5*n)]
  expected=[sum(inputs[g*n+lane]^(lane*17+g) for lane in range(n))&0xffffffff for g in range(5)]+[0]*5+[0xdeadbeef]*4
  expected_raw=struct.pack('<%dI'%len(expected),*expected)
  for writer in ['managed','managed-wgsl','reference']:
   name=f'{"atomic" if atomic else "value"}-{n}-{writer}';spv=work/f'{name}.spv'
   input_path=path
   if writer=='managed-wgsl':
    input_path=work/f'{"atomic" if atomic else "value"}-managed.wgsl'
    p=subprocess.run(list(map(str,[dotnet,harness,path,input_path])),capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=30)
    (work/f'{name}-wgsl.log').write_text(p.stdout+p.stderr,encoding='utf-8')
    if p.returncode:raise RuntimeError(f'{name} WGSL generation: {p.stdout+p.stderr}')
   args=[dotnet,harness,input_path,spv,f'n={n}'] if writer.startswith('managed') else [oracle,path,spv,f'n={n}']
   failed=False
   for step,command in [('compile',args),('validate',[validator,'--target-env','vulkan1.1',spv])]:
    p=subprocess.run(list(map(str,command)),capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=30)
    (work/f'{name}-{step}.log').write_text(p.stdout+p.stderr,encoding='utf-8')
    if p.returncode:
     records.append(dict(case=name,passed=False,stage=step,message=p.stdout+p.stderr));failed=True
     print(f'{name}: failed {step}: {(p.stdout+p.stderr).splitlines()[0]}',flush=True)
     break
   if failed:continue
   with Vulkan() as gpu:
    actual=gpu.execute(spv.read_bytes(),struct.pack('<%dI'%len(inputs),*inputs),len(expected_raw),5)
    words=struct.unpack('<%dI'%len(expected),actual);mismatches=[dict(index=i,expected=e,actual=a) for i,(e,a) in enumerate(zip(expected,words)) if e!=a]
    records.append(dict(case=name,passed=not mismatches,device=gpu.name,features=gpu.features,spirv_sha256=hashlib.sha256(spv.read_bytes()).hexdigest(),mismatches=mismatches,expected=base64.b64encode(expected_raw).decode(),actual=base64.b64encode(actual).decode()))
    print(f'{name}: {len(mismatches)} mismatches on {gpu.name}',flush=True)
(work/'report.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
raise SystemExit(0 if all(r['passed'] for r in records) else 1)
