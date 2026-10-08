from pathlib import Path
import hashlib,json,struct,subprocess,sys
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
from vulkan_compute import Vulkan
root=Path('.work/naga-csharp/pointer-slot-transfer');rows=[]
if '--resume' in sys.argv:rows=json.loads((root/'checks.json').read_text())
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll']
oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
validator=['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.2']
assembly=Path(harness[1]).with_name('Sia.Spirv.Naga.dll')
assemblyHash=hashlib.sha256(assembly.read_bytes()).hexdigest()
if '--resume' in sys.argv:assert json.loads((root/'assembly.json').read_text())['sha256']==assemblyHash,'Cannot resume checks from a different assembly'
def save():(root/'checks.json').write_text(json.dumps(rows,indent=2))
def run(name,args,external=False):
 r=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=60)
 rows.append(dict(check=name,passed=r.returncode==0,external=external,code=r.returncode,message=r.stdout+r.stderr,command=list(map(str,args))));save()
 if r.returncode and not external:raise RuntimeError(rows[-1])
 return r
for case in json.loads((root/'fixtures.json').read_text()):
 name=case['name'];shared=case['kind']=='workgroup'
 if any(r['check']==name+'-managed-bytes-7' and r['passed'] for r in rows):continue
 rows=[r for r in rows if not r['check'].startswith(name+'-')]
 original=root/(name+'.spv');normalized=root/(name+'-normalized.spv');back=root/(name+'-back.spv');wgsl=root/(name+'.wgsl');reference=root/(name+'-reference.spv');managed=root/(name+'-managed.spv')
 run(name+'-normalized-native',validator+[normalized])
 run(name+'-input-native',validator+[original]);run(name+'-emit',harness+[original,back]);run(name+'-back-native',validator+[back])
 if case['slotVolatile']:
  rejection=subprocess.run(list(map(str,harness+[original,wgsl])),capture_output=True,text=True,timeout=60)
  message=rejection.stdout+rejection.stderr
  rows.append(dict(check=name+'-wgsl-expected-rejection',passed=rejection.returncode!=0 and 'volatile memory access' in message,code=rejection.returncode,message=message));save()
  assert rows[-1]['passed']
  paths=[('input',original),('normalized',normalized),('roundtrip',back)]
 else:
  run(name+'-wgsl',harness+[original,wgsl]);run(name+'-reference',oracle+[wgsl,reference])
  refValidation=run(name+'-reference-native',validator+[reference],external=shared)
  if refValidation.returncode and 'VUID-StandaloneSpirv-None-10684' not in refValidation.stderr:raise RuntimeError(rows[-1])
  run(name+'-managed',harness+[wgsl,managed]);run(name+'-managed-native',validator+[managed])
  paths=[('input',original),('normalized',normalized),('roundtrip',back)]+([] if refValidation.returncode else [('reference',reference)])+[('managed',managed)]
 inputs=[]
 for seed in range(8):
  flag=seed&1;second=(seed>>1)&1;c=seed%4;r=(seed+1)%4;third=(seed+2)%4
  flags=[flag,c,r,second,third,0,0,0];a=[11+10*j+100*seed for j in range(4)];b=[111+10*j+100*seed for j in range(4)]
  data=struct.pack('<17I',*(flags+a+b+[919]));chosenA=bool(flag);chosenIndex=c if flag else r
  if case['mode'] in ('nested','hybrid') and not second:chosenA=True;chosenIndex=third
  initialA=chosenA
  for iteration in range(case['iterations']):
   if case['kind'].startswith('mixed'):a[0]+=5
   if case['transfer']=='helper-edit':(a if chosenA else b)[chosenIndex]+=7
   if case['mode']=='swap':chosenA=initialA if iteration%2==0 else not initialA;chosenIndex=c if chosenA else r
   target=a if chosenA or case['kind']=='same' else b
   if case['kind'] in ('array','mixed-array','mixed-struct'):old=target[2];target[:]=[v+100 for v in target]
   else:old=target[chosenIndex];target[chosenIndex]+=100
   expected=[old,old+100,919,0xdeadbeef]
  if case['kind']=='noop':expected=[0xdeadbeef]*4
  else:flags[0]=1-flag if case['iterations']%2 else flag;flags[1]=3;flags[2]=0
  expectedMemory=struct.pack('<17I',*(flags+([11+10*j+100*seed for j in range(4)] if shared else a)+([111+10*j+100*seed for j in range(4)] if shared else b)+[919]))
  inputs.append((seed,data,expected,expectedMemory))
 for label,path in paths:
  pointers=('full' if shared else True) if label in ('input','normalized') else False
  with Vulkan(variablePointers=pointers,vulkanMemory=case['kind']=='atomic' and case['qualified']) as vk:
   for seed,data,expected,expectedMemory in inputs:
    actual=list(struct.unpack('<4I',vk.execute(path.read_bytes(),data,16,1)));passed=actual==expected
    rows.append(dict(check=f'{name}-{label}-gpu-{seed}',passed=passed,device=vk.name,expected=expected,actual=actual))
    if not passed:save();raise RuntimeError(rows[-1])
    actual=vk.execute(path.read_bytes(),data,16,1,returnInput=True);passed=actual==expectedMemory
    rows.append(dict(check=f'{name}-{label}-bytes-{seed}',passed=passed,device=vk.name,expected=expectedMemory.hex(),actual=actual.hex()))
    if not passed:save();raise RuntimeError(rows[-1])
 save();print(name,'checks recorded',flush=True)
print(sum(r['passed'] for r in rows),'/',len(rows),'pointer-slot transfer checks passed',flush=True)
print('Reference emitter native validation failures:',sum(not r['passed'] and r.get('external',False) for r in rows),flush=True)
assert assemblyHash==hashlib.sha256(assembly.read_bytes()).hexdigest(),'Harness assembly changed during verification'
(root/'assembly.json').write_text(json.dumps(dict(sha256=assemblyHash),indent=2))
