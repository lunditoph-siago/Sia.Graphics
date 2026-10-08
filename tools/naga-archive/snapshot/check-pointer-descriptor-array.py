from pathlib import Path
import hashlib,json,struct,subprocess,sys
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
from vulkan_descriptor_array import Vulkan
root=Path('.work/naga-csharp/pointer-descriptor-array');rows=[]
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
 name=case['name'];original=root/(name+'.spv');normalized=root/(name+'-normalized.spv');back=root/(name+'-back.spv');wgsl=root/(name+'.wgsl')
 reference=root/(name+'-reference.spv');managed=root/(name+'-managed.spv')
 run(name+'-input-native',validator+[original]);run(name+'-normalized-native',validator+[normalized])
 run(name+'-emit',harness+[original,back]);run(name+'-back-native',validator+[back])
 run(name+'-wgsl',harness+[original,wgsl]);run(name+'-reference',oracle+[wgsl,reference]);run(name+'-reference-native',validator+[reference])
 run(name+'-managed',harness+[wgsl,managed]);run(name+'-managed-native',validator+[managed])
 for label,path in [('input',original),('normalized',normalized),('roundtrip',back),('reference',reference),('managed',managed)]:
  with Vulkan(variablePointers=(True if case['kind']=='descriptor-same' else 'full') if label in ('input','normalized') else False,descriptorIndexing='-dynamic' in case['kind']) as vk:
   for alias in [False,True]:
    for seed in range(8):
     flag=seed&1;c=seed%4;r=(seed>>1)%4
     descriptor=(seed>>2)&1
     first=[flag,c,r,0,descriptor if '-dynamic' in case['kind'] else 0,0,0,0]+[11+10*j+100*seed for j in range(4)]+[111+10*j+100*seed for j in range(4)]+[919]
     second=[20+j for j in range(8)]+[211+10*j+100*seed for j in range(4)]+[311+10*j+100*seed for j in range(4)]+[1919]
     inputs=[first[:],second[:]];other=first if alias else second;output=[0xdeadbeef]*4
     for iteration in range(case['iterations']):
      choose=bool(flag) if iteration%2==0 else not bool(flag);index=c if choose else r
      memory=first if choose or case['kind']=='descriptor-same' or '-dynamic' in case['kind'] and descriptor==0 else other;old=memory[8+index]
      if case['operation']=='memory':output[:3]=[old,(old+100)&0xffffffff,919]
      else:
       equal=True if case['kind']=='descriptor' else index==r;delta=0 if case['kind']=='descriptor' else (index-r)&0xffffffff
       output[:]=[int(equal),int(not equal),delta,old]
      memory[8+index]=(old+100)&0xffffffff
     first[0]=1-flag if case['iterations']%2 else flag;first[1]=3;first[2]=0
     if '-dynamic' in case['kind']:first[4]=1-descriptor if case['iterations']%2 else descriptor
     expected=[struct.pack('<4I',*output),struct.pack('<17I',*first),struct.pack('<17I',*(first if alias else second))]
     actual=vk.execute(path.read_bytes(),struct.pack('<17I',*inputs[0]),struct.pack('<17I',*inputs[1]),alias)
     for target,a,e in zip(['output','first','second'],actual,expected):
      rows.append(dict(check=f'{name}-{label}-{alias}-{seed}-{target}',passed=a==e,actual=a.hex(),expected=e.hex(),device=vk.name))
      if not rows[-1]['passed']:save();raise RuntimeError(rows[-1])
 save();print(name,'checks recorded',flush=True)
assert digest==hashlib.sha256(assembly.read_bytes()).hexdigest(),'Assembly changed during verification'
(root/'assembly.json').write_text(json.dumps(dict(sha256=digest),indent=2))
print(sum(r['passed'] for r in rows),'/',len(rows),'descriptor-array checks passed',flush=True)
