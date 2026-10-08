from pathlib import Path
import collections,hashlib,json,struct,xml.etree.ElementTree as ET
task=Path('.work/naga-csharp');root=task/'pointer-arithmetic'
digest='0b3ed0caf77777b87b80cc424c5a676b0631031f3b03377cf9bb8903b593cc4d'
for path in ['Sia.Graphics/Sia.Spirv.Naga/bin/Release/net10.0/Sia.Spirv.Naga.dll','Sia.Graphics/Sia.Spirv.Naga.Tests/bin/Release/net10.0/Sia.Spirv.Naga.dll',str(task/'harness/bin/Release/net10.0/Sia.Spirv.Naga.dll'),str(task/'pointer-selection-fixtures/bin/Release/net10.0/Sia.Spirv.Naga.dll')]:
 assert hashlib.sha256(Path(path).read_bytes()).hexdigest()==digest,path
assert json.loads((root/'assembly.json').read_text())['sha256']==digest
counters=ET.parse(task/'test-results/pointer-arithmetic-full.trx').find('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters')
assert counters is not None and counters.attrib['total']=='1202' and counters.attrib['passed']=='1202' and counters.attrib['failed']=='0'
fixtures=json.loads((root/'fixtures.json').read_text());rows=json.loads((root/'checks.json').read_text());assert len(fixtures)==91 and len(rows)==8235
def narrow(c):return abs(c['width'])==16 and c['offset'] in ('backward','chained','selected')
expected={f"{c['name']}-{path}-{seed}-{target}" for c in fixtures for path in ['input','normalized','roundtrip','reference','managed']+(['widened'] if narrow(c) else []) for seed in range(8) for target in ['output','input-bytes']}
gpu=[r for r in rows if 'device' in r];assert len(gpu)==7408 and {r['check'] for r in gpu}==expected
assert len({r['check'] for r in rows})==len(rows)
failed=[r for r in rows if not r['passed']];assert len(failed)==88
for r in failed:
 assert r.get('external') and r.get('failure_class')=='native-16-bit-pointer-element-zero-extension'
 case=next(c for c in fixtures if r['check'].startswith(c['name']+'-'))
 assert narrow(case) and r['check'].startswith(case['name']+'-input-')
 if r['check'].endswith('-output'):
  actual=struct.unpack('<4I',bytes.fromhex(r['actual']));expected_output=struct.unpack('<4I',bytes.fromhex(r['expected']))
  assert (actual[2]-expected_output[2])&0xffffffff==65536*(2 if case['kind']=='aggregate' else 1)
for suffix in ['input-native','normalized-native','back-native','reference-native','managed-native']:
 selected=[r for r in rows if r['check'].endswith('-'+suffix)];assert len(selected)==91 and all(r['passed'] for r in selected)
widened=[r for r in gpu if '-widened-' in r['check']];assert len(widened)==128 and all(r['passed'] for r in widened)
before=json.loads((root/'native-before.json').read_text());assert before['assembly']=='197d5f521328beccacb2e838377234c8654e914fef7ce286ac26fe412276935f'
assert len(before['rows'])==273 and all(r['passed'] for r in before['rows'])
old=json.loads((task/'full-corpus/pre-pointer-arithmetic-baseline.json').read_text(encoding='utf-8'));new=json.loads((task/'full-corpus/manifest.json').read_text(encoding='utf-8'))
def codes(rs):return {r['input']:{name:step['code'] for name,step in r['steps'].items()} for r in rs}
assert len(new)==197 and codes(old)==codes(new)
summary=dict(sha256=digest,maintained_tests=1202,new_tests=21,fixtures=91,total_checks=len(rows),passed=len(rows)-len(failed),failed=len(failed),gpu_readbacks=len(gpu),gpu_dispatches=len(gpu),
 native_validation_checks=463,baseline_checks=273,widened_control_readbacks=len(widened),external_failure_class='native-16-bit-pointer-element-zero-extension',
 external_failed_outputs=sum(r['check'].endswith('-output') for r in failed),external_failed_input_readbacks=sum(r['check'].endswith('-input-bytes') for r in failed),
 normalized_and_translated_failures=0,devices=sorted({r['device'] for r in gpu}),corpus_inputs=197,corpus_step_changes=[],
 limits='Storage GPU coverage; Workgroup arithmetic maintained tests only. Prior pointer/descriptor reports use earlier assembly hashes. Browser and other devices not rerun.')
(task/'pointer-arithmetic-summary.json').write_text(json.dumps(summary,indent=2));print(json.dumps(summary,indent=2))
