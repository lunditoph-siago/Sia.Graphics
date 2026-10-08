from pathlib import Path
import hashlib, json, struct, subprocess, sys
sys.path.insert(0, str(Path('.work/naga-csharp').resolve()))
from vulkan_compute import Vulkan

root = Path('.work/naga-csharp/pointer-cross-buffer')
rows = []
harness = ['.dotnet/dotnet.exe', '.work/naga-csharp/harness/bin/Release/net10.0/harness.dll']
oracle = ['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
validator = ['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe', '--target-env', 'vulkan1.2']
assembly = Path(harness[1]).with_name('Sia.Spirv.Naga.dll')
digest = hashlib.sha256(assembly.read_bytes()).hexdigest()
def save(): (root / 'checks.json').write_text(json.dumps(rows, indent=2))
def run(name, args):
    result = subprocess.run(list(map(str, args)), capture_output=True, text=True, timeout=60)
    rows.append(dict(check=name, passed=result.returncode == 0, code=result.returncode,
                     message=result.stdout + result.stderr, command=list(map(str, args))))
    save()
    if result.returncode: raise RuntimeError(rows[-1])

for case in json.loads((root / 'fixtures.json').read_text()):
    name = case['name']
    original = root / (name + '.spv'); normalized = root / (name + '-normalized.spv')
    back = root / (name + '-back.spv'); wgsl = root / (name + '.wgsl')
    reference = root / (name + '-reference.spv'); managed = root / (name + '-managed.spv')
    run(name + '-input-native', validator + [original]); run(name + '-normalized-native', validator + [normalized])
    run(name + '-emit', harness + [original, back]); run(name + '-back-native', validator + [back])
    run(name + '-wgsl', harness + [original, wgsl]); run(name + '-reference', oracle + [wgsl, reference])
    run(name + '-reference-native', validator + [reference]); run(name + '-managed', harness + [wgsl, managed])
    run(name + '-managed-native', validator + [managed])
    inputs = []
    for seed in range(8):
        flag = seed & 1; second = (seed >> 1) & 1; c = seed % 4; r = (seed + 1) % 4; third = (seed + 2) % 4
        flags = [flag, c, r, second, third, 0, 0, 0]
        a = [11 + 10 * j + 100 * seed for j in range(4)]; b = [111 + 10 * j + 100 * seed for j in range(4)]
        data = struct.pack('<17I', *(flags + a + b + [919])); output = [0xdeadbeef] * 4
        chosen_a = bool(flag); index = c if flag else r
        if case['mode'] == 'nested' and not second: chosen_a = True; index = third
        initial_a = chosen_a
        for iteration in range(case['iterations']):
            if case['mode'] == 'swap': chosen_a = initial_a if iteration % 2 == 0 else not initial_a; index = c if chosen_a else r
            target = a if chosen_a else output
            if case['transfer'] == 'helper-edit': target[index] = (target[index] + 7) & 0xffffffff
            old = target[index]; target[index] = (old + 100) & 0xffffffff
            output[0] = old; output[1] = target[index]; output[2] = 919
        flags[0] = 1 - flag if case['iterations'] % 2 else flag; flags[1] = 3; flags[2] = 0
        expected_memory = struct.pack('<17I', *(flags + a + b + [919]))
        inputs.append((seed, data, output, expected_memory))
    for label, path in [('input', original), ('normalized', normalized), ('roundtrip', back), ('reference', reference), ('managed', managed)]:
        pointers = 'full' if label in ('input', 'normalized') else False
        with Vulkan(variablePointers=pointers) as vk:
            for seed, data, expected, expected_memory in inputs:
                actual = list(struct.unpack('<4I', vk.execute(path.read_bytes(), data, 16, 1)))
                rows.append(dict(check=f'{name}-{label}-gpu-{seed}', passed=actual == expected, device=vk.name, actual=actual, expected=expected))
                if not rows[-1]['passed']: save(); raise RuntimeError(rows[-1])
                actual = vk.execute(path.read_bytes(), data, 16, 1, returnInput=True)
                rows.append(dict(check=f'{name}-{label}-bytes-{seed}', passed=actual == expected_memory, device=vk.name, actual=actual.hex(), expected=expected_memory.hex()))
                if not rows[-1]['passed']: save(); raise RuntimeError(rows[-1])
    save(); print(name, 'checks recorded', flush=True)
assert digest == hashlib.sha256(assembly.read_bytes()).hexdigest(), 'Assembly changed during verification'
(root / 'assembly.json').write_text(json.dumps(dict(sha256=digest), indent=2))
print(sum(r['passed'] for r in rows), '/', len(rows), 'cross-buffer checks passed', flush=True)
