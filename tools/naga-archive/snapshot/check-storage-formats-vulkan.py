from pathlib import Path
import subprocess,json,hashlib,struct
from vulkan_images import ImageVulkan
root=Path('.work/naga-csharp/storage-formats-gpu');root.mkdir(exist_ok=True);task=Path('.work/naga-csharp');harness=['.dotnet/dotnet.exe',str(task/'harness/bin/Release/net10.0/harness.dll')];oracle=[str(task/'oracle-target/release/sia-naga-reference-oracle.exe')];validator=[str(task/'spirv-tools-local/tools/Release/spirv-val.exe'),'--target-env','vulkan1.1'];records=[]
def save(): (root/'report.json').write_text(json.dumps(records,indent=2))
def run(label,args):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=40);records.append(dict(check=label,passed=p.returncode==0,message=p.stdout+p.stderr));save()
 if p.returncode:raise RuntimeError(label+': '+p.stdout+p.stderr)
def word(x):return struct.unpack('<I',struct.pack('<f',x))[0]
for row in json.loads((task/'storage-formats/device-formats.json').read_text()):
 fmt=row['format'];bgra=fmt=='bgra8unorm';available=row.get('supportedWrite',False) if bgra else row['supported']
 if not available:
  records.append(dict(check=fmt+'-gpu',skipped=True,reason='Device format/feature unavailable',device=row['device']));save();continue
 signed=fmt.endswith('sint');unsigned=fmt.endswith('uint');component='i32' if signed else 'u32' if unsigned else 'f32'
 def scalar(n):return f'i32((lane>>{n}u)&1u)*10i-3i' if signed else f'((lane>>{n}u)&1u)+2u' if unsigned else f'f32((lane>>{n}u)&1u)'
 values=','.join(scalar(n) for n in range(4));observed='value' if bgra else 'textureLoad(image,coordinate)';source=f'@group(0) @binding(0) var image:texture_storage_2d<{fmt},{"write" if bgra else "read_write"}>; @group(0) @binding(1) var<storage,read_write> outputs:array<u32>; @compute @workgroup_size(16) fn main(@builtin(local_invocation_index) lane:u32){{let coordinate=vec2i(i32(lane%8u),i32(lane/8u));let value=vec4<{component}>({values}); textureStore(image,coordinate,value); textureBarrier(); let observed={observed};'+''.join(f'outputs[lane*4u+{n}u]=bitcast<u32>(observed[{n}u]);' for n in range(4))+'}'
 path=root/(fmt+'.wgsl');path.write_text(source);managed=root/(fmt+'-managed.spv');reference=root/(fmt+'-reference.spv');run(fmt+'-managed-compile',harness+[path,managed]);run(fmt+'-reference-compile',oracle+[path,reference]);variants=[('managed',managed),('reference',reference)]
 if not bgra:
  back=root/(fmt+'-back.wgsl');roundtrip=root/(fmt+'-roundtrip.spv');fromwgsl=root/(fmt+'-wgsl-reference.spv');run(fmt+'-wgsl',harness+[managed,back]);run(fmt+'-roundtrip',harness+[managed,roundtrip]);run(fmt+'-wgsl-reference',oracle+[back,fromwgsl]);variants += [('roundtrip',roundtrip),('wgsl-reference',fromwgsl)]
 expectedOutput=[];expectedImage=b''
 for lane in range(16):
  values=[(((lane>>n)&1)*10-3) if signed else ((lane>>n)&1)+2 if unsigned else (lane>>n)&1 for n in range(4)]
  loaded=[values[n] if n<row['channels'] else 1 if n==3 else 0 for n in range(4)]
  expectedOutput += [(v&0xffffffff) if signed or unsigned else word(v) for v in loaded]
  if fmt.startswith('rgb10a2'):
   encoded=[v if unsigned else v*(3 if n==3 else 1023) for n,v in enumerate(values)];bits=encoded[0]|encoded[1]<<10|encoded[2]<<20|encoded[3]<<30;expectedImage += struct.pack('<I',bits)
  elif fmt=='rg11b10ufloat':expectedImage += struct.pack('<I',values[0]*960|(values[1]*960)<<11|(values[2]*480)<<22)
  elif bgra:expectedImage += bytes(values[n]*255 for n in [2,1,0,3])
  else:
   for v in values[:row['channels']]:
    bits=row['bits']
    if signed or unsigned:expectedImage += (v&((1<<bits)-1)).to_bytes(bits//8,'little')
    elif fmt.endswith('unorm'):expectedImage += (v*((1<<bits)-1)).to_bytes(bits//8,'little')
    elif fmt.endswith('snorm'):expectedImage += (v*((1<<(bits-1))-1)).to_bytes(bits//8,'little')
    else:expectedImage += struct.pack('<e' if bits==16 else '<f',v)
 expectedOutput += [0xdeadbeef]*4
 for label,spv in variants:
  run(fmt+'-'+label+'-native',validator+[spv])
  with ImageVulkan() as vk:
   actual,image=vk.execute_image(spv.read_bytes(),format=row['vulkan'],bytesPerTexel=row['texel'],outputSize=272,requireAtomic=False,rawImage=True);errors=[dict(resource='buffer',index=n,expected=e,actual=a) for n,(e,a) in enumerate(zip(expectedOutput,actual)) if e!=a]+[dict(resource='image',index=n,expected=e,actual=a) for n,(e,a) in enumerate(zip(expectedImage,image)) if e!=a]
   if len(actual)!=len(expectedOutput) or len(image)!=len(expectedImage):errors.append(dict(resource='length',actual=[len(actual),len(image)],expected=[len(expectedOutput),len(expectedImage)]))
   records.append(dict(check=fmt+'-'+label+'-gpu',passed=not errors,device=vk.name,sha256=hashlib.sha256(spv.read_bytes()).hexdigest(),expectedOutput=expectedOutput,actual=actual,expectedImage=expectedImage.hex(),image=image.hex(),mismatches=errors));save()
 print(fmt,sum(r.get('passed') is False for r in records),'failures so far',flush=True)
print(sum(r.get('passed') is True for r in records),'/',sum('passed' in r for r in records),'checks passed;',sum(r.get('skipped',False) for r in records),'skipped formats',flush=True)
raise SystemExit(0 if all(r.get('passed',True) for r in records) else 1)
