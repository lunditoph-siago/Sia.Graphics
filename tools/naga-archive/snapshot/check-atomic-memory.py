from pathlib import Path
import json, struct, subprocess, sys
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
from vulkan_compute import Vulkan
root=Path('.work/naga-csharp/atomic-memory');rows=[]
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll']
oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
validator=Path('.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe')
def save():(root/'checks.json').write_text(json.dumps(rows,indent=2))
def run(name,args,rejection=None):
 result=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=60);log=result.stdout+result.stderr
 passed=result.returncode==0 if rejection is None else result.returncode!=0 and rejection in log
 rows.append(dict(check=name,passed=passed,code=result.returncode,expected_rejection=rejection,message=log));save()
 if not passed:raise RuntimeError(name+': '+log)
def native(name,path):run(name,[validator,'--target-env','vulkan1.2',path])
for case in json.loads((root/'fixtures.json').read_text()):
 name=case['name'];original=root/(name+'.spv');output=root/(name+'-back.spv');back=root/(name+'-back.wgsl');reference=root/(name+'-reference.spv')
 native(name+'-input-native',original);run(name+'-emit',harness+[original,output]);native(name+'-back-native',output)
 rejection='no equivalent WGSL relaxed builtin' if case['semantics'] else None
 run(name+'-wgsl',harness+[original,back],rejection)
 if rejection is None:run(name+'-reference',oracle+[back,reference]);native(name+'-reference-native',reference)
 print(name,'passed',flush=True)

for original in sorted(root.glob('native-image-*.spv')):
 if '-back' in original.stem or '-reference' in original.stem:continue
 name=original.stem;output=root/(name+'-back.spv');back=root/(name+'-back.wgsl')
 native(name+'-native',original);run(name+'-emit',harness+[original,output]);native(name+'-back-native',output)
 rejection='no equivalent WGSL relaxed builtin' if '-2056-' in name else None
 run(name+'-wgsl',harness+[original,back],rejection)
 if rejection is None:run(name+'-reference',oracle+[back])
for mode in range(4):
 name=f'native-aggregate-{mode}';original=root/(name+'.spv');reemitted=root/(name+'-back.spv');back=root/(name+'-back.wgsl');reference=root/(name+'-reference.spv')
 native(name+'-input-native',original);run(name+'-emit',harness+[original,reemitted]);native(name+'-back-native',reemitted)
 run(name+'-read',harness+[original,back]);run(name+'-reference',oracle+[back,reference]);native(name+'-reference-native',reference)
 for label,spirv in [('original',original),('roundtrip',reemitted),('wgsl-reference',reference)]:
  with Vulkan() as gpu:
   actual=list(struct.unpack('<3I',gpu.execute(spirv.read_bytes(),struct.pack('<3I',0,0,0),12,1)));device=gpu.name
  expected=[9 if mode==3 else 10,7,99];passed=actual==expected
  rows.append(dict(check=f'{name}-{label}-gpu',passed=passed,actual=actual,expected=expected,device=device));save()
  if not passed:raise RuntimeError(rows[-1])
 print(name,'GPU passed',flush=True)
sources={
 'compare-storage':'''@group(0) @binding(0) var<storage,read_write> data:atomic<u32>;
 @group(0) @binding(1) var<storage,read_write> output:array<u32>;
 @compute @workgroup_size(1) fn main() {let r=atomicCompareExchangeWeak(&data,9u,7u);output[0]=r.old_value;output[1]=u32(r.exchanged);output[2]=atomicLoad(&data);}''',
 'compare-dynamic':'''@group(0) @binding(0) var<storage,read_write> data:array<atomic<u32>,4>;
 @group(0) @binding(1) var<storage,read_write> output:array<u32>;
 var<private> counter:u32;
 fn next()->u32 {let old=counter;counter++;return old;}
 @compute @workgroup_size(1) fn main() {let r=atomicCompareExchangeWeak(&data[next()],next(),next());output[0]=r.old_value;output[1]=u32(r.exchanged);output[2]=atomicLoad(&data[0]);output[3]=counter;}''',
 'compare-workgroup':'''@group(0) @binding(0) var<storage,read> data:u32;
 @group(0) @binding(1) var<storage,read_write> output:array<u32>;
 var<workgroup> workgroup_value:atomic<u32>;
 @compute @workgroup_size(1) fn main() {atomicStore(&workgroup_value,data);let r=atomicCompareExchangeWeak(&workgroup_value,9u,7u);output[0]=r.old_value;output[1]=u32(r.exchanged);output[2]=atomicLoad(&workgroup_value);}''',
 'compare-continuing':'''@group(0) @binding(0) var<storage,read_write> data:atomic<u32>;
 @group(0) @binding(1) var<storage,read_write> output:array<u32>;
 @compute @workgroup_size(1) fn main() {var n=0u;loop {n++;continuing {let r=atomicCompareExchangeWeak(&data,9u,7u);output[0]=r.old_value;output[1]=u32(r.exchanged);output[2]=atomicLoad(&data);break if n==1u;}}}'''
}
for name,source in sources.items():
 path=root/(name+'.wgsl');path.write_text(source);native_spv=root/(name+'.spv');back=root/(name+'-back.wgsl');reemitted=root/(name+'-back.spv');reference=root/(name+'-reference.spv')
 run(name+'-source-reference',oracle+[path]);run(name+'-compile',harness+[path,native_spv]);native(name+'-native',native_spv)
 run(name+'-read',harness+[native_spv,back]);run(name+'-wgsl-reference',oracle+[back,reference]);native(name+'-reference-native',reference)
 run(name+'-reemit',harness+[native_spv,reemitted]);native(name+'-reemit-native',reemitted)
 inputs=[(struct.pack('<4I',1,0,0,0),[1,1,2,3])] if name=='compare-dynamic' else [(struct.pack('<I',9),[9,1,7]),(struct.pack('<I',12),[12,0,12])]
 for variant,spirv in [('original',native_spv),('roundtrip',reemitted),('wgsl-reference',reference)]:
  for index,(input_data,expected) in enumerate(inputs):
   with Vulkan() as gpu:
    actual=list(struct.unpack('<'+'I'*len(expected),gpu.execute(spirv.read_bytes(),input_data,4*len(expected),1)))
    device=gpu.name
   passed=actual==expected;rows.append(dict(check=f'{name}-{variant}-gpu-{index}',passed=passed,actual=actual,expected=expected,device=device));save()
   if not passed:raise RuntimeError(rows[-1])
 print(name,'GPU passed',flush=True)
print(sum(row['passed'] for row in rows),'/',len(rows),'atomic memory checks passed',flush=True)
