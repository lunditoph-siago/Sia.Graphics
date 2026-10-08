from pathlib import Path
import subprocess,json
root=Path('.work/naga-csharp/image-atomic');root.mkdir(exist_ok=True);rows=[]
for fmt in ['r32uint','r32sint','r32float','rgba32uint','r64uint']:
 for access in ['read_write','atomic']:
  for op in ['decl','textureLoad','textureStore','textureAtomicAdd','textureAtomicMin']:
   component='u64' if fmt=='r64uint' else 'i32' if fmt=='r32sint' else 'f32' if fmt=='r32float' else 'u32';value=f'{component}(1)'
   args='image,vec2i(0)' if op=='textureLoad' else f'image,vec2i(0),vec4<{component}>({value})' if op=='textureStore' else f'image,vec2i(0),{value}'
   body='' if op=='decl' else f'let value = {op}({args});' if op=='textureLoad' else f'{op}({args});'
   path=root/f'{fmt}-{access}-{op}.wgsl';path.write_text(f'@group(0) @binding(0) var image:texture_storage_2d<{fmt},{access}>; @compute @workgroup_size(1) fn main(){{{body}}}')
   r=subprocess.run(['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe',str(path)],capture_output=True,text=True);rows.append(dict(file=str(path),code=r.returncode,error=r.stderr.strip()))
(root/'admission.json').write_text(json.dumps(rows,indent=2));print(json.dumps(rows,indent=2))
