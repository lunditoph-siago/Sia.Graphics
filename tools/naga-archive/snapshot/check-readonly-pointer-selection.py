from pathlib import Path
import hashlib,json,struct,subprocess
from vulkan_compute import Vulkan

root=Path('.work/naga-csharp/readonly-pointer-selection');rows=[]
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll']
oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
validator=['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.2']
assembly=Path(harness[1]).with_name('Sia.Spirv.Naga.dll');assemblyHash=hashlib.sha256(assembly.read_bytes()).hexdigest()
def save():(root/'checks.json').write_text(json.dumps(rows,indent=2))
def run(name,args):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=60)
 rows.append(dict(check=name,passed=p.returncode==0,code=p.returncode,message=p.stdout+p.stderr,command=list(map(str,args))));save()
 assert p.returncode==0,rows[-1]
for case in json.loads((root/'fixtures.json').read_text()):
 name=case['name'];original=root/(name+'.spv');back=root/(name+'-back.spv');wgsl=root/(name+'.wgsl');reference=root/(name+'-reference.spv');managed=root/(name+'-managed.spv')
 run(name+'-input-native',validator+[original]);run(name+'-emit',harness+[original,back]);run(name+'-back-native',validator+[back])
 run(name+'-wgsl',harness+[original,wgsl]);run(name+'-reference',oracle+[wgsl,reference]);run(name+'-reference-native',validator+[reference])
 run(name+'-managed',harness+[wgsl,managed]);run(name+'-managed-native',validator+[managed])
 inputs=[]
 for seed in range(8):
  flag=seed&1;c=seed%4;r=(seed+1)%4
  a=[11+10*j+100*seed for j in range(4)];b=[111+10*j+100*seed for j in range(4)]
  data=struct.pack('<17I',*([flag,c,r,0,0,0,0,0]+a+b+[919]))
  memory=[0xdeadbeef]*8;memory[0]+=5
  chosenAtomic=bool(flag)==case['atomicFirst'];target=memory if chosenAtomic else a
  old=target[(r if chosenAtomic else c) if case['kind']=='scalar' else 2]
  memory[4]=old;memory[5]=(old+100)&0xffffffff;memory[6]=919
  inputs.append((seed,data,memory))
 for label,path in [('input',original),('roundtrip',back),('reference',reference),('managed',managed)]:
  with Vulkan(variablePointers='full' if label=='input' else False) as vk:
   for seed,data,expected in inputs:
    actual=list(struct.unpack('<8I',vk.execute(path.read_bytes(),data,32,1)))
    rows.append(dict(check=f'{name}-{label}-gpu-{seed}',passed=actual==expected,device=vk.name,expected=expected,actual=actual));save();assert actual==expected,rows[-1]
    actual=vk.execute(path.read_bytes(),data,32,1,returnInput=True)
    rows.append(dict(check=f'{name}-{label}-bytes-{seed}',passed=actual==data,device=vk.name,expected=data.hex(),actual=actual.hex()));save();assert actual==data,rows[-1]
 print(name,'checks recorded',flush=True)
assert assemblyHash==hashlib.sha256(assembly.read_bytes()).hexdigest(),'Harness assembly changed during verification'
(root/'assembly.json').write_text(json.dumps(dict(sha256=assemblyHash),indent=2))
print(sum(r['passed'] for r in rows),'/',len(rows),'read-only selection checks passed',flush=True)
