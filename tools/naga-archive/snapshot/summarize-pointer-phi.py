from pathlib import Path
import hashlib
import json

root = Path('.work/naga-csharp')
before = json.loads((root / 'full-corpus/pre-pointer-phi-baseline.json').read_text(encoding='utf-8'))
after = json.loads((root / 'full-corpus/manifest.json').read_text(encoding='utf-8'))
def codes(rows):
    return {r['input']: {name: step['code'] for name, step in r['steps'].items()} for r in rows}
a, b = codes(before), codes(after)
changes = [dict(input=name, before=a.get(name), after=b.get(name))
           for name in sorted(a.keys() | b.keys()) if a.get(name) != b.get(name)]
digest = hashlib.sha256((root / 'harness/bin/Release/net10.0/Sia.Spirv.Naga.dll').read_bytes()).hexdigest()
assert digest == hashlib.sha256(Path('Sia.Graphics/Sia.Spirv.Naga/bin/Release/net10.0/Sia.Spirv.Naga.dll').read_bytes()).hexdigest()
reports = {}
for name, external_failures in [('pointer-phi', 3), ('pointer-selection', 2), ('atomic-memory', 0)]:
    rows = json.loads((root / name / 'checks.json').read_text())
    failed = [r for r in rows if not r['passed']]
    assert len(failed) == external_failures
    assert all(r.get('external') and 'VUID-StandaloneSpirv-None-10684' in r['message'] for r in failed)
    if name != 'atomic-memory':
        assert json.loads((root / name / 'assembly.json').read_text())['sha256'] == digest
    reports[name] = dict(total=len(rows), passed=sum(r['passed'] for r in rows), failed=failed,
                         gpu=sum('actual' in r and '-bytes' not in r['check'] for r in rows),
                         bytes=sum('-bytes-' in r['check'] for r in rows))
phi = json.loads((root / 'pointer-phi/checks.json').read_text())
for suffix in ['-input-native', '-normalized-native']:
    selected = [r for r in phi if r['check'].endswith(suffix)]
    assert len(selected) == 38 and all(r['passed'] for r in selected)
assert not changes
summary = dict(sha256=digest, corpus_inputs=len(after), corpus_step_changes=changes, reports=reports)
(root / 'pointer-phi-summary.json').write_text(json.dumps(summary, indent=2))
print(json.dumps(dict(sha256=digest, corpus_inputs=len(after), corpus_step_changes=changes,
                     reports={name: {k: v for k, v in report.items() if k != 'failed'}
                              for name, report in reports.items()}), indent=2))
