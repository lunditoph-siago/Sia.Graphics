from pathlib import Path
import hashlib,json,struct,subprocess,sys
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
from vulkan_compute import Vulkan
root=Path('.work/naga-csharp/pointer-arithmetic');rows=[]
if '--resume' in sys.argv:rows=json.loads((root/'checks.json').read_text())
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll']
oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
validator=['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.2']
assembly=Path(harness[1]).with_name('Sia.Spirv.Naga.dll');digest=hashlib.sha256(assembly.read_bytes()).hexdigest()
assert digest=='0b3ed0caf77777b87b80cc424c5a676b0631031f3b03377cf9bb8903b593cc4d'
def save():(root/'checks.json').write_text(json.dumps(rows,indent=2))
def run(name,args):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=60)
 rows.append(dict(check=name,passed=p.returncode==0,code=p.returncode,message=p.stdout+p.stderr,command=list(map(str,args))))
 if p.returncode:save();raise RuntimeError(rows[-1])
def widen_elements(source,destination):
 words=list(struct.unpack('<'+'I'*(len(source.read_bytes())//4),source.read_bytes()));instructions=[];at=5;next_id=words[3]
 while at<len(words):
  count=words[at]>>16;instructions.append(words[at:at+count]);at+=count
 types={i[1]:i[2:] for i in instructions if i[0]&0xffff==21}
 signed=next(t for t,args in types.items() if args==[32,1])
 definitions={i[2]:i[1] for i in instructions if len(i)>2 and i[1] in types}
 result=[]
 for i in instructions:
  if i[0]&0xffff==67 and types[definitions[i[4]]][0]==16:
   result.append([(4<<16)|114,signed,next_id,i[4]]);i=i[:];i[4]=next_id;next_id+=1
  result.append(i)
 words[3]=next_id;data=words[:5]+[word for i in result for word in i]
 destination.write_bytes(struct.pack('<'+'I'*len(data),*data))
for case in json.loads((root/'fixtures.json').read_text()):
 narrow_negative=abs(case['width'])==16 and case['offset'] in ('backward','chained','selected')
 if '--resume' in sys.argv and any(r['check']==case['name']+'-managed-7-input-bytes' for r in rows):
  assert len([r for r in rows if r['check'].startswith(case['name']+'-')])==89+(17 if narrow_negative else 0)
  continue
 rows=[r for r in rows if not r['check'].startswith(case['name']+'-')]
 name=case['name']; original=root/(name+'.spv');normalized=root/(name+'-normalized.spv');back=root/(name+'-back.spv');wgsl=root/(name+'.wgsl')
 reference=root/(name+'-reference.spv');managed=root/(name+'-managed.spv')
 run(name+'-input-native',validator+[original]);run(name+'-normalized-native',validator+[normalized])
 run(name+'-emit',harness+[original,back]);run(name+'-back-native',validator+[back])
 run(name+'-wgsl',harness+[original,wgsl]);run(name+'-reference',oracle+[wgsl,reference]);run(name+'-reference-native',validator+[reference])
 run(name+'-managed',harness+[wgsl,managed]);run(name+'-managed-native',validator+[managed])
 inputs=[]
 for seed in range(8):
  flag=seed&1;c=seed%4;r=(seed>>1)%4
  if case['offset'] in ('backward','chained'):c=1+seed%3
  if case['offset']=='selected':c=1+seed%3;r=1+(seed>>1)%3
  if case['offset']=='selected' and case['mode']=='loop':c=2+seed%2;r=2+(seed>>1)%2
  if case['kind']=='aggregate':c=seed%2;r=(seed>>1)%2
  if case['kind']=='constants':c=2;r=3
  flags=[flag,c,r,(seed>>1)&1,(seed+2)%4,0,0,0]
  a=[11+10*j+100*seed for j in range(4)];b=[111+10*j+100*seed for j in range(4)]
  data=struct.pack('<17I',*(flags+a+b+[919]));output=[0xdeadbeef]*4
  for iteration in range(case['iterations']):
   choose=bool(flag) if iteration%2==0 else not bool(flag)
   left=c-1 if case['offset']=='backward' else 4 if case['offset']=='onepast' else c
   if case['kind']=='aggregate':left=c+(2 if case['offset']=='forward' else 0)
   reference_index=r+(2 if case['kind']=='aggregate' and case['offset']=='forward' else 0)
   index=left if choose else reference_index
   if case['offset']=='selected':index-=1+iteration//2 if case['mode']=='loop' else 1
   memory_index=r if case['offset']=='onepast' else index
   old=a[memory_index]
   output=[int(index==reference_index),int(index!=reference_index),(index-reference_index)&0xffffffff,old]
   if case['kind']=='direct':output[:3]=[0,0,0]
   a[memory_index]=(old+100)&0xffffffff
  flags[0]=1-flag if case['iterations']%2 else flag;flags[1]=3;flags[2]=0
  inputs.append((seed,data,struct.pack('<4I',*output),struct.pack('<17I',*(flags+a+b+[919]))))
 paths=[('input',original),('normalized',normalized),('roundtrip',back),('reference',reference),('managed',managed)]
 narrow_negative=abs(case['width'])==16 and case['offset'] in ('backward','chained','selected')
 if narrow_negative:
  widened=root/(name+'-widened.spv');widen_elements(original,widened);run(name+'-widened-native',validator+[widened]);paths.append(('widened',widened))
 for label,path in paths:
  with Vulkan(variablePointers=label in ('input','normalized','widened')) as vk:
   for seed,data,expected,expected_memory in inputs:
    actual=vk.execute(path.read_bytes(),data,16,1)
    rows.append(dict(check=f'{name}-{label}-{seed}-output',passed=actual==expected,device=vk.name,actual=actual.hex(),expected=expected.hex()))
    if not rows[-1]['passed']:
     if label=='input' and narrow_negative:rows[-1].update(external=True,failure_class='native-16-bit-pointer-element-zero-extension');save()
     else:save();raise RuntimeError(rows[-1])
    actual=vk.execute(path.read_bytes(),data,16,1,returnInput=True)
    rows.append(dict(check=f'{name}-{label}-{seed}-input-bytes',passed=actual==expected_memory,device=vk.name,actual=actual.hex(),expected=expected_memory.hex()))
    if not rows[-1]['passed']:
     if label=='input' and narrow_negative:rows[-1].update(external=True,failure_class='native-16-bit-pointer-element-zero-extension');save()
     else:save();raise RuntimeError(rows[-1])
 save();print(name,'checks recorded',flush=True)
assert digest==hashlib.sha256(assembly.read_bytes()).hexdigest(),'Assembly changed during verification'
(root/'assembly.json').write_text(json.dumps(dict(sha256=digest),indent=2))
print(sum(r['passed'] for r in rows),'/',len(rows),'pointer arithmetic checks passed',flush=True)
