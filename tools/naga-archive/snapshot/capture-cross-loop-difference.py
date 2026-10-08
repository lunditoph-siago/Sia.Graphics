from pathlib import Path
import hashlib,json,struct,subprocess,sys
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
from vulkan_compute import Vulkan
p=Path('.work/naga-csharp/pointer-cross-buffer'); name='swap-copy-False-0'
h=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll']
o=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
v=['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.2']
rows=[]
def run(label,args):
 r=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=60)
 rows.append(dict(check=label,code=r.returncode,message=r.stdout+r.stderr)); assert r.returncode==0,rows[-1]
original=p/(name+'.spv'); normalized=p/(name+'-normalized.spv'); back=p/'loop-managed.spv'; wgsl=p/'loop-managed.wgsl'; reference=p/'loop-reference.spv'
run('managed-native',h+[original,back]); run('managed-wgsl',h+[original,wgsl]); run('reference-native',o+[wgsl,reference])
for label,path in [('original',original),('normalized',normalized),('managed',back),('reference',reference)]:
 run(label+'-validation',v+[path])
 with Vulkan(variablePointers='full' if label in ('original','normalized') else False) as vk:
  for seed in [0,1]:
   flag=seed&1; c=seed%4; r=(seed+1)%4
   flags=[flag,c,r,0,(seed+2)%4,0,0,0]; a=[11+10*j+100*seed for j in range(4)]; b=[111+10*j+100*seed for j in range(4)]
   data=struct.pack('<17I',*(flags+a+b+[919])); expected=[0xdeadbeef]*4
   for iteration in range(3):
    chosen_a=bool(flag) if iteration%2==0 else not bool(flag); index=c if chosen_a else r; target=a if chosen_a else expected
    old=target[index]; target[index]=(old+100)&0xffffffff; expected[0]=old; expected[1]=target[index]; expected[2]=919
   actual=list(struct.unpack('<4I',vk.execute(path.read_bytes(),data,16,1)))
   rows.append(dict(check=label+'-output-'+str(seed),matches=actual==expected,actual=actual,expected=expected,device=vk.name))
   flags[0]=1-flag; flags[1]=3; flags[2]=0
   expected_bytes=struct.pack('<17I',*(flags+a+b+[919])); actual_bytes=vk.execute(path.read_bytes(),data,16,1,returnInput=True)
   rows.append(dict(check=label+'-input-'+str(seed),matches=actual_bytes==expected_bytes,actual=actual_bytes.hex(),expected=expected_bytes.hex(),device=vk.name))
report=dict(sha256=hashlib.sha256(Path(h[1]).with_name('Sia.Spirv.Naga.dll').read_bytes()).hexdigest(),rows=rows,
            status='Unresolved native execution difference; no driver/translator root cause established.')
(p/'full-capability-loop-difference.json').write_text(json.dumps(report,indent=2))
print(json.dumps(rows,indent=2))
assert all(r.get('matches',True) for r in rows if r['check'].startswith(('managed','reference')))
