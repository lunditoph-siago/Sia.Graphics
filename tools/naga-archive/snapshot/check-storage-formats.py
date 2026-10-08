from pathlib import Path
import json,subprocess
root=Path('.work/naga-csharp/storage-formats');harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll'];oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe'];validator=['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.1'];rows=[]
def run(label,args,accepted=True):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=30);passed=(p.returncode==0)==accepted;rows.append(dict(check=label,passed=passed,code=p.returncode,message=p.stdout+p.stderr));(root/'checks.json').write_text(json.dumps(rows,indent=2))
 if not passed:raise RuntimeError(label+': '+p.stdout+p.stderr)
for entry in json.loads((root/'baseline.json').read_text()):
 fmt=entry['format'];source=root/(fmt+'.wgsl');spv=root/(fmt+'-managed.spv');wgsl=root/(fmt+'-back.wgsl');back=root/(fmt+'-back.spv')
 run(fmt+'-compile',harness+[source,spv]);run(fmt+'-native',validator+[spv]);run(fmt+'-canonical-wgsl',harness+[source,root/(fmt+'-canonical.wgsl')]);run(fmt+'-reference-canonical',oracle+[root/(fmt+'-canonical.wgsl')])
 if fmt=='bgra8unorm':run(fmt+'-formatless',harness+[spv,wgsl],False);continue
 for name,args in [('back',harness+[spv,wgsl]),('reference-wgsl',oracle+[wgsl]),('reemit',harness+[spv,back]),('native-reemit',validator+[back]),('reference-input-back',harness+[root/(fmt+'-reference.spv'),root/(fmt+'-reference-back.wgsl')]),('reference-input-wgsl',oracle+[root/(fmt+'-reference-back.wgsl')])]:run(fmt+'-'+name,args)
print(f'{sum(r["passed"] for r in rows)}/{len(rows)} checks passed')
