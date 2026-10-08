import pathlib, struct, subprocess, json
root=pathlib.Path(__file__).resolve().parents[2]
work=root/'.work/naga-csharp/special-floats'; work.mkdir(exist_ok=True)
dotnet=root/'.dotnet/dotnet.exe'
harness=root/'.work/naga-csharp/harness/bin/Release/net10.0/harness.dll'
oracle=root/'.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe'
validator=root/'.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe'
records=[]
def run(name, args):
    p=subprocess.run(list(map(str,args)),capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=30)
    records.append({'step':name,'code':p.returncode,'message':p.stdout+p.stderr})
    if p.returncode: raise RuntimeError(name+': '+p.stdout+p.stderr)
for kind in ['f32','vec2f','f16','f64']:
    enable='enable f16;' if kind=='f16' else ''
    comparison='all(a < b)' if kind=='vec2f' else 'a < b'
    source=work/(kind+'.wgsl'); original=work/(kind+'.original.spv'); mutated=work/(kind+'.spv'); output=work/(kind+'.out.wgsl'); final=work/(kind+'.out.spv')
    source.write_text(f'{enable} @group(0) @binding(0) var<storage, read> input: array<{kind}>; @group(0) @binding(1) var<storage, read_write> output: array<u32>; @compute @workgroup_size(1) fn main() {{ let a = input[0]; let b = input[1]; output[0] = select(0u, 1u, {comparison}); }}',encoding='utf-8')
    run(kind+' generate',[dotnet,harness,source,original])
    words=list(struct.unpack('<'+'I'*(original.stat().st_size//4),original.read_bytes())); cursor=5; changed=0
    while cursor<len(words):
        count=words[cursor]>>16
        if words[cursor]&65535==184: words[cursor]=count<<16|185; changed+=1
        cursor+=count
    assert changed==1
    mutated.write_bytes(struct.pack('<'+'I'*len(words),*words))
    run(kind+' input validation',[validator,'--target-env','vulkan1.1',mutated])
    run(kind+' translate',[dotnet,harness,mutated,output])
    run(kind+' WGSL reference validation',[oracle,output])
    run(kind+' regenerate',[dotnet,harness,output,final])
    run(kind+' output validation',[validator,'--target-env','vulkan1.1',final])
(work/'manifest.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
print(f'{len(records)} independent conversion checks passed; GPU behavior not executed.')
