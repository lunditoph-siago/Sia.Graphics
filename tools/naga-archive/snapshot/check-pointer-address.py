from pathlib import Path
import hashlib,json,re,struct,subprocess
from vulkan_compute import Vulkan

root=Path('.work/naga-csharp/pointer-address');root.mkdir(exist_ok=True)
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll']
oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
validator=['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.1']
rows=[]
def save():(root/'checks.json').write_text(json.dumps(rows,indent=2))
def run(name,args,reject=False):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=60)
 passed=p.returncode!=0 if reject else p.returncode==0
 rows.append(dict(check=name,passed=passed,expected_rejection=reject,code=p.returncode,message=p.stdout+p.stderr));save()
 assert passed,rows[-1]
 return p

text=Path('Sia.Graphics/Sia.Spirv.Naga.Tests/PointerAddressTests.cs').read_text()
cases=re.findall(r'\[InlineData\("([^"]*)", "([^"]*)"\)\]',text)
assert len(cases)==17
for i,(declarations,operand) in enumerate(cases):
 reject=i<11
 source='struct S{value:vec2f,scalar:f32,} @compute @workgroup_size(1) fn main(){'+declarations+'let q=&'+operand+';}'
 path=root/f'address-{i}.wgsl';path.write_text(source)
 native=root/f'address-{i}.spv';back=root/f'address-{i}-back.wgsl'
 run(f'address-{i}-reference',oracle+[path],reject)
 result=run(f'address-{i}-managed',harness+[path,native],reject)
 if reject:assert 'vector component' in result.stdout+result.stderr
 else:
  run(f'address-{i}-native',validator+[native]);run(f'address-{i}-reverse',harness+[native,back]);run(f'address-{i}-reverse-reference',oracle+[back])

source=re.search(r'internal const string ControlSource = """\n(.*?)\n        """;',text,re.S).group(1)
path=root/'control.wgsl';path.write_text(source)
inline=root/'control-inline.wgsl';native=root/'control-query.spv'
run('reference-source',oracle+[path]);run('inline',harness+[path,inline]);run('reference-inline',oracle+[inline])
run('query-native',harness+[path,native]);run('query-native-validation',validator+[native])
lines=inline.read_text().splitlines();removed=[];handles=set()
for line in lines:
 match=re.fullmatch(r'\s*(?:var|let) (\w+): (?:ptr<function, ray_query>|ray_query)(?: = (.*))?;',line)
 if not match:continue
 name,initializer=match.groups()
 if initializer is not None:
  dependencies=re.findall(r'\b[A-Za-z_]\w*\b',initializer);assert len(dependencies)==1 and dependencies[0] in handles
 handles.add(name);removed.append(line)
control='\n'.join(line for line in lines if line not in removed and line!='enable wgpu_ray_query;')
assert not re.search(r'\bray_query\b|\brayQuery\w*\b',control)
assert all(not re.search(r'\b'+re.escape(name)+r'\b',control) for name in handles)
gpu=root/'control-gpu.wgsl';gpu.write_text(control)
reference=root/'control-reference.wgsl';reference.write_text(source.replace('enable wgpu_ray_query;','').replace('ray_query','u32'))
managed=root/'control-managed.spv';ref=root/'control-reference.spv';recompiled=root/'control-inline-reference.spv';back=root/'control-back.spv'
run('gpu-managed',harness+[gpu,managed]);run('gpu-reference',oracle+[reference,ref]);run('gpu-inline-reference',oracle+[gpu,recompiled]);run('gpu-roundtrip',harness+[managed,back])
inputs=list(range(8));data=struct.pack('<8I',*inputs);expected=[]
for n in inputs:expected.extend([91,12,1] if n%2==0 else [11,91,0])
expected += [0xdeadbeef]*4
for label,spv in [('managed',managed),('reference',ref),('inline-reference',recompiled),('roundtrip',back)]:
 run(label+'-native',validator+[spv])
 with Vulkan() as vk:
  actual=list(struct.unpack('<28I',vk.execute(spv.read_bytes(),data,112,8)))
  rows.append(dict(check=label+'-gpu',passed=actual==expected,device=vk.name,expected=expected,actual=actual));save();assert actual==expected
  actual=vk.execute(spv.read_bytes(),data,112,8,returnInput=True)
  rows.append(dict(check=label+'-bytes',passed=actual==data,device=vk.name,expected=data.hex(),actual=actual.hex()));save();assert actual==data
(root/'opaque-removal.json').write_text(json.dumps(dict(declarations=removed,handles=sorted(handles),reason='Unused opaque handles removed after helper expansion; no traversal operation or surviving handle use.'),indent=2))
(root/'assembly.json').write_text(json.dumps(dict(sha256=hashlib.sha256(Path(harness[1]).with_name('Sia.Spirv.Naga.dll').read_bytes()).hexdigest()),indent=2))
print(sum(r['passed'] for r in rows),'/',len(rows),'pointer address checks passed',flush=True)
