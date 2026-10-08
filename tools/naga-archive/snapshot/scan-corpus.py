import concurrent.futures, collections, json, pathlib, subprocess

root = pathlib.Path(__file__).resolve().parents[2]
task = root / '.work/naga-csharp'
output = task / 'full-corpus'
output.mkdir(exist_ok=True)
dotnet = root / '.dotnet/dotnet.exe'
harness = task / 'harness/bin/Release/net10.0/harness.dll'
oracle = task / 'oracle-target/release/sia-naga-reference-oracle.exe'
native = task / 'spirv-tools-local/tools/Release'

def run(args):
    result = subprocess.run(list(map(str,args)), capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=30)
    return result.returncode, result.stdout + result.stderr

def check(source):
    work = output / (source.parent.name + '-' + source.stem)
    work.mkdir(exist_ok=True)
    record = {'input': str(source.relative_to(root)), 'steps': {}}
    def step(name, args):
        try: code, log = run(args)
        except subprocess.TimeoutExpired: code, log = -1, 'TIMEOUT'
        (work / (name + '.log')).write_text(log,encoding='utf-8')
        record['steps'][name] = {'code': code, 'message': log.splitlines()[0] if log else ''}
        return code == 0
    if source.suffix == '.spvasm':
        actual = work / 'source.spv'
        if not step('assemble', [native/'spirv-as.exe','--target-env','spv1.3',source,'-o',actual]): return record
    else: actual = source
    if not step('reference_input', [oracle,actual]): return record
    wgsl, spv = work/'managed.wgsl', work/'managed.spv'
    if step('managed_wgsl', [dotnet,harness,actual,wgsl]): step('reference_wgsl', [oracle,wgsl])
    if step('managed_spirv', [dotnet,harness,actual,spv]):
        version = int.from_bytes(spv.read_bytes()[4:8], 'little')
        step('native_spirv', [native/'spirv-val.exe','--target-env','vulkan1.2' if version >= 0x10400 else 'vulkan1.1',spv])
        step('reference_spirv', [oracle,spv])
        roundtrip = work/'roundtrip.wgsl'
        if step('managed_roundtrip', [dotnet,harness,spv,roundtrip]): step('reference_roundtrip', [oracle,roundtrip])
    return record

sources = sorted((root/'.reference/wgpu/naga/tests/in/wgsl').glob('*.wgsl')) + sorted((root/'.reference/wgpu/naga/tests/in/spv').glob('*.spvasm'))
with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
    records = list(pool.map(check, sources))
(output/'manifest.json').write_text(json.dumps(records,indent=2,ensure_ascii=False),encoding='utf-8')
for step in ['assemble','reference_input','managed_wgsl','reference_wgsl','managed_spirv','native_spirv','reference_spirv','managed_roundtrip','reference_roundtrip']:
    counts = collections.Counter('pass' if r['steps'][step]['code']==0 else 'fail' for r in records if step in r['steps'])
    print(step,dict(counts), flush=True)
failures = collections.Counter(r['steps']['managed_wgsl']['message'] for r in records if r['steps'].get('managed_wgsl',{}).get('code',0)!=0)
print('WGSL failure groups',json.dumps(failures,ensure_ascii=False,indent=2),flush=True)
