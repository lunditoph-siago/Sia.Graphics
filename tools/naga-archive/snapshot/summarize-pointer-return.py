from pathlib import Path
import hashlib,json
root=Path('.work/naga-csharp')
before=json.loads((root/'full-corpus/pre-pointer-cross-buffer-baseline.json').read_text(encoding='utf-8'))
after=json.loads((root/'full-corpus/manifest.json').read_text(encoding='utf-8'))
def codes(rows): return {r['input']:{name:step['code'] for name,step in r['steps'].items()} for r in rows}
a,b=codes(before),codes(after)
changes=[dict(input=name,before=a.get(name),after=b.get(name)) for name in sorted(a.keys()|b.keys()) if a.get(name)!=b.get(name)]
digest=hashlib.sha256((root/'harness/bin/Release/net10.0/Sia.Spirv.Naga.dll').read_bytes()).hexdigest()
for path in ['Sia.Graphics/Sia.Spirv.Naga/bin/Release/net10.0/Sia.Spirv.Naga.dll','Sia.Graphics/Sia.Spirv.Naga.Tests/bin/Release/net10.0/Sia.Spirv.Naga.dll']:
 assert hashlib.sha256(Path(path).read_bytes()).hexdigest()==digest
reports={}
for name,external in [('pointer-return',12),('pointer-slot-transfer',12),('pointer-memory',6),('pointer-phi',3),('pointer-selection',2),('atomic-memory',0)]:
 rows=json.loads((root/name/'checks.json').read_text()); failed=[r for r in rows if not r['passed']]
 assert len(failed)==external,(name,len(failed))
 assert all(r.get('external') and 'VUID-StandaloneSpirv-None-10684' in r['message'] for r in failed)
 if name!='atomic-memory': assert json.loads((root/name/'assembly.json').read_text())['sha256']==digest
 reports[name]=dict(total=len(rows),passed=sum(r['passed'] for r in rows),failed=failed,
                    gpu=sum('actual' in r and '-bytes' not in r['check'] for r in rows),
                    bytes=sum('-bytes-' in r['check'] for r in rows),devices=sorted({r['device'] for r in rows if 'device' in r}))
for name,count in [('pointer-return',66),('pointer-slot-transfer',76),('pointer-memory',52)]:
 rows=json.loads((root/name/'checks.json').read_text())
 native=json.loads((root/name/'native-validation.json').read_text())
 assert len(native)==count*2 and all(r['code']==0 for r in native)
 for suffix in ['-input-native','-normalized-native','-back-native']:
  selected=[r for r in rows if r['check'].endswith(suffix)]
  assert len(selected)==count and all(r['passed'] for r in selected),(name,suffix,len(selected))
memory=json.loads((root/'pointer-memory/checks.json').read_text())
rejections=[r for r in memory if r['check'].endswith('-wgsl-expected-rejection')]
assert len(rejections)==2 and all(r['passed'] and r['code']!=0 and 'volatile memory access' in r['message'] for r in rejections)
probe=json.loads((root/'pointer-cross-buffer/full-capability-loop-difference.json').read_text())
assert probe['sha256']==digest
differences=[r for r in probe['rows'] if r.get('matches') is False]
assert {r['check'] for r in differences}=={'original-output-0','normalized-output-0'}
assert all(r.get('matches',True) for r in probe['rows'] if r['check'].startswith(('managed','reference')))
assert len(after)==197 and not changes
summary=dict(sha256=digest,corpus_inputs=len(after),corpus_step_changes=changes,reports=reports,
             unresolved_native_loop_differences=differences)
(root/'pointer-return-summary.json').write_text(json.dumps(summary,indent=2))
print(json.dumps(dict(sha256=digest,corpus_inputs=len(after),corpus_step_changes=changes,
                     reports={n:{k:v for k,v in r.items() if k!='failed'} for n,r in reports.items()},
                     unresolved_native_loop_differences=[r['check'] for r in differences]),indent=2))
