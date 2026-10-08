from pathlib import Path
import hashlib,json,collections
root=Path('.work/naga-csharp')
corpus=root/'full-corpus'
old=json.loads((corpus/'pre-pointer-selection-baseline.json').read_text(encoding='utf-8'))
new=json.loads((corpus/'manifest.json').read_text(encoding='utf-8'))
def codes(records):return {r['input']:{s:v['code'] for s,v in r['steps'].items()} for r in records}
before,after=codes(old),codes(new)
changed=[dict(input=name,before=before.get(name),after=after.get(name)) for name in sorted(before.keys()|after.keys()) if before.get(name)!=after.get(name)]
reports={}
for name in ['pointer-selection','pointer-address','atomic-memory','query-helpers']:
 rows=json.loads((root/name/'checks.json').read_text())
 reports[name]=dict(total=len(rows),passed=sum(r['passed'] for r in rows),failed=[r for r in rows if not r['passed']],gpu=sum('actual' in r and '-bytes' not in r['check'] for r in rows),bytes=sum('-bytes-' in r['check'] or r['check'].endswith('-bytes') for r in rows))
assembly=Path('.work/naga-csharp/harness/bin/Release/net10.0/Sia.Spirv.Naga.dll')
production=Path('Sia.Graphics/Sia.Spirv.Naga/bin/Release/net10.0/Sia.Spirv.Naga.dll')
digest=hashlib.sha256(assembly.read_bytes()).hexdigest()
assert digest==hashlib.sha256(production.read_bytes()).hexdigest()
summary=dict(sha256=digest,reports=reports,corpus_inputs=len(new),corpus_step_changes=changed)
(root/'pointer-continuation-summary.json').write_text(json.dumps(summary,indent=2))
print(json.dumps(dict(sha256=digest,reports={n:{k:v for k,v in r.items() if k!='failed'} for n,r in reports.items()},corpus_inputs=len(new),corpus_step_changes=changed),indent=2))
assert not changed
