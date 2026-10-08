from pathlib import Path
import subprocess,json,struct
root=Path('.work/naga-csharp/image-atomic');harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll'];oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe'];validator=['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.1'];records=[]
def run(label,args,accepted=True):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=40);passed=(p.returncode==0)==accepted;records.append(dict(check=label,passed=passed,code=p.returncode,expected_accept=accepted,message=p.stdout+p.stderr));(root/'checks.json').write_text(json.dumps(records,indent=2))
 if not passed:raise RuntimeError(label+': '+p.stdout+p.stderr)
def verify(label,path,reference=True):
 if reference:run(label+'-reference-input',oracle+[path])
 spv=root/(label+'.spv');back=root/(label+'-back.wgsl');reemitted=root/(label+'-back.spv')
 for name,args in [('compile',harness+[path,spv]),('native',validator+[spv]),('back',harness+[spv,back]),('reference-wgsl',oracle+[back]),('reemit',harness+[spv,reemitted]),('native-reemit',validator+[reemitted])]:run(label+'-'+name,args)
for row in json.loads((root/'admission.json').read_text()):
 path=Path(row['file']);run(path.stem+'-admission',harness+[path,root/(path.stem+'.spv')],row['code']==0)
 if row['code']==0:verify(path.stem,path,False)
for fmt,component in [('r32uint','u32'),('r32sint','i32'),('r64uint','u64')]:
 for dim,coord in [('1d','i32(0)'),('2d','vec2i(0)'),('2d_array','vec2u(0), 0lu'),('3d','vec3i(0)')]:
  ops=['Min','Max'] if fmt=='r64uint' else ['Add','Min','Max','And','Or','Xor'];label=fmt+'-'+dim
  path=root/(label+'.wgsl');path.write_text(f'@group(0) @binding(0) var image:texture_storage_{dim}<{fmt},atomic>; @compute @workgroup_size(1) fn main(){{'+''.join(f'textureAtomic{op}(image,{coord},{component}(1));' for op in ops)+'}')
  verify(label,path)
for width in [16,32,64]:
 for layer in [16,32,64]:
  label=f'width-{width}-layer-{layer}';path=root/(label+'.wgsl');path.write_text(('enable wgpu_int16;' if width==16 or layer==16 else '')+f'@group(0) @binding(0) var image:texture_storage_2d_array<r32uint,atomic>; @compute @workgroup_size(1) fn main(){{textureAtomicAdd(image,vec2<i{width}>(i{width}(0)),u{layer}(0),1u);}}');verify(label,path)
for dynamic in [False,True]:
 label='bindings-'+str(dynamic);path=root/(label+'.wgsl');index='lane' if dynamic else '0u';path.write_text(f'enable wgpu_binding_array; @group(0) @binding(0) var images:binding_array<texture_storage_2d<r32uint,atomic>,2>; @compute @workgroup_size(1) fn main(@builtin(local_invocation_index) lane:u32){{textureAtomicAdd(images[{index}],vec2i(0),1u);}}');verify(label,path)
# A valid SPIR-V atomic result consumed by a scalar instruction cannot be represented by WGSL image atomics.
blob=(root/'managed32.spv').read_bytes();words=list(struct.unpack('<'+str(len(blob)//4)+'I',blob));out=words[:5];next_id=words[3];inserted=False;at=5
while at<len(words):
 n=words[at]>>16;instruction=words[at:at+n];out+=instruction
 if not inserted and (instruction[0]&65535)==234:
  out += [(5<<16)|128,instruction[1],next_id,instruction[2],instruction[2]];inserted=True
 at+=n
assert inserted;out[3]=next_id+1;path=root/'consumed-result.spv';path.write_bytes(struct.pack('<'+str(len(out))+'I',*out));run('consumed-native',validator+[path]);run('consumed-reader',harness+[path,root/'consumed-result.wgsl'],False)
print(f'{sum(r["passed"] for r in records)}/{len(records)} checks passed',flush=True)
