from pathlib import Path
import subprocess,json,struct,hashlib
from vulkan_compute import Vulkan
root=Path('.work/naga-csharp/workgroup-specialization');root.mkdir(exist_ok=True);harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll'];oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe'];validator=['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe'];records=[]
def run(label,args,accepted=True):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=40);passed=(p.returncode==0)==accepted;records.append(dict(check=label,passed=passed,code=p.returncode,message=p.stdout+p.stderr));(root/'report.json').write_text(json.dumps(records,indent=2))
 if not passed:raise RuntimeError(label+': '+p.stdout+p.stderr)
source='''@id(7) override width=3u; @id(8) override height=2i;
@group(0) @binding(0) var<storage,read> inputs:array<u32>;
@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;
var<workgroup> cells:array<atomic<u32>,(width+1u)*u32(height)*2u>;
@compute @workgroup_size(width+1u,height,2u) fn main(@builtin(local_invocation_index) lane:u32,@builtin(local_invocation_id) local:vec3u,@builtin(workgroup_id) group:vec3u){
 let size=(width+1u)*u32(height)*2u;let i=group.x*size+lane;let value=inputs[i]^i;atomicStore(&cells[lane],value);workgroupBarrier();
 if lane==0u{var sum=0u;for(var j=0u;j<size;j++){sum+=atomicLoad(&cells[j]);}outputs[group.x]=sum;}
 outputs[3u+i]=value^(local.x<<16u|local.y<<8u|local.z);
}'''
path=root/'source.wgsl';path.write_text(source);direct=root/'direct.spv';roundtrip=root/'roundtrip.spv';wgsl=root/'back.wgsl'
run('direct',[*harness,path,direct]);run('direct-native',[*validator,'--target-env','vulkan1.1',direct]);run('roundtrip',[*harness,direct,roundtrip]);run('roundtrip-native',[*validator,'--target-env','vulkan1.1',roundtrip]);run('wgsl',[*harness,direct,wgsl]);run('reference-wgsl',[*oracle,wgsl])
for width,height in [(3,2),(6,3),(1,1),(15,2)]:
 label=f'{width}-{height}';size=(width+1)*height*2;inputs=[(n*0x9e3779b9+0x12345678)&0xffffffff for n in range(3*size)];values=[v^i for i,v in enumerate(inputs)];expected=[sum(values[g*size:(g+1)*size])&0xffffffff for g in range(3)]+[v^(((i%size)%(width+1))<<16|((i%size)//(width+1)%height)<<8|(i%size)//((width+1)*height)) for i,v in enumerate(values)]+[0xdeadbeef]*4
 resolved=root/(label+'-resolved.spv');reference=root/(label+'-reference.spv');api=f'7={width};8={height}'
 run(label+'-resolve',[*harness,path,resolved,api]);run(label+'-reference',[*oracle,path,reference,api])
 for variant,spv,specialize in [('direct',direct,True),('roundtrip',roundtrip,True),('resolved',resolved,False),('reference',reference,False)]:
  run(label+'-'+variant+'-native',[*validator,'--target-env','vulkan1.1',spv])
  with Vulkan() as vk:
   actual=list(struct.unpack('<'+str(len(expected))+'I',vk.execute(spv.read_bytes(),struct.pack('<'+str(len(inputs))+'I',*inputs),len(expected)*4,3,{7:struct.pack('<I',width),8:struct.pack('<i',height)} if specialize else None)));errors=[dict(index=i,expected=e,actual=a) for i,(e,a) in enumerate(zip(expected,actual)) if e!=a];records.append(dict(check=label+'-'+variant+'-gpu',passed=not errors,device=vk.name,sha256=hashlib.sha256(spv.read_bytes()).hexdigest(),expected=expected,actual=actual,mismatches=errors));print(label,variant,len(errors),'mismatches',flush=True);(root/'report.json').write_text(json.dumps(records,indent=2))
# Per-entry LocalSizeId is independently valid only with the corresponding Vulkan environment.
multi=root/'multi.wgsl';multi.write_text('@id(7) override width=3u; @compute @workgroup_size(width) fn first(){} @compute @workgroup_size(width+1u) fn second(){}');ids=root/'local-size-id.spv';back=root/'local-size-id.wgsl';run('multi-default-rejected',[*harness,multi,root/'multi-invalid.spv'],False);run('multi-ids',[*harness,multi,ids,'unresolved','local-size-id']);run('multi-ids-vulkan13',[*validator,'--target-env','vulkan1.3',ids]);run('multi-ids-vulkan11-rejected',[*validator,'--target-env','vulkan1.1',ids],False);run('multi-ids-wgsl',[*harness,ids,back]);run('multi-ids-reference-wgsl',[*oracle,back]);run('multi-ids-roundtrip',[*harness,ids,root/'local-size-id-back.spv','unresolved','local-size-id']);run('multi-ids-roundtrip-native',[*validator,'--target-env','vulkan1.3',root/'local-size-id-back.spv'])
print(sum(r['passed'] for r in records),'/',len(records),'checks passed');raise SystemExit(0 if all(r['passed'] for r in records) else 1)
