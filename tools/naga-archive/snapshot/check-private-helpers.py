from pathlib import Path
import json,struct,sys,subprocess
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
from vulkan_compute import Vulkan
root=Path('.work/naga-csharp/matrix-helpers');rows=[]
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll']
oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
validator=['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.2']
def save():(root/'private-checks.json').write_text(json.dumps(rows,indent=2))
def run(name,args,external=False):
 r=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=60)
 rows.append(dict(check=name,passed=r.returncode==0,external=external,code=r.returncode,message=r.stdout+r.stderr,command=list(map(str,args))));save()
 if r.returncode and not external:raise RuntimeError(rows[-1])
 return r
source=root/'private.wgsl';managed=root/'private-managed.spv';reference=root/'private-reference.spv';back=root/'private-back.spv';reverse=root/'private-back.wgsl';reverseNative=root/'private-reverse.spv';reverseReference=root/'private-reverse-reference.spv'
run('source-reference',oracle+[source]);run('source-managed',harness+[source,managed]);refResult=run('source-reference-native',oracle+[source,reference],external=True)
if refResult.returncode and 'is not cached!' not in refResult.stderr:raise RuntimeError(rows[-1])
run('native-roundtrip',harness+[managed,back]);run('reverse-wgsl',harness+[managed,reverse]);run('reverse-managed',harness+[reverse,reverseNative]);run('reverse-reference',oracle+[reverse,reverseReference])
control=root/'private-control.wgsl';controlNative=root/'private-control-reference.spv'
control.write_text(source.read_text().split('fn edit(')[0]+'''@compute @workgroup_size(1) fn main(){
let c=inputs[0];let r=inputs[1];let mode=inputs[2];first[c]=91.0;var result:f32;
if mode==0u{result=second[r];}else{second[r]+=1.0;result=first[c]+second[r];}
output[0]=result;output[1]=first[0];output[2]=first[1];output[3]=second[0];output[4]=second[1];}''')
run('control-reference-native',oracle+[control,controlNative])
paths=[('managed',managed),('reference-control',controlNative),('roundtrip',back),('reverse-managed',reverseNative),('reverse-reference',reverseReference)]
if refResult.returncode==0:paths.append(('reference-original',reference))
for name,path in paths:run(name+'-validate',validator+[path])
for c in range(2):
 for r in range(2):
  for mode in range(2):
   data=struct.pack('<3I',c,r,mode);a=[11.,12.];b=[21.,22.];a[c]=91.
   if mode:b[r]+=1
   expected=[91.+b[r] if mode else b[r]]+a+b
   for name,path in paths:
    with Vulkan() as vk:
     output=vk.execute(path.read_bytes(),data,20,1);actual=list(struct.unpack('<5f',output));passed=actual==expected
     rows.append(dict(check=f'{name}-gpu-{c}-{r}-{mode}',passed=passed,device=vk.name,expected=expected,actual=actual));save()
     if not passed:raise RuntimeError(rows[-1])
    with Vulkan() as vk:
     actual=vk.execute(path.read_bytes(),data,20,1,returnInput=True);passed=actual==data
     rows.append(dict(check=f'{name}-bytes-{c}-{r}-{mode}',passed=passed,device=vk.name,expected=data.hex(),actual=actual.hex()));save()
     if not passed:raise RuntimeError(rows[-1])
print(sum(r['passed'] for r in rows),'/',len(rows),'private helper checks passed',flush=True)
print('Reference emitter failures:',sum(not r['passed'] and r.get('external',False) for r in rows),flush=True)
