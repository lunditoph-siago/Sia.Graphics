from pathlib import Path
import hashlib,json,re,struct,subprocess
from vulkan_compute import Vulkan
root=Path('.work/naga-csharp/raw-query-state');rows=[]
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll'];oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe'];validator=['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.1']
def save():(root/'checks.json').write_text(json.dumps(rows,indent=2))
def run(name,args):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=60);rows.append(dict(check=name,passed=p.returncode==0,message=p.stdout+p.stderr));save()
 if p.returncode:raise RuntimeError(name+': '+p.stdout+p.stderr)
run('raw-input-native',validator+[root/'input.spv']);run('direct-reference',oracle+[root/'direct.wgsl'])
back=root/'back.wgsl';run('raw-import',harness+[root/'input.spv',back]);run('raw-import-reference',oracle+[back])
for name in ['direct','back']:
 path=root/(name+'.wgsl');ref=root/(name+'-reference.spv');run(name+'-reference-compile',oracle+[path,ref]);run(name+'-reference-native',validator+[ref])
 # Isolate descriptor bookkeeping only; this does not emulate traversal.
 lines=path.read_text().splitlines();removed=[];handles=set()
 for line in lines:
  match=re.fullmatch(r'\s*(?:var|let) (\w+): (?:ptr<function, ray_query>|ray_query|acceleration_structure)(?: = (.*))?;',line)
  global_match=re.fullmatch(r'@group\(0\) @binding\(0\) var (\w+): acceleration_structure;',line)
  if global_match:handles.add(global_match.group(1));removed.append(line);continue
  if match:
   name2,initializer=match.groups()
   if initializer is not None:
    dependencies=re.findall(r'\b[A-Za-z_]\w*\b',initializer);assert len(dependencies)==1 and dependencies[0] in handles,(name2,initializer)
   handles.add(name2);removed.append(line)
  elif re.fullmatch(r'\s*rayQuery(?:Initialize|Terminate)\(.*\);',line):removed.append(line)
 control='\n'.join(line for line in lines if line not in removed and line!='enable wgpu_ray_query;')
 assert not re.search(r'\bray_query\b|\brayQuery\w*\b|\bacceleration_structure\b',control)
 assert all(not re.search(r'\b'+re.escape(handle)+r'\b',control) for handle in handles)
 gpu=root/(name+'-bookkeeping.wgsl');gpu.write_text(control)
 for implementation,command in [('managed',harness),('reference',oracle)]:
  spv=root/(name+'-bookkeeping-'+implementation+'.spv');run(name+'-'+implementation+'-compile',command+[gpu,spv]);run(name+'-'+implementation+'-native',validator+[spv])
  expected=[1,0x3e800000,2,0x3f000000]+[0xdeadbeef]*4
  with Vulkan() as vk:
   actual=list(struct.unpack('<8I',vk.execute(spv.read_bytes(),struct.pack('<I',0),32,1)))
   rows.append(dict(check=name+'-'+implementation+'-bookkeeping-gpu',passed=actual==expected,device=vk.name,sha256=hashlib.sha256(spv.read_bytes()).hexdigest(),expected=expected,actual=actual));save();print(name,implementation,actual==expected,flush=True)
   assert actual==expected
print(sum(r['passed'] for r in rows),'/',len(rows),'checks passed; GPU comparison covers bookkeeping only')
