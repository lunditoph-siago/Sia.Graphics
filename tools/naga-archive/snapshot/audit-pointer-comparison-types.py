from pathlib import Path
import hashlib,json,struct,subprocess
root=Path('.work/naga-csharp/pointer-comparison-types');root.mkdir(exist_ok=True)
source=Path('.work/naga-csharp/pointer-comparison/scalar-select-other-32-False-32.spv')
words=list(struct.unpack('<'+'I'*(source.stat().st_size//4),source.read_bytes()));code=[];p=5
while p<len(words):
 n=words[p]>>16;code.append((words[p]&0xffff,words[p+1:p+n]));p+=n
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll'];validator=['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.2']
digest=hashlib.sha256(Path(harness[1]).with_name('Sia.Spirv.Naga.dll').read_bytes()).hexdigest();rows=[]
for opcode in [401,402,403]:
 for stride in [4,8]:
  comparison=next(a for op,a in code if op==opcode);reference=comparison[3]
  access=next(a for op,a in code if op==65 and a[1]==reference);pointer=next(a for op,a in code if op==32 and a[0]==access[0])
  duplicate=words[3];address=duplicate+1;modified=[]
  for op,a in code:
   if op==21 and not any(op2==71 and a2[0]==duplicate for op2,a2 in modified):modified.append((71,[duplicate,6,stride]))
   if op==54 and not any(op2==32 and a2[0]==duplicate for op2,a2 in modified):modified.append((32,[duplicate,*pointer[1:]]))
   if op in [401,402,403] and not any(op2==65 and a2[1]==address for op2,a2 in modified):modified.append((65,[duplicate,address,*access[2:]]))
   modified.append((op,[*a[:3],address] if op==opcode else a))
  out=words[:5];out[3]=address+1
  for op,a in modified:out.extend([((len(a)+1)<<16)|op,*a])
  name=f'{opcode}-stride-{stride}';path=root/(name+'.spv');path.write_bytes(struct.pack('<'+'I'*len(out),*out))
  record=dict(name=name,sha256=digest)
  for label,command in [('native',validator+[str(path)]),('managed',harness+[str(path),str(root/(name+'.wgsl'))])]:
   r=subprocess.run(command,capture_output=True,text=True,timeout=60);record[label]=dict(code=r.returncode,message=r.stdout+r.stderr)
  rows.append(record)
(root/'before.json').write_text(json.dumps(rows,indent=2));print(json.dumps(rows,indent=2))
