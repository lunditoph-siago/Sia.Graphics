from pathlib import Path
import subprocess,json,hashlib
from vulkan_images import ImageVulkan
root=Path('.work/naga-csharp/image-atomic-gpu');root.mkdir(exist_ok=True);harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll'];oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe'];validator=['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.1'];records=[]
def run(label,args):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=40);records.append(dict(check=label,passed=p.returncode==0,message=p.stdout+p.stderr));(root/'report.json').write_text(json.dumps(records,indent=2))
 if p.returncode:raise RuntimeError(label+': '+p.stdout+p.stderr)
for signed in [False,True]:
 for arrayed in [False,True]:
  label=('signed' if signed else 'unsigned')+('-array' if arrayed else '-2d');fmt='r32sint' if signed else 'r32uint';component='i32' if signed else 'u32';suffix='i' if signed else 'u'
  def coord(index):return f'vec2i({index},0)'+(',1i' if arrayed else '')
  initial=[10,1000,-1000 if signed else 0,-1 if signed else 4294967295,0,0]
  stores=''.join(f'textureStore(image,{coord(n)},vec4<{component}>({value}{suffix}));' for n,value in enumerate(initial))
  value='i32(lane)-8i' if signed else 'lane+1u';mask=f'bitcast<{component}>(1u<<lane)'
  operations=''.join(f'textureAtomic{op}(image,{coord(n)},{val});' for n,(op,val) in enumerate([('Add','value'),('Min','value'),('Max','value'),('And','~mask'),('Or','mask'),('Xor','mask')]))
  loads=''.join(f'outputs[{n}u]=bitcast<u32>(textureLoad(image,{coord(n)}).x);' for n in range(6))
  source=f'@group(0) @binding(0) var image:texture_storage_2d'+('_array' if arrayed else '')+f'<{fmt},atomic>; @group(0) @binding(1) var<storage,read_write> outputs:array<u32>; @compute @workgroup_size(16) fn main(@builtin(local_invocation_index) lane:u32){{if lane==0u{{{stores}}} textureBarrier(); let value={value}; let mask={mask}; {operations} textureBarrier(); if lane==0u{{{loads}}}}}'
  path=root/(label+'.wgsl');path.write_text(source);managed=root/(label+'-managed.spv');reference=root/(label+'-reference.spv');back=root/(label+'-back.wgsl');roundtrip=root/(label+'-roundtrip.spv');fromwgsl=root/(label+'-wgsl-reference.spv')
  for name,args in [('managed',harness+[path,managed]),('reference',oracle+[path,reference]),('back',harness+[managed,back]),('roundtrip',harness+[managed,roundtrip]),('wgsl-reference',oracle+[back,fromwgsl])]:run(label+'-'+name,args)
  expected=[2,(-8)&0xffffffff,7,0xffff0000,0xffff,0xffff] if signed else [146,1,16,0xffff0000,0xffff,0xffff]
  expectedOutput=expected+[0xdeadbeef]*4;expectedImage=[0x13579bdf]*(48 if arrayed else 16);offset=16 if arrayed else 0;expectedImage[offset:offset+6]=expected
  for name,path in [('managed',managed),('reference',reference),('roundtrip',roundtrip),('wgsl-reference',fromwgsl)]:
   run(label+'-'+name+'-native',validator+[path])
   with ImageVulkan() as vk:
    output,image=vk.execute_image(path.read_bytes(),signed,arrayed);mismatches=[dict(resource=r,index=i,expected=e,actual=a) for r,values,expect in [('output',output,expectedOutput),('image',image,expectedImage)] for i,(a,e) in enumerate(zip(values,expect)) if a!=e]
    records.append(dict(check=label+'-'+name+'-gpu',passed=not mismatches,device=vk.name,sha256=hashlib.sha256(path.read_bytes()).hexdigest(),output=output,image=image,expectedOutput=expectedOutput,expectedImage=expectedImage,mismatches=mismatches));print(label,name,len(mismatches),'mismatches',flush=True);(root/'report.json').write_text(json.dumps(records,indent=2))
print(sum(r['passed'] for r in records),'/',len(records),'checks passed',flush=True)
raise SystemExit(0 if all(r['passed'] for r in records) else 1)
