from pathlib import Path
import json,struct,subprocess,sys
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
from vulkan_compute import Vulkan
root=Path('.work/naga-csharp/access-memory');rows=[]
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll'];oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
validator='.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe'
def save():(root/'checks.json').write_text(json.dumps(rows,indent=2))
def run(name,args,rejection=None):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=60);message=p.stdout+p.stderr
 passed=p.returncode==0 if rejection is None else p.returncode!=0 and rejection in message
 rows.append(dict(check=name,passed=passed,code=p.returncode,expected_rejection=rejection,message=message,command=list(map(str,args))));save()
 if not passed:raise RuntimeError(name+': '+message)
def native(name,path):run(name,[validator,'--target-env','vulkan1.2',path])
def gpu(name,path,values,expected,vulkan=False):
 with Vulkan(vulkanMemory=vulkan) as device:
  output=device.execute(path.read_bytes(),struct.pack('<'+'I'*len(values),*values),4*len(expected),1)
  actual=list(struct.unpack('<'+'I'*len(expected),output));passed=actual==expected
  rows.append(dict(check=name,passed=passed,device=device.name,features=device.features,expected=expected,actual=actual));save()
 if not passed:raise RuntimeError(rows[-1])
for case in json.loads((root/'fixtures.json').read_text()):
 name=case['name'];original=root/(name+'.spv');output=root/(name+'-back.spv');back=root/(name+'-back.wgsl');reference=root/(name+'-reference.spv')
 if case.get('invalid'):
  run(name+'-input-native',[validator,'--target-env','vulkan1.2',original],'is banned when using the Vulkan memory model')
  run(name+'-emit',harness+[original,output],'banned with the Vulkan memory model')
  print(name,'rejected as expected',flush=True);continue
 native(name+'-input-native',original);run(name+'-emit',harness+[original,output]);native(name+'-back-native',output)
 run(name+'-wgsl',harness+[original,back],case['rejection'])
 if case['rejection'] is None:
  run(name+'-reference',oracle+[back,reference])
  if name in ['coop-1','coop-7']:run(name+'-reference-native',[validator,'--target-env','vulkan1.2',reference],'is banned when using the Vulkan memory model')
  else:native(name+'-reference-native',reference)
  managed=root/(name+'-wgsl-managed.spv');run(name+'-wgsl-managed',harness+[back,managed]);native(name+'-wgsl-managed-native',managed)
 if not case['coop'] and not name.startswith('global-volatile') and not name.startswith('strong-volatile'):
  if case['aggr']:
   values=[0,0,0];expected=[9 if name=='aggregate-3' else 10,7,99]
  else:values=[1,2,3,4];expected=values if case['copy'] else [8,9,10,11]
  paths=[('original',original),('roundtrip',output)]
  if case['rejection'] is None:paths += [('wgsl-reference',reference),('wgsl-managed',managed)]
  for label,path in paths:gpu(name+'-'+label+'-gpu',path,values,expected,case['vulkan'] and label in ['original','roundtrip'])
 elif name.startswith('global-volatile'):
  for label,path in [('original',original),('roundtrip',output),('wgsl-reference',reference),('wgsl-managed',managed)]:gpu(name+'-'+label+'-gpu',path,[9],[9,10],label in ['original','roundtrip'])
 elif name.startswith('strong-volatile'):
  for label,path in [('original',original),('roundtrip',output)]:gpu(name+'-'+label+'-gpu',path,[9],[9],case['vulkan'])
 print(name,'passed',flush=True)
print(sum(r['passed'] for r in rows),'/',len(rows),'per-access memory checks passed',flush=True)
