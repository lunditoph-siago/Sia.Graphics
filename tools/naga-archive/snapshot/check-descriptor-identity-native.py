from pathlib import Path
import json,subprocess,hashlib
root=Path('.work/naga-csharp/pointer-descriptor-identity');rows=[]
validator=['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.2']
baseline=['.dotnet/dotnet.exe','.work/naga-csharp/harness-before-descriptor-identity/harness.dll']
for c in json.loads((root/'fixtures.json').read_text()):
 for suffix in ['','-normalized']:
  p=subprocess.run(validator+[str(root/(c['name']+suffix+'.spv'))],capture_output=True,text=True,timeout=30)
  rows.append(dict(name=c['name'],stage='normalized' if suffix else 'input',passed=p.returncode==0,code=p.returncode,message=p.stdout+p.stderr))
 p=subprocess.run(baseline+[str(root/(c['name']+'.spv')),str(root/'before.wgsl')],capture_output=True,text=True,timeout=30)
 rows.append(dict(name=c['name'],stage='baseline',accepted=p.returncode==0,code=p.returncode,message=p.stdout+p.stderr))
for path in list(root.glob('independent-*.spv'))+list(root.glob('temporal-*.spv'))+[root/'repeated-call.spv']:
 p=subprocess.run(validator+[str(path)],capture_output=True,text=True,timeout=30)
 rows.append(dict(name=path.stem,stage='negative-input',passed=p.returncode==0,code=p.returncode,message=p.stdout+p.stderr))
(root/'native-before.json').write_text(json.dumps(dict(sha256=hashlib.sha256(Path(baseline[1]).with_name('Sia.Spirv.Naga.dll').read_bytes()).hexdigest(),rows=rows),indent=2))
print('Native',sum(r['passed'] for r in rows if 'passed' in r),'/',sum('passed' in r for r in rows))
print('Baseline accepted',sum(r.get('accepted',False) for r in rows),'/',sum(r['stage']=='baseline' for r in rows))
for r in rows:
 if r.get('passed',True)==False:print(r)
assert all(r.get('passed',True) for r in rows)
