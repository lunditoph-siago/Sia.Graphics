import json, pathlib, subprocess
root = pathlib.Path(__file__).resolve().parents[2]
work = root / '.work/naga-csharp/must-use-checks'
work.mkdir(exist_ok=True)
dotnet = root / '.dotnet/dotnet.exe'
harness = root / '.work/naga-csharp/harness/bin/Release/net10.0/harness.dll'
oracle = root / '.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe'
validator = root / '.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe'
valid = {
    'upstream': (root / '.reference/wgpu/naga/tests/in/wgsl/must-use.wgsl').read_text(),
    'forward-use': 'fn caller() -> i32 { return required() + 1; } @must_use fn required() -> i32 { return 10; } @compute @workgroup_size(1) fn main() { _ = caller(); _ = required(); }',
    'atomic': 'var<workgroup> value: atomic<u32>; fn optional() -> i32 { return 1; } @compute @workgroup_size(1) fn main() { optional(); atomicLoad(&value); atomicAdd(&value, 1u); atomicCompareExchangeWeak(&value, 1u, 2u); }',
    'discard': '@compute @workgroup_size(1) fn main() { _ = abs(-1); _ = vec2f(1.0); _ = bitcast<f32>(1u); _ = subgroupAdd(1u); }',
}
invalid = [
    'fn caller() { required(); } @must_use fn required() -> i32 { return 1; }',
    '@must_use fn required() {}',
    '@must_use(1) fn required() -> i32 { return 1; }',
    '@must_use @must_use fn required() -> i32 { return 1; }',
    '@must_use const value = 1;',
    'fn main() { abs(-1); }',
    'fn main() { vec2f(1.0); }',
    'struct Value { x: i32, } fn main() { Value(1); }',
    'fn main() { bitcast<f32>(1u); }',
    'var<workgroup> value: u32; @compute @workgroup_size(1) fn main() { workgroupUniformLoad(&value); }',
    '@compute @workgroup_size(1) fn main() { subgroupAdd(1u); }',
]
records = []
def check(name, step, args, expected=True):
    p = subprocess.run(list(map(str, args)), capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=30)
    log = p.stdout + p.stderr
    (work / f'{name}-{step}.log').write_text(log, encoding='utf-8')
    records.append(dict(case=name, step=step, expected_accept=expected, code=p.returncode, passed=(p.returncode == 0) == expected))
    return p.returncode == 0
for name, source in valid.items():
    path = work / f'{name}.wgsl'; path.write_text(source, encoding='utf-8')
    check(name, 'reference-input', [oracle, path])
    wgsl, spv, back = [work / f'{name}-{part}' for part in ['managed.wgsl', 'managed.spv', 'roundtrip.wgsl']]
    if check(name, 'managed-wgsl', [dotnet, harness, path, wgsl]): check(name, 'reference-output', [oracle, wgsl])
    if check(name, 'managed-spv', [dotnet, harness, path, spv]):
        check(name, 'native', [validator, '--target-env', 'vulkan1.1', spv])
        check(name, 'reference-spv', [oracle, spv])
        if check(name, 'roundtrip', [dotnet, harness, spv, back]): check(name, 'reference-roundtrip', [oracle, back])
for index, source in enumerate(invalid):
    name = f'invalid-{index}'
    path = work / f'{name}.wgsl'; path.write_text(source, encoding='utf-8')
    check(name, 'reference-rejection', [oracle, path], False)
    check(name, 'managed-rejection', [dotnet, harness, path, work / f'{name}.spv'], False)
(work / 'manifest.json').write_text(json.dumps(records, indent=2), encoding='utf-8')
print(f'{sum(r["passed"] for r in records)}/{len(records)} checks passed')
print(json.dumps([r for r in records if not r['passed']], indent=2))
raise SystemExit(0 if all(r['passed'] for r in records) else 1)
