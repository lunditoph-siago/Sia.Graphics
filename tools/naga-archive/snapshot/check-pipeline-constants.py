import json, pathlib, subprocess

root = pathlib.Path(__file__).resolve().parents[2]
work = root / '.work/naga-csharp/pipeline-checks'
work.mkdir(exist_ok=True)
dotnet = root / '.dotnet/dotnet.exe'
harness = root / '.work/naga-csharp/harness/bin/Release/net10.0/harness.dll'
oracle = root / '.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe'
validator = root / '.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe'
cases = [
    ('upstream', (root / '.reference/wgpu/naga/tests/in/wgsl/overrides.wgsl').read_text(), '1300=1.25;depth=3'),
    ('upstream-atomic', (root / '.reference/wgpu/naga/tests/in/wgsl/overrides-atomicCompareExchangeWeak.wgsl').read_text(), 'o=3'),
    ('dependent', '@id(7) override size: u32; override area = size * 2u; var<private> x: u32 = area + 1u; @compute @workgroup_size(area, 2i) fn main() { let v = x + area; }', '7=4.9'),
    ('bool-nan', 'override enabled: bool; @compute @workgroup_size(1) fn main() { let v = enabled; }', 'enabled=NaN'),
    ('bool-infinity', 'override enabled: bool; @compute @workgroup_size(1) fn main() { let v = enabled; }', 'enabled=inf'),
    ('half', 'enable f16; override value: f16; @compute @workgroup_size(1) fn main() { let v = value; }', 'value=1.25'),
    ('double', 'override value: f64; @compute @workgroup_size(1) fn main() { let v = value; }', 'value=1.25'),
    ('truncate', 'override value: u32; @compute @workgroup_size(1) fn main() { let v = value; }', 'value=-0.9'),
    ('defaults', 'override base: u32 = 2u; override area = base * 3u; @compute @workgroup_size(area) fn main() {}', ''),
    ('shadow', 'override value: u32 = 4u; fn helper(value: u32) -> u32 { return value; } @compute @workgroup_size(value) fn main() { { let value = 9u; let v = value; } let global = value; }', ''),
]
records = []
def run(name, step, args, expected=0):
    p = subprocess.run(list(map(str,args)), capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=30)
    (work / f'{name}-{step}.log').write_text(p.stdout+p.stderr, encoding='utf-8')
    records.append({'case': name, 'step': step, 'code': p.returncode, 'pass': p.returncode == 0 if expected == 0 else p.returncode != 0})
    return p.returncode == 0
for name, source, values in cases:
    path = work / f'{name}.wgsl'; path.write_text(source, encoding='utf-8')
    wgsl, spv, refspv = [work / f'{name}-{suffix}' for suffix in ('managed.wgsl','managed.spv','reference.spv')]
    managed_values = values.replace('=inf', '=Infinity')
    if run(name,'managed-wgsl',[dotnet,harness,path,wgsl,managed_values]): run(name,'validate-wgsl',[oracle,wgsl])
    if run(name,'managed-spv',[dotnet,harness,path,spv,managed_values]):
        run(name,'native-spv',[validator,'--target-env','vulkan1.1',spv])
        run(name,'reference-spv',[oracle,spv])
    if run(name,'reference-generate',[oracle,path,refspv,values]): run(name,'reference-native',[validator,'--target-env','vulkan1.1',refspv])
for name, values in [('missing',''),('unknown','unknown=1'),('nonfinite','value=NaN'),('range','value=4294967296')]:
    path = work / f'error-{name}.wgsl'; path.write_text('override value: u32; @compute @workgroup_size(1) fn main() { let v = value; }',encoding='utf-8')
    run(name,'managed-reject',[dotnet,harness,path,work/f'error-{name}.spv',values],1)
    run(name,'reference-reject',[oracle,path,work/f'error-{name}-reference.spv',values],1)
(work/'manifest.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
print(f'{sum(r["pass"] for r in records)}/{len(records)} checks passed')
print(json.dumps([r for r in records if not r['pass']],indent=2))
raise SystemExit(0 if all(r['pass'] for r in records) else 1)
