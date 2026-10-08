from pathlib import Path
import json,struct,subprocess,sys
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
from vulkan_compute import Vulkan,C,S,head,p,u
root=Path('.work/naga-csharp/workgroup-memory');rows=[]
if '--resume' in sys.argv:rows=[r for r in json.loads((root/'checks.json').read_text()) if r['passed']]
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll'];oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
validator='.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe';optimizer='.work/naga-csharp/spirv-tools-local/tools/Release/spirv-opt.exe'
with Vulkan() as device:
 subgroup=S('Subgroup',head+[('size',u),('stages',u),('operations',u),('quad',u)])(1000094000,None,0,0,0,0)
 properties=S('Properties2',head+[('properties',C.c_byte*4096)])(1000059001,C.addressof(subgroup))
 query=device.lib.vkGetPhysicalDeviceProperties2;query.restype=None;query.argtypes=[p,p];query(device.physical,C.byref(properties))
 (root/'subgroup-properties.json').write_text(json.dumps(dict(device=device.name,subgroupSize=subgroup.size,stages=subgroup.stages,operations=subgroup.operations),indent=2))
 print('subgroup size',subgroup.size,flush=True)
def save():(root/'checks.json').write_text(json.dumps(rows,indent=2))
def run(name,args,rejection=None):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=60);message=p.stdout+p.stderr
 passed=p.returncode==0 if rejection is None else p.returncode!=0 and rejection in message
 rows.append(dict(check=name,passed=passed,code=p.returncode,expected_rejection=rejection,message=message,command=list(map(str,args))));save()
 if not passed:raise RuntimeError(name+': '+message)
def native(name,path):run(name,[validator,'--target-env','vulkan1.2',path])
def gpu(name,path,values,expected,vulkan,specialized=False):
 specialization=None
 if specialized:
  words=struct.unpack('<'+'I'*(path.stat().st_size//4),path.read_bytes());position=5;ids=[]
  while position<len(words):
   word=words[position];count=word>>16
   if word&65535==71 and count==4 and words[position+2]==1:ids.append(words[position+3])
   position+=count
  if len(ids)!=1:raise RuntimeError('Expected one retained pipeline constant: '+str(ids))
  specialization={ids[0]:struct.pack('<I',3)}
 with Vulkan(vulkanMemory=vulkan) as device:
  output=device.execute(path.read_bytes(),struct.pack('<'+'I'*len(values),*values),4*len(expected),1,specialization=specialization)
  actual=list(struct.unpack('<'+'I'*len(expected),output));passed=actual==expected
  rows.append(dict(check=name,passed=passed,device=device.name,features=device.features,specializedLength=3 if specialized else None,expected=expected,actual=actual));save()
 if not passed:raise RuntimeError(rows[-1])
for case in json.loads((root/'fixtures.json').read_text()):
 name=case['name'];original=root/(name+'.spv');paths=[]
 completed=name+('-direct-back-gpu-1' if case['volatileAccess'] else '-upgraded-gpu')
 if case['gpu'] and any(r['check']==completed for r in rows):print(name,'already verified',flush=True);continue
 rows=[r for r in rows if not r['check'].startswith(name+'-')]
 for label,vulkan in [('input',False),('converted',True),('direct',True)]:
  input=original if label=='input' else root/(name+'-'+label+'.spv');back=root/(name+'-'+label+'-back.spv');wgsl=root/(name+'-'+label+'.wgsl')
  native(name+'-'+label+'-native',input);run(name+'-'+label+'-emit',harness+[input,back]);native(name+'-'+label+'-back-native',back)
  paths.extend([(label,input,vulkan),(label+'-back',back,vulkan)])
  rejection=('storage-buffer owner' if label=='input' else 'storage-buffer root') if case['volatileAccess'] else None
  run(name+'-'+label+'-wgsl',harness+[input,wgsl],rejection)
  if not rejection:
   reference=root/(name+'-'+label+'-reference.spv');managed=root/(name+'-'+label+'-managed.spv')
   run(name+'-'+label+'-reference',oracle+[wgsl,reference]+(['0=2'] if name.startswith('specialized-') else []))
   run(name+'-'+label+'-reference-native',[validator,'--target-env','vulkan1.2',reference],'VUID-StandaloneSpirv-None-10684')
   run(name+'-'+label+'-managed',harness+[wgsl,managed]);native(name+'-'+label+'-managed-native',managed)
   paths.append((label+'-managed',managed,False))
 if case['gpu']:
  for label,path,vulkan in paths:
   for seed in [0,1]:
    values=[9+seed*19,17+seed*23,2 if seed and name.startswith('specialized-') else seed]
    expected=[0,values[0],values[1],values[0]] if not name.startswith('atomic-') else [0,values[0],values[0]+2,values[1]]
    if name.startswith('multi-'):
     values=[i*3+seed*137+1 for i in range(64)];neighbor=values[1:]+values[:1];expected=neighbor+[0]*64+[v+100 for v in neighbor]
    gpu(name+'-'+label+'-gpu-'+str(seed),path,values,expected,vulkan,seed==1 and name.startswith('specialized-'))
  if not case['volatileAccess']:
   upgraded=root/(name+'-upgraded.spv');run(name+'-upgrade',[optimizer,'--upgrade-memory-model',original,'-o',upgraded]);native(name+'-upgraded-native',upgraded)
   gpu(name+'-upgraded-gpu',upgraded,values,expected,True,name.startswith('specialized-'))
 print(name,'passed',flush=True)
print(sum(r['passed'] for r in rows),'/',len(rows),'workgroup memory checks passed',flush=True)
