from pathlib import Path
import hashlib,json,re,struct,subprocess
from vulkan_compute import Vulkan

root=Path('.work/naga-csharp/query-helpers');root.mkdir(exist_ok=True)
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll']
oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
validator=['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.1']
rows=[]
def save(): (root/'checks.json').write_text(json.dumps(rows,indent=2))
def run(label,args):
    p=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=60)
    rows.append(dict(check=label,passed=p.returncode==0,message=p.stdout+p.stderr));save()
    if p.returncode: raise RuntimeError(label+': '+p.stdout+p.stderr)

text=Path('Sia.Graphics/Sia.Spirv.Naga.Tests/QueryHelperTests.cs').read_text()
source=re.search(r'internal const string ControlSource = """\n(.*?)\n        """;',text,re.S).group(1)
path=root/'control.wgsl';path.write_text(source)
inline=root/'control-inline.wgsl';native=root/'control-native.spv'
run('reference-source',oracle+[path]);run('inline',harness+[path,inline]);run('reference-inline',oracle+[inline])
run('managed-query-native',harness+[path,native]);run('query-native-validation',validator+[native])

# The device has no ray-query support. Remove ONLY typed opaque declarations
# whose initializers are addresses/aliases and whose uses are other removed
# declarations; reject any traversal operation or other surviving handle use.
lines=inline.read_text().splitlines();removed=[];handles=set()
for line in lines:
    match=re.fullmatch(r'\s*(?:var|let) (\w+): (?:ptr<function, ray_query>|ray_query)(?: = (.*))?;',line)
    if not match:continue
    name,initializer=match.groups()
    if initializer is not None:
        dependencies=re.findall(r'\b[A-Za-z_]\w*\b',initializer)
        assert len(dependencies)==1 and dependencies[0] in handles,(name,initializer)
    handles.add(name);removed.append(line)
control='\n'.join(line for line in lines if line not in removed and line!='enable wgpu_ray_query;')
assert not re.search(r'\bray_query\b|\brayQuery\w*\b',control)
assert all(not re.search(r'\b'+re.escape(name)+r'\b',control) for name in handles)
gpu=root/'control-inline-gpu.wgsl';gpu.write_text(control)
reference=root/'control-reference.wgsl';reference.write_text(source.replace('enable wgpu_ray_query;','').replace('ray_query','u32'))
run('reference-gpu-source',oracle+[gpu]);run('reference-control-source',oracle+[reference])
managed=root/'control-inline-gpu.spv';ref=root/'control-reference.spv';recompiled=root/'control-inline-reference.spv';back=root/'control-inline-back.spv'
run('gpu-compile',harness+[gpu,managed]);run('reference-control-compile',oracle+[reference,ref]);run('reference-inline-compile',oracle+[gpu,recompiled]);run('gpu-roundtrip',harness+[managed,back])
for name,spv in [('managed',managed),('reference',ref),('reference-inline',recompiled),('roundtrip',back)]:run(name+'-native',validator+[spv])

inputs=list(range(8));expected=[]
for n in inputs:
    step=n+(10 if n%3==0 else 20 if n%3==1 else 30)
    state=n;row=[step,state];state=state*10+2;row.append(state)
    state=(state*10+3)*10+1;row.extend([24,state])
    state=(state*10+4)*10+5;row.extend([24,state])
    state=(state*10+6)*10+7+1000;row.extend([627,state,21,200,1]);expected.extend(row)
expected += [0xdeadbeef]*4
for name,spv in [('managed',managed),('reference',ref),('reference-inline',recompiled),('roundtrip',back)]:
    with Vulkan() as vk:
        actual=list(struct.unpack('<'+str(len(expected))+'I',vk.execute(spv.read_bytes(),struct.pack('<8I',*inputs),len(expected)*4,len(inputs))))
        errors=[dict(index=i,expected=e,actual=a) for i,(e,a) in enumerate(zip(expected,actual)) if e!=a]
        rows.append(dict(check=name+'-gpu',passed=not errors,device=vk.name,sha256=hashlib.sha256(spv.read_bytes()).hexdigest(),expected=expected,actual=actual,mismatches=errors));save()
        print(name,len(errors),'mismatches',flush=True)
        if errors:raise RuntimeError(name+' GPU mismatch: '+str(errors[:6]))
(root/'opaque-removal.json').write_text(json.dumps(dict(declarations=removed,handles=sorted(handles),reason='No operations or surviving uses; control-flow-only GPU comparison.'),indent=2))

source='''enable wgpu_ray_query;
@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;
fn helper(q:ptr<function,ray_query>,n:u32)->u32 {if n>1u{return n+1u;}return 0u;}
@compute @workgroup_size(1) fn main(){var query:ray_query;var n=0u;loop{n++;continuing{let value=helper(&query,n);outputs[n-1u]=value;break if n>=4u;}}}'''
path=root/'continuing.wgsl';path.write_text(source);inline=root/'continuing-inline.wgsl';run('continuing-reference',oracle+[path]);run('continuing-inline',harness+[path,inline]);run('continuing-reference-inline',oracle+[inline])
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
gpu=root/'continuing-gpu.wgsl';gpu.write_text(control);reference=root/'continuing-reference.wgsl';reference.write_text(source.replace('enable wgpu_ray_query;','').replace('ray_query','u32'))
managed=root/'continuing-gpu.spv';ref=root/'continuing-reference.spv';recompiled=root/'continuing-inline-reference.spv';back=root/'continuing-back.spv'
run('continuing-gpu-compile',harness+[gpu,managed]);run('continuing-reference-compile',oracle+[reference,ref]);run('continuing-inline-reference-compile',oracle+[gpu,recompiled]);run('continuing-roundtrip',harness+[managed,back])
expected=[0,3,4,5]+[0xdeadbeef]*4
for name,spv in [('managed',managed),('reference',ref),('reference-inline',recompiled),('roundtrip',back)]:
 run('continuing-'+name+'-native',validator+[spv])
 with Vulkan() as vk:
  actual=list(struct.unpack('<8I',vk.execute(spv.read_bytes(),struct.pack('<I',0),32,1)))
  rows.append(dict(check='continuing-'+name+'-gpu',passed=actual==expected,device=vk.name,sha256=hashlib.sha256(spv.read_bytes()).hexdigest(),expected=expected,actual=actual));save();print('continuing',name,actual==expected,flush=True)
  assert actual==expected
print(sum(r['passed'] for r in rows),'/',len(rows),'checks passed')
