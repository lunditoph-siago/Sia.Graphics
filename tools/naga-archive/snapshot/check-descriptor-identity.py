from pathlib import Path
import hashlib,json,struct,subprocess,sys
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
from vulkan_descriptor_array import Vulkan
root=Path('.work/naga-csharp/pointer-descriptor-identity');rows=[]
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll']
oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
validator=['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.2']
assembly=Path(harness[1]).with_name('Sia.Spirv.Naga.dll');digest=hashlib.sha256(assembly.read_bytes()).hexdigest()
assert digest=='c22ef62a55cbc86718ca8fb3114a1c33eecf871801defe3f4c5a46bf6137ba4a'
def save():(root/'checks.json').write_text(json.dumps(rows,indent=2))
def run(name,args):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=60)
 rows.append(dict(check=name,passed=p.returncode==0,code=p.returncode,message=p.stdout+p.stderr,command=list(map(str,args))))
 if p.returncode:save();raise RuntimeError(rows[-1])
def spec_id(path):
 words=struct.unpack('<'+'I'*(len(path.read_bytes())//4),path.read_bytes());at=5
 while at<len(words):
  count=words[at]>>16
  if words[at]&0xffff==71 and count==4 and words[at+2:at+4]==(1,191):return True
  at+=count
 return False
for path in list(root.glob('independent-*.spv'))+list(root.glob('temporal-*.spv'))+[root/'repeated-call.spv']:
 run(path.stem+'-negative-native',validator+[path])
 p=subprocess.run(harness+[str(path),str(root/'unexpected.wgsl')],capture_output=True,text=True,timeout=60)
 rows.append(dict(check=path.stem+'-expected-diagnostic',passed=p.returncode!=0 and 'SpirvParse' in p.stdout and 'potentially aliased' in p.stdout,code=p.returncode,message=p.stdout+p.stderr))
 if not rows[-1]['passed']:save();raise RuntimeError(rows[-1])
for c in json.loads((root/'fixtures.json').read_text()):
 name=c['name'];original=root/(name+'.spv');normalized=root/(name+'-normalized.spv');back=root/(name+'-back.spv');wgsl=root/(name+'.wgsl')
 reference=root/(name+'-reference.spv');reference_one=root/(name+'-reference-one.spv');managed=root/(name+'-managed.spv');managed_one=root/(name+'-managed-one.spv');special=c['index'].startswith('spec')
 run(name+'-input-native',validator+[original]);run(name+'-normalized-native',validator+[normalized])
 run(name+'-emit',harness+[original,back]);run(name+'-back-native',validator+[back]);run(name+'-wgsl',harness+[original,wgsl])
 run(name+'-reference',oracle+[wgsl,reference]+(['191=0'] if special else []));run(name+'-reference-native',validator+[reference])
 if special:
  run(name+'-reference-one',oracle+[wgsl,reference_one,'191=1']);run(name+'-reference-one-native',validator+[reference_one])
 run(name+'-managed',harness+[wgsl,managed]+(['191=0'] if special else []));run(name+'-managed-native',validator+[managed])
 if special:
  run(name+'-managed-one',harness+[wgsl,managed_one,'191=1']);run(name+'-managed-one-native',validator+[managed_one])
  for label,path in [('input',original),('normalized',normalized),('roundtrip',back)]:
   rows.append(dict(check=name+'-'+label+'-spec-id',passed=spec_id(path)))
   if not rows[-1]['passed']:save();raise RuntimeError(rows[-1])
 for label,path in [('input',original),('normalized',normalized),('roundtrip',back),('reference',reference),('managed',managed)]:
  with Vulkan(variablePointers=(('full' if c['full'] else True) if label in ('input','normalized') else False),descriptorIndexing=True) as vk:
   for alias in [False,True]:
    for seed in range(8):
     flag=seed&1;left=seed%4;right=(seed>>1)%4;descriptor=(seed>>2)&1
     first=[flag,left,right,0,descriptor,0,0,0]+[11+10*j+100*seed for j in range(4)]+[111+10*j+100*seed for j in range(4)]+[919]
     second=[20+j for j in range(8)]+[211+10*j+100*seed for j in range(4)]+[311+10*j+100*seed for j in range(4)]+[1919]
     inputs=[first[:],second[:]];memory=first if alias or descriptor==0 else second;output=[0xdeadbeef]*4
     for iteration in range(c['iterations']):
      choose=bool(flag) if iteration%2==0 else not bool(flag);index=left if choose else right;old=memory[8+index]
      if c['comparison']:output[:]=[int(index==right),int(index!=right),(index-right)&0xffffffff,old]
      else:output[:3]=[old,(old+100)&0xffffffff,919]
      memory[8+index]=(old+100)&0xffffffff
     first[0]=1-flag if c['iterations']%2 else flag;first[1]=3;first[2]=0;first[4]=1-descriptor
     expected=[struct.pack('<4I',*output),struct.pack('<17I',*first),struct.pack('<17I',*(first if alias else second))]
     specialization={191:struct.pack('<I',1)} if special and descriptor==1 and label in ('input','normalized','roundtrip') else None
     shader=(reference_one if label=='reference' else managed_one) if special and descriptor==1 and label in ('reference','managed') else path
     actual=vk.execute(shader.read_bytes(),struct.pack('<17I',*inputs[0]),struct.pack('<17I',*inputs[1]),alias,specialization=specialization)
     for target,a,e in zip(['output','first','second'],actual,expected):
      rows.append(dict(check=f'{name}-{label}-{alias}-{seed}-{target}',passed=a==e,actual=a.hex(),expected=e.hex(),device=vk.name))
      if not rows[-1]['passed']:save();raise RuntimeError(rows[-1])
 save();print(name,'checks recorded',flush=True)
assert digest==hashlib.sha256(assembly.read_bytes()).hexdigest(),'Assembly changed during verification'
(root/'assembly.json').write_text(json.dumps(dict(sha256=digest),indent=2))
print(sum(r['passed'] for r in rows),'/',len(rows),'descriptor identity checks passed',flush=True)
