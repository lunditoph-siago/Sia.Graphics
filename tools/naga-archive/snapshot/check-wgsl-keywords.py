from pathlib import Path
import concurrent.futures,json,re,subprocess
root=Path('.work/naga-csharp/keywords');root.mkdir(exist_ok=True)
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll']
oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
words=''.join(re.findall(r'"([A-Za-z_ ]+)"',Path('Sia.Graphics/Sia.Spirv.Naga/WgslKeywords.cs').read_text())).split()
reference=re.search(r'pub const RESERVED.*?=\s*&\[(.*?)\];',Path('.reference/wgpu/naga/src/keywords/wgsl.rs').read_text(),re.S)
assert reference, 'Pinned keyword declaration not found'
assert set(words)==set(re.findall(r'"([A-Za-z_]+)"',reference.group(1)))
def check(word):
    sources=[f'const {word}=1;',f'alias {word}=u32;',f'fn {word}(){{}}',f'fn f({word}:u32){{}}',f'fn f(){{let {word}=1u;}}',f'struct S{{{word}:u32}}']
    rows=[]
    for index,source in enumerate(sources):
        path=root/f'{word}-{index}.wgsl';path.write_text(source)
        for label,command in [('managed',harness+[path,root/f'{word}-{index}-out.wgsl']),('reference',oracle+[path])]:
            p=subprocess.run(list(map(str,command)),capture_output=True,text=True,timeout=30)
            rows.append(dict(word=word,context=index,implementation=label,passed=p.returncode!=0 and (label!='managed' or 'Reserved keyword' in p.stdout+p.stderr),message=p.stdout+p.stderr))
    return rows
with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:rows=[r for group in pool.map(check,words) for r in group]
source='diagnostic(off,derivative_uniformity); @coherent @volatile @group(0) @binding(0) var<storage,read_write> values:array<u32>; @compute @workgroup_size(1) @diagnostic(off,derivative_uniformity) fn main(){values[0]=1u;}'
path=root/'attributes.wgsl';path.write_text(source)
for label,command in [('managed',harness+[path,root/'attributes-out.wgsl']),('reference',oracle+[path]),('reference-canonical',oracle+[root/'attributes-out.wgsl'])]:
    p=subprocess.run(list(map(str,command)),capture_output=True,text=True,timeout=30);rows.append(dict(check=label+'-attributes',passed=p.returncode==0,message=p.stdout+p.stderr))
(root/'checks.json').write_text(json.dumps(rows,indent=2));print(len(words),'reserved words;',sum(r['passed'] for r in rows),'/',len(rows),'expected outcomes')
raise SystemExit(0 if all(r['passed'] for r in rows) else 1)
