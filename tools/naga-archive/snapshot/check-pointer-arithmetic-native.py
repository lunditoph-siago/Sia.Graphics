from pathlib import Path
import json, subprocess, hashlib
root=Path('.work/naga-csharp/pointer-arithmetic'); rows=[]
validator=['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.2']
baseline=['.dotnet/dotnet.exe','.work/naga-csharp/harness-before-pointer-arithmetic/harness.dll']
for case in json.loads((root/'fixtures.json').read_text()):
 for label in ['','-normalized']:
  path=root/(case['name']+label+'.spv')
  p=subprocess.run(validator+[str(path)],capture_output=True,text=True,timeout=30)
  rows.append(dict(name=case['name'],stage='normalized' if label else 'input',passed=p.returncode==0,code=p.returncode,message=p.stdout+p.stderr))
 p=subprocess.run(baseline+[str(root/(case['name']+'.spv')),str(root/'before.wgsl')],capture_output=True,text=True,timeout=30)
 rows.append(dict(name=case['name'],stage='baseline-rejection',passed=p.returncode!=0,code=p.returncode,message=p.stdout+p.stderr))
(root/'native-before.json').write_text(json.dumps(dict(assembly=hashlib.sha256(Path(baseline[1]).with_name('Sia.Spirv.Naga.dll').read_bytes()).hexdigest(),rows=rows),indent=2))
print(sum(r['passed'] for r in rows),'/',len(rows))
for row in rows:
 if not row['passed']: print(row)
assert all(r['passed'] for r in rows)
