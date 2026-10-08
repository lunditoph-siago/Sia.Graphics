from pathlib import Path
import collections,hashlib,json
root=Path('.work/naga-csharp')
assembly=root/'harness/bin/Release/net10.0/Sia.Spirv.Naga.dll';digest=hashlib.sha256(assembly.read_bytes()).hexdigest()
for path in ['Sia.Graphics/Sia.Spirv.Naga/bin/Release/net10.0/Sia.Spirv.Naga.dll','Sia.Graphics/Sia.Spirv.Naga.Tests/bin/Release/net10.0/Sia.Spirv.Naga.dll','.work/naga-csharp/pointer-selection-fixtures/bin/Release/net10.0/Sia.Spirv.Naga.dll']:
 assert hashlib.sha256(Path(path).read_bytes()).hexdigest()==digest,path
before=json.loads((root/'full-corpus/pre-pointer-null-baseline.json').read_text(encoding='utf-8'))
after=json.loads((root/'full-corpus/manifest.json').read_text(encoding='utf-8'))
def codes(rows):return {r['input']:{n:s['code'] for n,s in r['steps'].items()} for r in rows}
assert len(after)==197 and codes(before)==codes(after)
reports={}
for directory,count in [('pointer-null',80),('pointer-cross-buffer-noopt',34)]:
 p=root/directory;rows=json.loads((p/'checks.json').read_text());meta=json.loads((p/'assembly.json').read_text())
 assert meta['sha256']==digest,(directory,meta)
 assert len(json.loads((p/'fixtures.json').read_text()))==count
 for suffix in ['-input-native','-normalized-native','-back-native','-managed-native']:
  selected=[r for r in rows if r['check'].endswith(suffix)];assert len(selected)==count and all(r['passed'] for r in selected)
 failed=[r for r in rows if not r['passed']]
 if directory=='pointer-null':
  native=[r for r in failed if r.get('code') is not None];gpu=[r for r in failed if 'device' in r]
  assert len(native)==16 and all(r.get('external') and 'VUID-StandaloneSpirv-None-10684' in r['message'] for r in native)
  assert len(gpu)==96 and all(r.get('external') and r.get('failure_class')=='unresolved-original-workgroup-null-execution'
     and r['check'].startswith('workgroup-') and '-input-gpu-' in r['check'] for r in gpu)
  assert len(rows)==6864
 else:
  assert not failed and len(rows)==3026
  assert meta['original_normalized_pipeline_flags']==1 and meta['translated_pipeline_flags']==0
  for r in rows:
   if 'device' in r:assert r['pipeline_flags']==(1 if '-input-' in r['check'] or '-normalized-' in r['check'] else 0)
 reports[directory]=dict(total=len(rows),passed=sum(r['passed'] for r in rows),failed=len(failed),
    gpu_total=sum('device' in r for r in rows),gpu_passed=sum('device' in r and r['passed'] for r in rows),
    outputs_passed=sum('device' in r and '-gpu-' in r['check'] and r['passed'] for r in rows),
    inputs_passed=sum('device' in r and '-bytes-' in r['check'] and r['passed'] for r in rows),
    failure_classes=dict(collections.Counter(r.get('failure_class','reference-native-validation') for r in failed)))
alias=json.loads((root/'pointer-cross-buffer/alias-investigation/report.json').read_text())
assert alias['sha256']==digest and len(alias['rows'])==28
assert sum(not r['matches'] for r in alias['rows'])==5
assert all(r['matches'] for r in alias['rows'] if r['noopt'] or r['label'] in ('managed','reference'))
probe=json.loads((root/'pointer-null/workgroup-native-difference.json').read_text());assert probe['sha256']==digest
assert sum(r.get('matches') is False for r in probe['rows'])==2
assert all(r.get('matches',True) for r in probe['rows'] if not r['check'].startswith('input-'))
summary=dict(sha256=digest,corpus_inputs=197,corpus_step_changes=[],reports=reports,
             cross_alias_probe=dict(outputs=28,failed_default_optimization=5,passed_noopt=14),
             workgroup_probe=dict(failed_original_outputs=2,passed_normalized_and_managed_outputs=12,reference_native_validation_failed=True))
(root/'pointer-null-summary.json').write_text(json.dumps(summary,indent=2));print(json.dumps(summary,indent=2))
