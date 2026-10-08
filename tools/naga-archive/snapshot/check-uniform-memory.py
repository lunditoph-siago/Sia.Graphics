from pathlib import Path
import json,struct,subprocess,sys
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
from vulkan_compute import Vulkan
root=Path('.work/naga-csharp/uniform-memory');rows=[]
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll'];oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
validator='.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe'
def save():(root/'checks.json').write_text(json.dumps(rows,indent=2))
def run(name,args,rejection=None):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=60);message=p.stdout+p.stderr
 passed=p.returncode==0 if rejection is None else p.returncode!=0 and rejection in message
 rows.append(dict(check=name,passed=passed,code=p.returncode,expected_rejection=rejection,message=message,command=list(map(str,args))));save()
 if not passed:raise RuntimeError(name+': '+message)
def native(name,path):run(name,[validator,'--target-env','vulkan1.2',path])
def gpu(name,path,data,expected,vulkan=False,uniform=True):
 with Vulkan(vulkanMemory=vulkan) as device:
  output=device.execute(path.read_bytes(),data,len(expected)*4,1,inputDescriptorType=6 if uniform else 7)
  actual=list(struct.unpack('<'+'f'*len(expected),output));passed=actual==expected
  rows.append(dict(check=name,passed=passed,device=device.name,features=device.features,input=data.hex(),expected=expected,actual=actual));save()
 if not passed:raise RuntimeError(rows[-1])
def scalar(index=1):return struct.pack('<2I6f',index,0,2,3,5,7,11,0)
def probes(case):
 name=case['name']
 if name.startswith('shape-'):
  _,c,r,half=name.split('-');c=int(c);r=int(r);w=2 if half=='True' else 4;align=w*(2 if r==2 else 4);offset=(4+align-1)//align*align
  stride=align;end=offset+c*stride+4;size=(end+align-1)//align*align;data=bytearray(size);struct.pack_into('<I',data,0,c-1)
  for column in range(c):
   for row in range(r):struct.pack_into('<e' if w==2 else '<f',data,offset+column*stride+row*w,float(column*r+row+1))
  struct.pack_into('<f',data,offset+c*stride,99);return [(bytes(data),[float(c*r)])]
 if name=='whole-array':return [(struct.pack('<8f',2,3,5,7,11,13,17,19),[19.])]
 if name=='AliasSource':return [(scalar(0),[3.,11.]),(scalar(1),[7.,11.])]
 if name=='NestedSource':
  values=[2.,3.,5.,7.,11.,13.,17.,0.,19.,23.,29.,31.,37.,41.,43.,0.]
  return [(struct.pack('<4I16f',0,2,1,0,*values),[13.,17.,1.]),(struct.pack('<4I16f',1,1,0,0,*values),[29.,43.,1.])]
 if name.startswith('leaf-') and name.endswith('-True'):return [(scalar(0),[3.]),(scalar(1),[7.]),(scalar(2),[0.])]
 return [(scalar(),[7.])]
for case in json.loads((root/'fixtures.json').read_text()):
 name=case['name'];original=root/(name+'.spv');back=root/(name+'-back.spv');wgsl=root/(name+'-back.wgsl');reference=root/(name+'-reference.spv');managed=root/(name+'-managed.spv')
 native(name+'-input-native',original);run(name+'-emit',harness+[original,back]);native(name+'-back-native',back)
 run(name+'-wgsl',harness+[original,wgsl],case['rejection']);paths=[('input',original),('roundtrip',back)]
 if case['rejection'] is None:
  run(name+'-reference',oracle+[wgsl,reference])
  if name.startswith('binding-'):run(name+'-reference-native',[validator,'--target-env','vulkan1.2',reference],'VUID-StandaloneSpirv-Uniform-06676')
  else:native(name+'-reference-native',reference)
  run(name+'-managed',harness+[wgsl,managed]);native(name+'-managed-native',managed);paths += [('reference',reference),('managed',managed)]
 if case['gpu']:
  for label,path in paths:
   for index,(data,expected) in enumerate(probes(case)):gpu(name+'-'+label+'-gpu-'+str(index),path,data,expected,case['vulkan'] and label in ['input','roundtrip'])
 source=root/(name+'.wgsl')
 if source.exists():
  original_reference=root/(name+'-original-reference.spv');run(name+'-original-reference',oracle+[source,original_reference])
  if name.startswith('binding-'):run(name+'-original-reference-native',[validator,'--target-env','vulkan1.2',original_reference],'OpAccessChain result type (OpTypeFloat) does not match')
  else:native(name+'-original-reference-native',original_reference)
  if case['gpu']:
   for index,(data,expected) in enumerate(probes(case)):gpu(name+'-original-reference-gpu-'+str(index),original_reference,data,expected)
 print(name,'passed',flush=True)
original=root/'unsupported-stride.spv';native('unsupported-stride-native',original)
run('explicit-stride-managed',harness+[original,root/'unsupported-stride-back.spv'])
native('explicit-stride-managed-native',root/'unsupported-stride-back.spv')
run('explicit-stride-wgsl',harness+[original,root/'explicit-stride.wgsl'])
run('explicit-stride-reference-wgsl',oracle+[root/'explicit-stride.wgsl',root/'explicit-stride-reference.spv'])
native('explicit-stride-reference-native',root/'explicit-stride-reference.spv')
run('unsupported-stride-reference',oracle+[original],'UnsupportedMatrixStride')
gpu('unsupported-stride-original-gpu',original,struct.pack('<12f',2,3,0,0,5,7,0,0,11,0,0,0),[7.],uniform=False)
for label,path in [('managed',root/'unsupported-stride-back.spv'),('reference',root/'explicit-stride-reference.spv')]:
 gpu('explicit-stride-'+label+'-gpu',path,struct.pack('<12f',2,3,0,0,5,7,0,0,11,0,0,0),[7.],uniform=False)
print(sum(r['passed'] for r in rows),'/',len(rows),'uniform memory checks passed',flush=True)
