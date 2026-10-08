from pathlib import Path
import subprocess,json,struct,hashlib
from vulkan_compute import Vulkan
root=Path('.work/naga-csharp/workgroup-specialization');harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll'];oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe'];validator=['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe'];records=[]
def run(label,args):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=40);records.append(dict(check=label,passed=p.returncode==0,message=p.stdout+p.stderr));(root/'local-size-id-gpu.json').write_text(json.dumps(records,indent=2))
 if p.returncode:raise RuntimeError(label+': '+p.stdout+p.stderr)
source='''@id(7) override width=3u; @group(0) @binding(0) var<storage,read> inputs:array<u32>; @group(0) @binding(1) var<storage,read_write> outputs:array<u32>;
fn write(i:u32){outputs[i]=inputs[i]^i;}
@compute @workgroup_size(width) fn first(@builtin(global_invocation_id) id:vec3u){write(id.x);}
@compute @workgroup_size(width+1u) fn second(@builtin(global_invocation_id) id:vec3u){write(id.x);}'''
path=root/'multi-gpu.wgsl';path.write_text(source);direct=root/'multi-gpu.spv';roundtrip=root/'multi-gpu-back.spv';run('compile',[*harness,path,direct,'unresolved','local-size-id']);run('native',[*validator,'--target-env','vulkan1.3',direct]);run('roundtrip',[*harness,direct,roundtrip,'unresolved','local-size-id']);run('roundtrip-native',[*validator,'--target-env','vulkan1.3',roundtrip])
for width in [4,7]:
 resolved=root/f'multi-{width}-resolved.spv';reference=root/f'multi-{width}-reference.spv';run(f'{width}-resolved',[*harness,path,resolved,f'7={width}']);run(f'{width}-reference',[*oracle,path,reference,f'7={width}'])
 for entry,extra in [('first',0),('second',1)]:
  count=(width+extra)*3;inputs=[(i*0x9e3779b9+0x12345678)&0xffffffff for i in range(count)];expected=[v^i for i,v in enumerate(inputs)]+[0xdeadbeef]*4
  for label,spv,specialize in [('direct',direct,True),('roundtrip',roundtrip,True),('resolved',resolved,False),('reference',reference,False)]:
   run(f'{width}-{entry}-{label}-native',[*validator,'--target-env','vulkan1.3',spv])
   with Vulkan(maintenance4=True) as vk:
    actual=list(struct.unpack('<'+str(len(expected))+'I',vk.execute(spv.read_bytes(),struct.pack('<'+str(len(inputs))+'I',*inputs),len(expected)*4,3,{7:struct.pack('<I',width)} if specialize else None,entry)));errors=[dict(index=i,expected=e,actual=a) for i,(e,a) in enumerate(zip(expected,actual)) if e!=a];records.append(dict(check=f'{width}-{entry}-{label}-gpu',passed=not errors,device=vk.name,sha256=hashlib.sha256(spv.read_bytes()).hexdigest(),expected=expected,actual=actual,mismatches=errors));print(width,entry,label,len(errors),'mismatches',flush=True);(root/'local-size-id-gpu.json').write_text(json.dumps(records,indent=2))
print(sum(r['passed'] for r in records),'/',len(records),'checks passed');raise SystemExit(0 if all(r['passed'] for r in records) else 1)
