import json, pathlib, subprocess
root = pathlib.Path(__file__).resolve().parents[2]
work = root / '.work/naga-csharp/memory-checks'; work.mkdir(exist_ok=True)
dotnet = root / '.dotnet/dotnet.exe'
harness = root / '.work/naga-csharp/harness/bin/Release/net10.0/harness.dll'
oracle = root / '.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe'
validator = root / '.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe'
valid = {name: (root / f'.reference/wgpu/naga/tests/in/wgsl/{name}.wgsl').read_text() for name in ['memory-decorations', 'memory-decorations-coherent']}
valid['duplicate'] = '@coherent @coherent @volatile @volatile @group(0) @binding(0) var<storage,read_write> data: array<u32>; @compute @workgroup_size(1) fn main() { data[0] = data[1]; }'
valid['read-only'] = '@coherent @volatile @group(0) @binding(0) var<storage,read> data: array<u32>; @compute @workgroup_size(1) fn main() { _ = data[0]; }'
valid['atomic'] = '@coherent @volatile @group(0) @binding(0) var<storage,read_write> data: atomic<u32>; @compute @workgroup_size(1) fn main() { atomicAdd(&data, 1u); }'
invalid = ['@coherent var<private> value: u32;', '@volatile var<workgroup> value: u32;', '@coherent @group(0) @binding(0) var<uniform> value: u32;', '@volatile @group(0) @binding(0) var image: texture_storage_2d<r32uint,read_write>;', '@coherent(1) @group(0) @binding(0) var<storage> value: u32;', '@volatile(1) @group(0) @binding(0) var<storage> value: u32;']
records = []
def check(name, step, args, expected=True):
    p = subprocess.run(list(map(str,args)), capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=30)
    log = p.stdout+p.stderr; (work/f'{name}-{step}.log').write_text(log, encoding='utf-8')
    records.append(dict(case=name, step=step, expected_accept=expected, code=p.returncode, passed=(p.returncode==0)==expected, message=log.splitlines()[0] if log else ''))
    return p.returncode == 0
for name, source in valid.items():
    path=work/f'{name}.wgsl'; path.write_text(source,encoding='utf-8')
    check(name,'reference-input',[oracle,path])
    wgsl,spv,back=[work/f'{name}-{part}' for part in ['managed.wgsl','managed.spv','roundtrip.wgsl']]
    if check(name,'managed-wgsl',[dotnet,harness,path,wgsl]): check(name,'reference-output',[oracle,wgsl])
    if check(name,'managed-spv',[dotnet,harness,path,spv]):
        check(name,'native',[validator,'--target-env','vulkan1.1',spv]); check(name,'reference-spv',[oracle,spv])
        if check(name,'roundtrip',[dotnet,harness,spv,back]): check(name,'reference-roundtrip',[oracle,back])
for index,source in enumerate(invalid):
    name=f'invalid-{index}'; path=work/f'{name}.wgsl'; path.write_text(source,encoding='utf-8')
    check(name,'reference-rejection',[oracle,path],False); check(name,'managed-rejection',[dotnet,harness,path,work/f'{name}.spv'],False)
(work/'manifest.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
print(f'{sum(r["passed"] for r in records)}/{len(records)} checks passed')
print(json.dumps([r for r in records if not r['passed']],indent=2))
raise SystemExit(0 if all(r['passed'] for r in records) else 1)
