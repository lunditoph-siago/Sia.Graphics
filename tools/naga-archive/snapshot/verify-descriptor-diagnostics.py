from pathlib import Path
import hashlib,json,subprocess
base=Path('.work/naga-csharp');harness=['.dotnet/dotnet.exe',str(base/'harness/bin/Release/net10.0/harness.dll')]
validator=[str(base/'spirv-tools-local/tools/Release/spirv-val.exe'),'--target-env','vulkan1.2'];rows=[]
digest=hashlib.sha256(Path(harness[1]).with_name('Sia.Spirv.Naga.dll').read_bytes()).hexdigest()
previous=json.loads((base/'pointer-comparison-types/before.json').read_text())
assert len(previous)==6 and all(r['native']['code']==1 and r['managed']['code']==0 for r in previous)
for prior in previous:
 path=base/'pointer-comparison-types'/(prior['name']+'.spv');r=subprocess.run(harness+[str(path),str(path.with_suffix('.after.wgsl'))],capture_output=True,text=True,timeout=60)
 row=dict(name=prior['name'],code=r.returncode,message=r.stdout+r.stderr,passed=r.returncode==1 and 'operand type mismatch' in r.stdout)
 rows.append(row);assert row['passed'],row
for mode in ['select','loop','nested']:
 path=base/'pointer-descriptor-array'/('alias-'+mode+'.spv')
 for label,command in [('native',validator+[str(path)]),('managed',harness+[str(path),str(path.with_suffix('.wgsl'))])]:
  r=subprocess.run(command,capture_output=True,text=True,timeout=60)
  passed=r.returncode==0 if label=='native' else r.returncode==1 and 'potentially aliased descriptor' in r.stdout
  row=dict(name='alias-'+mode+'-'+label,code=r.returncode,message=r.stdout+r.stderr,passed=passed);rows.append(row);assert passed,row
path=base/'pointer-descriptor-array/alias-difference.spv'
for label,command in [('native',validator+[str(path)]),('managed',harness+[str(path),str(path.with_suffix('.wgsl'))])]:
 r=subprocess.run(command,capture_output=True,text=True,timeout=60)
 passed=r.returncode==0 if label=='native' else r.returncode==1 and 'potentially aliased bindings' in r.stdout
 row=dict(name='alias-difference-'+label,code=r.returncode,message=r.stdout+r.stderr,passed=passed);rows.append(row);assert passed,row
report=dict(sha256=digest,checks=rows)
(base/'pointer-descriptor-array/diagnostics.json').write_text(json.dumps(report,indent=2));print('diagnostic checks',sum(r['passed'] for r in rows),'/',len(rows))
