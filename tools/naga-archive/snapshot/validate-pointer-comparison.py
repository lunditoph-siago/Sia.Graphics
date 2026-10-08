from pathlib import Path
import hashlib,json,subprocess
p=Path('.work/naga-csharp/pointer-comparison');rows=[]
fixtures=json.loads((p/'fixtures.json').read_text())
for case in fixtures:
 for suffix in ['.spv','-normalized.spv']:
  path=p/(case['name']+suffix)
  result=subprocess.run(['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.2',str(path)],capture_output=True,text=True,timeout=60)
  rows.append(dict(file=path.name,code=result.returncode,message=result.stdout+result.stderr))
(p/'native-validation.json').write_text(json.dumps(rows,indent=2))
print('fixtures',len(fixtures),'native passed',sum(r['code']==0 for r in rows),'/',len(rows))
for r in rows:
 if r['code']:print(r)
assert all(r['code']==0 for r in rows)
h=Path('.work/naga-csharp/harness/bin/Release/net10.0/harness.dll')
digest=hashlib.sha256(h.with_name('Sia.Spirv.Naga.dll').read_bytes()).hexdigest()
if digest=='bd4d477d4aa7ced5378216eb583c63a4ed8920b5a1ff1be5d99e232fbdc51ef7':
 path=p/'scalar-select-other-32-False-32.spv'
 result=subprocess.run(['.dotnet/dotnet.exe',str(h),str(path),str(p/'pre-feature.wgsl')],capture_output=True,text=True,timeout=60)
 record=dict(sha256=digest,input=path.name,code=result.returncode,message=result.stdout+result.stderr)
 (p/'first-failure.json').write_text(json.dumps(record,indent=2));print(record)
 assert result.returncode and 'address equivalence' in record['message']
