from pathlib import Path
import subprocess,json
root=Path('.work/naga-csharp/float-atomic');root.mkdir(exist_ok=True)
rows=[]
for space in ['storage,read_write','workgroup']:
 for op in ['decl','atomicLoad','atomicStore','atomicAdd','atomicSub','atomicExchange','atomicMin','atomicCompareExchangeWeak']:
  prefix='@group(0) @binding(0) ' if space.startswith('storage') else ''
  args='&a' if op=='atomicLoad' else '&a, 1.5, 2.0' if op=='atomicCompareExchangeWeak' else '&a, 1.5'
  body='' if op=='decl' else f'{op}({args});'
  path=root/(space.split(',')[0]+'-'+op+'.wgsl');path.write_text(f'{prefix}var<{space}> a:atomic<f32>; @compute @workgroup_size(1) fn main() {{ {body} }}')
  r=subprocess.run(['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe',str(path)],capture_output=True,text=True)
  rows.append(dict(space=space,op=op,code=r.returncode,error=r.stderr.strip()))
print(json.dumps(rows,indent=2));(root/'admission.json').write_text(json.dumps(rows,indent=2))
