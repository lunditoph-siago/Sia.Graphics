from pathlib import Path
import hashlib,json,struct,subprocess,sys
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
from vulkan_compute import Vulkan
root=Path('.work/naga-csharp/pointer-comparison');rows=[]
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll']
oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
validator=['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.2']
assembly=Path(harness[1]).with_name('Sia.Spirv.Naga.dll');digest=hashlib.sha256(assembly.read_bytes()).hexdigest()
def save():(root/'checks.json').write_text(json.dumps(rows,indent=2))
def run(name,args,external=False):
 r=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=60)
 rows.append(dict(check=name,passed=r.returncode==0,external=external,code=r.returncode,message=r.stdout+r.stderr,command=list(map(str,args))))
 if r.returncode and not external:save();raise RuntimeError(rows[-1])
 return r
for case in json.loads((root/'fixtures.json').read_text()):
 name=case['name'];shared=case['kind']=='workgroup';relation=case['relation']
 original=root/(name+'.spv');normalized=root/(name+'-normalized.spv');back=root/(name+'-back.spv');wgsl=root/(name+'.wgsl')
 reference=root/(name+'-reference.spv');managed=root/(name+'-managed.spv')
 run(name+'-input-native',validator+[original]);run(name+'-normalized-native',validator+[normalized])
 run(name+'-emit',harness+[original,back]);run(name+'-back-native',validator+[back])
 run(name+'-wgsl',harness+[original,wgsl]);ref=run(name+'-reference',oracle+[wgsl,reference],external=abs(case['indexWidth'])!=32 or abs(case['resultWidth'])!=32)
 valid=False
 if ref.returncode==0:
  refval=run(name+'-reference-native',validator+[reference],external=shared);valid=refval.returncode==0
  if not valid and 'VUID-StandaloneSpirv-None-10684' not in refval.stderr:save();raise RuntimeError(rows[-1])
 run(name+'-managed',harness+[wgsl,managed]);run(name+'-managed-native',validator+[managed])
 paths=[('input',original),('normalized',normalized),('roundtrip',back)]+([('reference',reference)] if valid else [])+[('managed',managed)]
 inputs=[]
 for seed in range(8):
  flag=seed&1;c=seed%4;r=2 if relation=='constant' else (seed>>1)%4
  flags=[flag,c,(seed>>1)%4,(seed>>1)&1,(seed+2)%4,0,0,0]
  a=[11+10*j+100*seed for j in range(4)];b=[111+10*j+100*seed for j in range(4)]
  data=struct.pack('<17I',*(flags+a+b+[919]));initial_a=a[:];initial_b=b[:];output=[0xdeadbeef]*4
  for iteration in range(case['iterations']):
   choose=bool(flag) if iteration%2==0 else not bool(flag)
   selected_index=c if choose else r
   selected_root='a' if choose or relation not in ('different-field','self-cross') else ('b' if relation=='different-field' else 'output')
   if relation in ('self','self-cross'):equal=True;delta=0
   elif relation=='cross-workgroup':equal=False;delta=0
   else:
    equal=(selected_root==('b' if relation=='different-field' else 'a') and selected_index==r)
    delta=selected_index-r if relation!='different-field' and case['kind']!='vector' else 0
   delta &= (1<<abs(case['resultWidth']))-1
   delta &= 0xffffffff
   memory={'a':a,'b':b,'output':output}[selected_root];old=memory[selected_index]
   if case['kind']=='atomic':memory[selected_index]=(old+100)&0xffffffff
   output[:]=[int(equal),int(not equal),delta,old]
   if case['kind']!='atomic' and relation!='self-cross':memory[selected_index]=(old+100)&0xffffffff
  flags[0]=1-flag if case['iterations']%2 else flag;flags[1]=3;flags[2]=0
  expected_memory=struct.pack('<17I',*(flags+(initial_a if shared else a)+(initial_b if shared else b)+[919]));inputs.append((seed,data,output,expected_memory))
 for label,path in paths:
  pointers=('full' if shared or relation=='self-cross' else True) if label in ('input','normalized') else False
  with Vulkan(variablePointers=pointers) as vk:
   for seed,data,expected,expected_memory in inputs:
    actual=list(struct.unpack('<4I',vk.execute(path.read_bytes(),data,16,1)))
    rows.append(dict(check=f'{name}-{label}-gpu-{seed}',passed=actual==expected,device=vk.name,actual=actual,expected=expected))
    if not rows[-1]['passed']:
     if label=='input' and shared:
      rows[-1].update(external=True,failure_class='unresolved-original-workgroup-pointer-execution');save()
     else:save();raise RuntimeError(rows[-1])
    actual=vk.execute(path.read_bytes(),data,16,1,returnInput=True)
    rows.append(dict(check=f'{name}-{label}-bytes-{seed}',passed=actual==expected_memory,device=vk.name,actual=actual.hex(),expected=expected_memory.hex()))
    if not rows[-1]['passed']:save();raise RuntimeError(rows[-1])
 save();print(name,'checks recorded',flush=True)
assert digest==hashlib.sha256(assembly.read_bytes()).hexdigest(),'Assembly changed during verification'
(root/'assembly.json').write_text(json.dumps(dict(sha256=digest),indent=2))
print(sum(r['passed'] for r in rows),'/',len(rows),'pointer-comparison checks passed; all differences retained',flush=True)
