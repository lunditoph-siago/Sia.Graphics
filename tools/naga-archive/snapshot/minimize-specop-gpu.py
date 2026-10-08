import json,pathlib,subprocess,struct
from vulkan_compute import Vulkan
root=pathlib.Path(__file__).resolve().parents[2];task=root/'.work/naga-csharp';work=task/'specop-probes';assembler=task/'spirv-tools-local/tools/Release/spirv-as.exe'
source=(task/'specop-gpu/int32.spvasm').read_text();prefix,body=source.split('%main = OpFunction');lines=body.splitlines()
fixed={120,122,124};candidates=[int(line.split()[1][4:]) for line in lines if line.startswith('OpStore ') and int(line.split()[1][4:]) not in fixed];report=[]
def fails(selected):
 keep=set(selected)|fixed;filtered=[line for line in lines if not line.startswith('OpStore ') or int(line.split()[1][4:]) in keep]
 text=prefix+'%main = OpFunction'+'\n'.join(filtered);asm=work/'minimize-current.spvasm';spv=asm.with_suffix('.spv');asm.write_text(text,encoding='utf-8')
 subprocess.run(list(map(str,[assembler,'--target-env','spv1.3',asm,'-o',spv])),check=True,capture_output=True)
 with Vulkan() as gpu:words=struct.unpack('<208I',gpu.execute(spv.read_bytes(),bytes(16),208*4,1))
 bad=words[120]!=2147483649 or words[122]!=2147483649
 report.append(dict(stores=sorted(keep),actual=[words[i] for i in sorted(fixed)],failed=bad));print(f'{len(keep)} stores: {bad}',flush=True)
 if bad:(work/'minimal-failure.spvasm').write_text(text,encoding='utf-8');(work/'minimal-failure.spv').write_bytes(spv.read_bytes())
 (work/'minimization.json').write_text(json.dumps(report,indent=2),encoding='utf-8');return bad
n=2
while len(candidates)>=1:
 chunks=[candidates[i*len(candidates)//n:(i+1)*len(candidates)//n] for i in range(n)]
 reduced=False
 for chunk in chunks:
  rest=[x for x in candidates if x not in chunk]
  if fails(rest):candidates=rest;n=max(2,n-1);reduced=True;break
 if not reduced:
  if n>=len(candidates):break
  n=min(len(candidates),n*2)
print('Additional stores required:',candidates,flush=True)
