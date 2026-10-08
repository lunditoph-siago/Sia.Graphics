from pathlib import Path
import subprocess,json
root=Path('.work/naga-csharp/image-atomic');rows=[]
cases={
'buffer-atomic-access':'@group(0) @binding(0) var<storage,atomic> data:atomic<u32>; @compute @workgroup_size(1) fn main(){atomicAdd(&data,1u);}',
'abstract-coordinates':'@group(0) @binding(0) var image:texture_storage_2d<r32uint,atomic>; @compute @workgroup_size(1) fn main(){textureAtomicAdd(image,vec2(0),1u);}',
'abstract-value':'@group(0) @binding(0) var image:texture_storage_2d<r32uint,atomic>; @compute @workgroup_size(1) fn main(){textureAtomicAdd(image,vec2i(0),1);}',
'parameter':'@group(0) @binding(0) var image:texture_storage_2d<r32uint,atomic>; fn f(t:texture_storage_2d<r32uint,atomic>){textureAtomicAdd(t,vec2i(0),1u);} @compute @workgroup_size(1) fn main(){f(image);}'
}
for label,source in cases.items():
 path=root/f'{label}.wgsl';path.write_text(source);p=subprocess.run(['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe',str(path)],capture_output=True,text=True);rows.append(dict(case=label,code=p.returncode,error=p.stderr.strip()))
print(json.dumps(rows,indent=2));(root/'extra-admission.json').write_text(json.dumps(rows,indent=2))
