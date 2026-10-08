from pathlib import Path
import json

root = Path('.work/naga-csharp')
before = {r['input']: r for r in json.loads((root / 'full-corpus/pre-workgroup-baseline.json').read_text())}
after = json.loads((root / 'full-corpus/manifest.json').read_text())
changes = [(r['input'], k, before[r['input']]['steps'].get(k), v)
           for r in after for k, v in r['steps'].items()
           if v['code'] != before[r['input']]['steps'].get(k, {}).get('code')]
print('corpus changes:', json.dumps(changes, indent=2))
for name, path in [('workgroup', 'workgroup-memory/checks.json'),
                   ('uniform', 'uniform-memory/checks.json'),
                   ('member', 'member-memory/checks.json'),
                   ('access', 'access-memory/checks.json'),
                   ('atomic', 'atomic-memory/checks.json'),
                   ('barrier', 'barrier-memory/checks.json'),
                   ('mesh', 'mesh/control-checks.json')]:
    rows = json.loads((root / path).read_text())
    commands = [r for r in rows if 'command' in r]
    gpu = [r for r in rows if 'actual' in r]
    native = [r for r in commands if 'spirv-val.exe' in str(r['command']) and r.get('code') == 0]
    rejected = [r for r in commands if r.get('expected_rejection')]
    vulkan = [r for r in gpu if r.get('features', {}).get('vulkanMemoryModel')]
    specialized = [r for r in gpu if r.get('specializedLength') == 3]
    print(name, dict(total=len(rows), passed=sum(bool(r['passed']) for r in rows),
                     commands=len(commands), successfulCommands=sum(r.get('code') == 0 for r in commands),
                     expectedRejections=len(rejected), nativeValidations=len(native),
                     gpuReadbacks=len(gpu), vulkanGpu=len(vulkan), specializedLength3=len(specialized)))
