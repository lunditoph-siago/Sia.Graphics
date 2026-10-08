from pathlib import Path
import json,subprocess
root=Path('.work/naga-csharp/readonly-pointer-selection');rows=[]
for case in json.loads((root/'fixtures.json').read_text()):
 path=root/(case['name']+'.spv')
 args=['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.2',str(path)]
 p=subprocess.run(args,capture_output=True,text=True,timeout=30)
 rows.append(dict(name=case['name'],passed=p.returncode==0,code=p.returncode,message=p.stdout+p.stderr,command=args))
(root/'initial-native-validation.json').write_text(json.dumps(rows,indent=2))
print(sum(r['passed'] for r in rows),'/',len(rows),'native inputs valid')
assert all(r['passed'] for r in rows),[r for r in rows if not r['passed']]
