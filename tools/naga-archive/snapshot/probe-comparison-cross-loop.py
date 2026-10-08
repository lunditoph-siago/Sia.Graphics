from pathlib import Path
import hashlib,json,struct,sys
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
import vulkan_compute as compute
root=Path('.work/naga-csharp/pointer-comparison');name='scalar-loop-self-cross-32-False-32';rows=[]
for label,suffix in [('input',''),('normalized','-normalized'),('roundtrip','-back'),('reference','-reference'),('managed','-managed')]:
 for noopt in [False,True]:
  with compute.Vulkan(variablePointers='full' if label in ('input','normalized') else False) as vk:
   for seed in range(8):
    flag=seed&1;c=seed%4;r=(seed>>1)%4;flags=[flag,c,r,(seed>>1)&1,(seed+2)%4,0,0,0]
    a=[11+10*j+100*seed for j in range(4)];b=[111+10*j+100*seed for j in range(4)]
    data=struct.pack('<17I',*(flags+a+b+[919]));expected=[0xdeadbeef]*4
    for i in range(3):
     choose=bool(flag) if i%2==0 else not bool(flag);old=a[c] if choose else expected[r];expected[:]=[1,0,0,old]
    previous=compute.PipelineInfo
    if noopt:
     def pipeline(*args):
      values=list(args);values[2]|=1;return previous(*values)
     compute.PipelineInfo=pipeline
    try:actual=list(struct.unpack('<4I',vk.execute((root/(name+suffix+'.spv')).read_bytes(),data,16,1)))
    finally:compute.PipelineInfo=previous
    rows.append(dict(label=label,noopt=noopt,seed=seed,actual=actual,expected=expected,matches=actual==expected,device=vk.name))
report=dict(sha256=hashlib.sha256(Path('.work/naga-csharp/harness/bin/Release/net10.0/Sia.Spirv.Naga.dll').read_bytes()).hexdigest(),rows=rows)
(root/'cross-loop-native-difference.json').write_text(json.dumps(report,indent=2))
print('outputs',len(rows),'failed',sum(not r['matches'] for r in rows))
for row in rows:
 if not row['matches']:print(row)
