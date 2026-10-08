from pathlib import Path
import collections,hashlib,json,xml.etree.ElementTree as ET
root=Path('.work/naga-csharp');assembly=root/'harness/bin/Release/net10.0/Sia.Spirv.Naga.dll';digest=hashlib.sha256(assembly.read_bytes()).hexdigest()
assert digest=='c909be36dea2850e3b7c805dd6ebac63212664352b9f6bea78b9d5b8435e5274'
counters=ET.parse(root/'test-results/pointer-comparison.trx').find('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters')
assert counters is not None and counters.attrib['total']=='1141' and counters.attrib['passed']=='1141' and counters.attrib['failed']=='0'
for path in ['Sia.Graphics/Sia.Spirv.Naga/bin/Release/net10.0/Sia.Spirv.Naga.dll','Sia.Graphics/Sia.Spirv.Naga.Tests/bin/Release/net10.0/Sia.Spirv.Naga.dll','.work/naga-csharp/pointer-selection-fixtures/bin/Release/net10.0/Sia.Spirv.Naga.dll']:
 assert hashlib.sha256(Path(path).read_bytes()).hexdigest()==digest,path
before=json.loads((root/'full-corpus/pre-pointer-null-baseline.json').read_text(encoding='utf-8'));after=json.loads((root/'full-corpus/manifest.json').read_text(encoding='utf-8'))
def codes(rows):return {r['input']:{n:s['code'] for n,s in r['steps'].items()} for r in rows}
assert len(after)==197 and codes(before)==codes(after)
reports={}
for directory,total in [('pointer-comparison',6609),('pointer-comparison-tail-control',712),('runtime-integer-conversion',1044),('pointer-null',6864)]:
 p=root/directory;rows=json.loads((p/'checks.json').read_text());assert len(rows)==total
 failed=[r for r in rows if not r['passed']]
 if directory=='pointer-comparison':
  native=[r for r in failed if r.get('code') is not None];gpu=[r for r in failed if 'device' in r]
  assert len(native)==27 and all(r.get('external') and 'VUID-StandaloneSpirv-None-10684' in r['message'] for r in native)
  workgroup=[r for r in gpu if r.get('failure_class')=='unresolved-original-workgroup-pointer-execution']
  cross=[r for r in gpu if r['check']=='scalar-loop-self-cross-32-False-32-input-gpu-0']
  assert len(workgroup)==56 and len(cross)==1 and len(gpu)==57
  assert all(r.get('external') and '-input-gpu-' in r['check'] and r['check'].startswith('workgroup-') for r in workgroup)
  assert rows[-1]==cross[0]
  validation=json.loads((p/'native-validation.json').read_text());assert len(validation)==174 and all(r['code']==0 for r in validation)
  original=json.loads((p/'first-failure.json').read_text());assert original['code']==1 and 'address equivalence' in original['message']
 else:
  assert json.loads((p/'assembly.json').read_text())['sha256']==digest
  if directory=='pointer-null':
   native=[r for r in failed if r.get('code') is not None];gpu=[r for r in failed if 'device' in r]
   assert len(native)==16 and all(r.get('external') and 'VUID-StandaloneSpirv-None-10684' in r['message'] for r in native)
   assert len(gpu)==96 and all(r.get('external') and r.get('failure_class')=='unresolved-original-workgroup-null-execution' for r in gpu)
  else:assert not failed
 if directory=='pointer-comparison-tail-control':
  assert len(json.loads((p/'fixtures.json').read_text()))==8
  for r in rows:
   if 'device' in r:
    assert r['pipeline_flags']==(1 if r['check'].startswith('scalar-loop-self-cross-') and ('-input-' in r['check'] or '-normalized-' in r['check']) else 0)
 reports[directory]=dict(total=total,passed=sum(r['passed'] for r in rows),failed=len(failed),gpu_total=sum('device' in r for r in rows),gpu_passed=sum('device' in r and r['passed'] for r in rows))
probe=json.loads((root/'pointer-comparison/workgroup-native-difference.json').read_text());assert probe['sha256']==digest
assert len(probe['rows'])==32 and sum(not r['matches'] for r in probe['rows'])==6
assert all(r['matches'] for r in probe['rows'] if r['file'].endswith('-normalized.spv'))
cross=json.loads((root/'pointer-comparison/cross-loop-native-difference.json').read_text());assert cross['sha256']==digest
assert len(cross['rows'])==80 and sum(not r['matches'] for r in cross['rows'])==8
assert all(r['matches'] for r in cross['rows'] if r['noopt'] or r['label'] not in ('input','normalized'))
summary=dict(sha256=digest,maintained_tests=1141,corpus_inputs=197,corpus_step_changes=[],reports=reports,
 comparison_native_default_run='failed; complete report retained',comparison_tail_control='passed; native cross-buffer loop inputs disable optimization',
 workgroup_probe=dict(outputs=32,failed_original=6,passed_normalized=16),cross_probe=dict(outputs=80,failed_default_native=8,passed_noopt_native=16,passed_translated=48))
(root/'pointer-comparison-summary.json').write_text(json.dumps(summary,indent=2));print(json.dumps(summary,indent=2))
