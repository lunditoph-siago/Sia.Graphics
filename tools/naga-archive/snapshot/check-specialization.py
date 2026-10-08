import pathlib, struct, subprocess, json
root = pathlib.Path(__file__).resolve().parents[2]
work = root / '.work/naga-csharp/specialization-checks'; work.mkdir(exist_ok=True)
dotnet = root / '.dotnet/dotnet.exe'
harness = root / '.work/naga-csharp/harness/bin/Release/net10.0/harness.dll'
oracle = root / '.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe'
validator = root / '.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe'
records = []
def run(name, step, args):
 p = subprocess.run(list(map(str,args)),capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=30)
 log=p.stdout+p.stderr; (work/f'{name}-{step}.log').write_text(log,encoding='utf-8')
 records.append({'case':name,'step':step,'code':p.returncode,'message':log.splitlines()[0] if log else ''})
 if p.returncode: raise RuntimeError(f'{name}/{step}: {log}')
source=work/'source.wgsl'; source.write_text('override first = 1u; @id(0) override second = 2u; var<private> output: vec2u; @compute @workgroup_size(1) fn main() { let pair = vec2(first, second); output = pair; }',encoding='utf-8')
original=work/'original.spv'; run('original','generate',[dotnet,harness,source,original])
data=original.read_bytes(); words=list(struct.unpack('<'+'I'*(len(data)//4),data)); instructions=[]; offset=5
while offset<len(words):
 count=words[offset]>>16; instructions.append(words[offset:offset+count]); offset+=count
for name in ('original','composite','no-id'):
 code=[list(i) for i in instructions]
 if name=='composite':
  index=next(n for n,i in enumerate(code) if i[0]&65535==80); composite=code.pop(index); composite[0]=(composite[0]&0xffff0000)|51
  code.insert(next(n for n,i in enumerate(code) if i[0]&65535==54),composite)
 elif name=='no-id': code=[i for i in code if not(i[0]&65535==71 and i[2]==1)]
 binary=work/f'{name}.spv'; flat=words[:5]+[w for i in code for w in i]; binary.write_bytes(struct.pack('<'+'I'*len(flat),*flat))
 run(name,'native',[validator,'--target-env','vulkan1.1',binary])
 wgsl=work/f'{name}.wgsl'; run(name,'managed-wgsl',[dotnet,harness,binary,wgsl]); run(name,'reference-wgsl',[oracle,wgsl])
 output=work/f'{name}-managed.spv'; run(name,'managed-spv',[dotnet,harness,binary,output]); run(name,'managed-native',[validator,'--target-env','vulkan1.1',output])
(work/'manifest.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
print(f'{len(records)} checks passed')
