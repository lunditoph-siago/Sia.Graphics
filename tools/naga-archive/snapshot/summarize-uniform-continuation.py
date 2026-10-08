from pathlib import Path
import json,collections
root=Path('.work/naga-csharp')
before=json.loads((root/'full-corpus/pre-uniform-baseline.json').read_text())
after=json.loads((root/'full-corpus/manifest.json').read_text())
def outcomes(records):return {r['input']:{k:v['code'] for k,v in r['steps'].items()} for r in records}
print('corpus step codes unchanged:',outcomes(before)==outcomes(after))
for name,path in [('uniform','uniform-memory/checks.json'),('member','member-memory/checks.json'),('access','access-memory/checks.json'),('atomic','atomic-memory/checks.json'),('barrier','barrier-memory/checks.json'),('mesh','mesh/control-checks.json')]:
 if not (root/path).exists():continue
 rows=json.loads((root/path).read_text());commands=[r for r in rows if 'command' in r];gpu=[r for r in rows if 'actual' in r]
 native=[r for r in commands if 'spirv-val.exe' in str(r['command']) and r.get('code')==0]
 expected=[r for r in commands if r.get('expected_rejection')]
 vulkan=[r for r in gpu if r.get('features',{}).get('vulkanMemoryModel')]
 print(name,dict(total=len(rows),passed=sum(bool(r['passed']) for r in rows),commands=len(commands),expected=len(expected),native=len(native),gpu=len(gpu),vulkanGpu=len(vulkan)))
