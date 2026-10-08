from pathlib import Path
import collections,hashlib,json,xml.etree.ElementTree as ET
root=Path('.work/naga-csharp');digest=hashlib.sha256((root/'harness/bin/Release/net10.0/Sia.Spirv.Naga.dll').read_bytes()).hexdigest()
assert digest=='197d5f521328beccacb2e838377234c8654e914fef7ce286ac26fe412276935f'
for path in ['Sia.Graphics/Sia.Spirv.Naga/bin/Release/net10.0/Sia.Spirv.Naga.dll','Sia.Graphics/Sia.Spirv.Naga.Tests/bin/Release/net10.0/Sia.Spirv.Naga.dll','.work/naga-csharp/pointer-selection-fixtures/bin/Release/net10.0/Sia.Spirv.Naga.dll']:
 assert hashlib.sha256(Path(path).read_bytes()).hexdigest()==digest,path
counters=ET.parse(root/'test-results/pointer-descriptor-array.trx').find('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters')
assert counters is not None and counters.attrib['total']=='1181' and counters.attrib['passed']=='1181' and counters.attrib['failed']=='0'
reports={}
for directory,count,total in [('pointer-descriptor-array',40,9960),('pointer-descriptor-array-dynamic',16,3984)]:
 p=root/directory;fixtures=json.loads((p/'fixtures.json').read_text());rows=json.loads((p/'checks.json').read_text())
 assert json.loads((p/'assembly.json').read_text())['sha256']==digest and len(fixtures)==count and len(rows)==total and all(r['passed'] for r in rows)
 gpu=[r for r in rows if 'device' in r];expected={f"{c['name']}-{path}-{alias}-{seed}-{target}" for c in fixtures for path in ['input','normalized','roundtrip','reference','managed'] for alias in [False,True] for seed in range(8) for target in ['output','first','second']}
 assert len(gpu)==len(expected) and {r['check'] for r in gpu}==expected
 for suffix in ['-input-native','-normalized-native','-back-native','-reference-native','-managed-native']:
  selected=[r for r in rows if r['check'].endswith(suffix)];assert len(selected)==count
 reports[directory]=dict(fixtures=count,total=total,passed=total,gpu_readbacks=len(gpu),dispatches=len(gpu)//3,devices=sorted(set(r['device'] for r in gpu)))
negative=json.loads((root/'pointer-descriptor-array/diagnostics.json').read_text());assert negative['sha256']==digest and len(negative['checks'])==14 and all(r['passed'] for r in negative['checks'])
before=json.loads((root/'full-corpus/pre-pointer-descriptor-baseline.json').read_text(encoding='utf-8'));after=json.loads((root/'full-corpus/manifest.json').read_text(encoding='utf-8'))
def codes(rows):return {r['input']:{n:s['code'] for n,s in r['steps'].items()} for r in rows}
assert len(after)==197 and codes(before)==codes(after)
features=json.loads((root/'descriptor-features.json').read_text());assert features['features']['shaderStorageBufferArrayNonUniformIndexing']
summary=dict(sha256=digest,maintained_tests=1181,reports=reports,diagnostic_checks=14,corpus_inputs=197,corpus_step_changes=[],nonuniform_storage_descriptor_feature=features,
 older_pointer_null_comparison_and_native_difference_reports='historical C909 assembly; not rerun against this hash')
(root/'pointer-descriptor-summary.json').write_text(json.dumps(summary,indent=2));print(json.dumps(summary,indent=2))
