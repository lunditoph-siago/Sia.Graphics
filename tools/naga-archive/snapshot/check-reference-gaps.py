import json, pathlib, subprocess
root = pathlib.Path(__file__).resolve().parents[2]
work = root / '.work/naga-csharp/reference-gaps'; work.mkdir(exist_ok=True)
dotnet = root / '.dotnet/dotnet.exe'
harness = root / '.work/naga-csharp/harness/bin/Release/net10.0/harness.dll'
oracle = root / '.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe'
validator = root / '.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe'
cases = {
 'draw-index': 'enable draw_index; @vertex fn main(@builtin(draw_index) index: u32) -> @builtin(position) vec4f { return vec4f(f32(index), 0.0, 0.0, 1.0); }',
 'modf': '@compute @workgroup_size(1) fn main(@builtin(local_invocation_index) i: u32) { let result = modf(f32(i)); let x = result.fract; }',
 'frexp': '@compute @workgroup_size(1) fn main(@builtin(local_invocation_index) i: u32) { let result = frexp(f32(i)); let x = result.exp; }',
 'array-layers': '@group(0) @binding(0) var image: texture_2d_array<f32>; @compute @workgroup_size(1) fn main() { let layers = textureNumLayers(image); }',
}
records = []
def run(name, step, args):
 p = subprocess.run(list(map(str,args)), capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=30)
 log = p.stdout + p.stderr; (work/f'{name}-{step}.log').write_text(log,encoding='utf-8')
 records.append({'case':name,'step':step,'code':p.returncode,'message':log.splitlines()[0] if log else ''})
 return p.returncode == 0
for name, source in cases.items():
 path=work/f'{name}.wgsl'; path.write_text(source,encoding='utf-8')
 for writer in ('managed','reference'):
  spv=work/f'{name}-{writer}.spv'
  args=[dotnet,harness,path,spv] if writer=='managed' else [oracle,path,spv]
  if run(name,f'{writer}-generate',args):
   run(name,f'{writer}-native',[validator,'--target-env','vulkan1.1',spv])
   run(name,f'{writer}-read',[oracle,spv])
(work/'manifest.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
print(json.dumps(records,indent=2))
