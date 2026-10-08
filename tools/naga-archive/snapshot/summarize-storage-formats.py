from pathlib import Path
import json,collections
r=json.loads(Path('.work/naga-csharp/storage-formats-gpu/report.json').read_text());print('GPU comparisons',sum(x['check'].endswith('-gpu') and 'passed' in x for x in r));print('passed',sum(x.get('passed') is True for x in r),'failed',sum(x.get('passed') is False for x in r),'skipped',sum(x.get('skipped',False) for x in r));print('formats',len({x['check'].split('-')[0] for x in r if x['check'].endswith('-gpu') and 'passed' in x}))
