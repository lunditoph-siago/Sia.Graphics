from pathlib import Path
import json,struct,subprocess,sys
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
from vulkan_compute import Vulkan
root=Path('.work/naga-csharp/member-memory');rows=[]
only=sys.argv[1] if len(sys.argv)>1 else None
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll'];oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
tools=Path('.work/naga-csharp/spirv-tools-local/tools/Release');validator=tools/'spirv-val.exe'
def save():(root/('checks-'+only+'.json' if only else 'checks.json')).write_text(json.dumps(rows,indent=2))
def run(name,args,rejection=None):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=60);message=p.stdout+p.stderr
 passed=p.returncode==0 if rejection is None else p.returncode!=0 and rejection in message
 rows.append(dict(check=name,passed=passed,code=p.returncode,expected_rejection=rejection,message=message,command=list(map(str,args))));save()
 if not passed:raise RuntimeError(name+': '+message)
def native(name,path):run(name,[validator,'--target-env','vulkan1.2',path])
def gpu(name,path,values,expected,vulkan):
 with Vulkan(vulkanMemory=vulkan) as device:
  output=device.execute(path.read_bytes(),struct.pack('<'+'I'*len(values),*values),4*len(expected),1)
  actual=list(struct.unpack('<'+'I'*len(expected),output));passed=actual==expected
  rows.append(dict(check=name,passed=passed,device=device.name,features=device.features,expected=expected,actual=actual));save()
 if not passed:raise RuntimeError(rows[-1])
for case in json.loads((root/'fixtures.json').read_text()):
 if only and not case['name'].startswith(only):continue
 name=case['name'];models=['vulkan'] if case['coop'] else ['legacy','vulkan']
 for model in models:
  key=name+'-'+model;original=root/(name+('-vulkan' if model=='vulkan' else '')+'.spv');output=root/(key+'-back.spv');wgsl=root/(key+'.wgsl');reference=root/(key+'-reference.spv');managed=root/(key+'-managed.spv')
  native(key+'-input-native',original);run(key+'-emit',harness+[original,output]);native(key+'-back-native',output)
  rejection=case.get('rejection') or ('cannot be duplicated' if case['strong'] else None)
  run(key+'-wgsl',harness+[original,wgsl],rejection)
  gpu_paths=[('input',original),('roundtrip',output)]
  if not rejection:
   run(key+'-reference',oracle+[wgsl,reference])
   if case['coop']:run(key+'-reference-native',[validator,'--target-env','vulkan1.2',reference],'is banned when using the Vulkan memory model')
   else:native(key+'-reference-native',reference);gpu_paths.append(('reference',reference))
   run(key+'-managed',harness+[wgsl,managed]);native(key+'-managed-native',managed);gpu_paths.append(('managed',managed))
  if not case['coop']:
   for label,path in gpu_paths:gpu(key+'-'+label+'-gpu',path,case['values'],case['expected'],model=='vulkan' and label in ['input','roundtrip'])
  if model=='legacy':
   upgraded=root/(key+'-upgraded.spv');run(key+'-upgrade',[tools/'spirv-opt.exe','--upgrade-memory-model',original,'-o',upgraded]);native(key+'-upgrade-native',upgraded)
   gpu(key+'-upgrade-gpu',upgraded,case['values'],case['expected'],True)
 print(name,'passed',flush=True)
print(sum(r['passed'] for r in rows),'/',len(rows),'member/coherent checks passed',flush=True)
