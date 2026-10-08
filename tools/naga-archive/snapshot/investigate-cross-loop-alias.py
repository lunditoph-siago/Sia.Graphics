from pathlib import Path
import hashlib,json,struct,subprocess,sys
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
import vulkan_compute as compute

p=Path('.work/naga-csharp/pointer-cross-buffer/alias-investigation');p.mkdir(exist_ok=True)
source=Path('.work/naga-csharp/pointer-cross-buffer/swap-copy-False-0.spv').read_bytes()
words=list(struct.unpack('<%dI'%(len(source)//4),source)); instructions=[];offset=5
while offset<len(words):
 count=words[offset]>>16;instructions.append((words[offset]&65535,words[offset+1:offset+count]));offset+=count
def encode(items,bound=words[3]):
 result=words[:5];result[3]=bound
 for op,args in items:result += [(len(args)+1)<<16|op,*args]
 return struct.pack('<%dI'%len(result),*result)
variants={'original':source,'normalized':Path('.work/naga-csharp/pointer-cross-buffer/swap-copy-False-0-normalized.spv').read_bytes()}
for label,suffix in [('managed','loop-managed.spv'),('reference','loop-reference.spv')]:
 variants[label]=Path('.work/naga-csharp/pointer-cross-buffer',suffix).read_bytes()
decorations=[(71,[10,20]),(71,[14,20])]
split=next(i for i,(op,a) in enumerate(instructions) if op==21)
variants['aliased-roots']=encode(instructions[:split]+decorations+instructions[split:])
variants['volatile-accesses']=encode([(op,a[:3]+[1] if op==61 else a[:2]+[1] if op==62 else a) for op,a in instructions])

# Record direct output[1], selected-pointer reads, and input a[c] for each iteration.
items=[];next_id=words[3]
def fresh():
 global next_id
 value=next_id;next_id+=1;return value
five=fresh();four=fresh();base=fresh();start=fresh();direct=fresh();before=fresh();after=fresh();input_after=fresh()
for op,a in instructions:
 if op==54 and a[1]==1:items += [(43,[5,five,5]),(43,[5,four,4])]
 if op==61 and a[1]==61:
  items += [(65,[24,direct,14,18,29]),(61,[5,before,direct])]
 items.append((op,a))
 if op==61 and a[1]==73:
  items += [(61,[5,after,direct]),(61,[5,input_after,36]),(132,[5,base,81,five]),(128,[5,start,base,four])]
  for position,value in enumerate([before,61,68,after,input_after]):
   index=start
   if position:
    constant={1:29,2:41,3:57,4:8}[position];index=fresh();items.append((128,[5,index,start,constant]))
   address=fresh();items += [(65,[24,address,14,18,index]),(62,[address,value])]
variants['instrumented']=encode(items,next_id)
rows=[];old_pipeline=compute.PipelineInfo
for label,blob in variants.items():
 path=p/(label+'.spv');path.write_bytes(blob)
 result=subprocess.run(['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.2',str(path)],capture_output=True,text=True,timeout=60)
 assert result.returncode==0,(label,result.stdout+result.stderr)
 for noopt in [False,True]:
  if noopt:
   def pipeline(*args):
    a=list(args);a[2]|=1;return old_pipeline(*a)
   compute.PipelineInfo=pipeline
  else:compute.PipelineInfo=old_pipeline
  with compute.Vulkan(variablePointers=False if label in ('managed','reference') else 'full') as vk:
   for seed in [0,1]:
    flags=[seed&1,seed%4,(seed+1)%4,0,(seed+2)%4,0,0,0]
    av=[11+10*j+100*seed for j in range(4)];bv=[111+10*j+100*seed for j in range(4)]
    data=struct.pack('<17I',*(flags+av+bv+[919]));size=80 if label=='instrumented' else 16
    expected=[0xdeadbeef]*(size//4)
    for iteration in range(3):
     chosen_a=bool(seed&1) if iteration%2==0 else not bool(seed&1);index=seed%4 if chosen_a else (seed+1)%4
     target=av if chosen_a else expected;before=expected[1];old=target[index];target[index]=(old+100)&0xffffffff
     expected[0]=old;after=target[index];expected[1]=after;expected[2]=919
     if label=='instrumented':expected[4+iteration*5:9+iteration*5]=[before,old,after,expected[1],av[seed%4]]
    actual=list(struct.unpack('<%dI'%(size//4),vk.execute(blob,data,size,1)))
    rows.append(dict(label=label,noopt=noopt,seed=seed,actual=actual,expected=expected,matches=actual==expected,device=vk.name,features=vk.features))
compute.PipelineInfo=old_pipeline
digest=hashlib.sha256(Path('.work/naga-csharp/harness/bin/Release/net10.0/Sia.Spirv.Naga.dll').read_bytes()).hexdigest()
report=dict(sha256=digest,source_sha256=hashlib.sha256(source).hexdigest(),rows=rows)
(p/'report.json').write_text(json.dumps(report,indent=2))
print(json.dumps([dict(label=r['label'],noopt=r['noopt'],seed=r['seed'],matches=r['matches']) for r in rows],indent=2))
assert all(r['matches'] for r in rows if r['noopt'] or r['label'] in ('managed','reference'))
assert {(r['label'],r['seed']) for r in rows if not r['matches']}=={('original',0),('normalized',0),('aliased-roots',0),('volatile-accesses',0),('instrumented',1)}
