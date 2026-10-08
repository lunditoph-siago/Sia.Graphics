from pathlib import Path
import json,struct,subprocess,sys
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
from vulkan_compute import Vulkan
root=Path('.work/naga-csharp/barrier-memory');rows=[]
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll'];oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
validator='.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe'
def save():(root/'checks.json').write_text(json.dumps(rows,indent=2))
def run(name,args,rejection=None):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=60);message=p.stdout+p.stderr
 passed=p.returncode==0 if rejection is None else p.returncode!=0 and rejection in message
 rows.append(dict(check=name,passed=passed,code=p.returncode,expected_rejection=rejection,message=message,command=list(map(str,args))));save()
 if not passed:raise RuntimeError(name+': '+message)
def native(name,path):run(name,[validator,'--target-env','vulkan1.2',path])
def gpu(name,path,size,expected):
 with Vulkan() as device:
  output=device.execute(path.read_bytes(),struct.pack('<4I',1,2,3,4),4*size,1)
  actual=list(struct.unpack('<'+'I'*size,output));passed=actual==expected
  rows.append(dict(check=name,passed=passed,device=device.name,expected=expected,actual=actual));save()
 if not passed:raise RuntimeError(rows[-1])
for case in json.loads((root/'fixtures.json').read_text()):
 name=case['name'];original=root/(name+'.spv');output=root/(name+'-back.spv');back=root/(name+'-back.wgsl');reference=root/(name+'-reference.spv')
 native(name+'-input-native',original);run(name+'-emit',harness+[original,output]);native(name+'-back-native',output)
 run(name+'-wgsl',harness+[original,back],case['rejection'])
 reference_valid=case['rejection'] is None and case['execution']!=3
 if case['rejection'] is None:
  run(name+'-reference',oracle+[back,reference])
  if reference_valid:native(name+'-reference-native',reference)
  else:
   run(name+'-reference-native',[validator,'--target-env','vulkan1.2',reference],'VUID-StandaloneSpirv-OpControlBarrier-04650')
   managed=root/(name+'-wgsl-managed.spv');run(name+'-wgsl-managed',harness+[back,managed]);native(name+'-wgsl-managed-native',managed)
 if not case['vulkan'] and case['semantics']!=272:
  size=case['size'];expected=list(range(8,8+size))
  gpu(name+'-original-gpu',original,size,expected);gpu(name+'-roundtrip-gpu',output,size,expected)
  if reference_valid:gpu(name+'-wgsl-reference-gpu',reference,size,expected)
  elif case['rejection'] is None:gpu(name+'-wgsl-managed-gpu',managed,size,expected)
 print(name,'passed',flush=True)

# Four invocations exchange workgroup values across a collective barrier.
source='''@group(0) @binding(0) var<storage,read> input_values:array<u32>;
 @group(0) @binding(1) var<storage,read_write> output_values:array<u32>;
 var<workgroup> exchanged:array<u32,4>;
 @compute @workgroup_size(4) fn main(@builtin(local_invocation_index) index:u32) {
 exchanged[index]=input_values[index];workgroupBarrier();output_values[index]=exchanged[(index+1u)%4u];}'''
path=root/'exchange.wgsl';path.write_text(source);original=root/'exchange.spv';output=root/'exchange-back.spv';back=root/'exchange-back.wgsl';reference=root/'exchange-reference.spv'
run('exchange-source-reference',oracle+[path]);run('exchange-compile',harness+[path,original]);native('exchange-native',original)
run('exchange-read',harness+[original,back]);run('exchange-reference',oracle+[back,reference])
run('exchange-reference-native',[validator,'--target-env','vulkan1.2',reference],'VUID-StandaloneSpirv-None-10684')
managed=root/'exchange-wgsl-managed.spv';run('exchange-wgsl-managed',harness+[back,managed]);native('exchange-wgsl-managed-native',managed)
run('exchange-reemit',harness+[original,output]);native('exchange-reemit-native',output)
for label,p in [('original',original),('roundtrip',output),('wgsl-managed',managed)]:gpu('exchange-'+label+'-gpu',p,4,[2,3,4,1])
for stage in ['vertex','fragment']:
 original=root/(stage+'-fence.spv');output=root/(stage+'-fence-back.spv');back=root/(stage+'-fence.wgsl')
 native(stage+'-input-native',original);run(stage+'-emit',harness+[original,output]);native(stage+'-back-native',output)
 run(stage+'-wgsl',harness+[original,back],'uniformity-proven')
print(sum(r['passed'] for r in rows),'/',len(rows),'barrier memory checks passed',flush=True)
