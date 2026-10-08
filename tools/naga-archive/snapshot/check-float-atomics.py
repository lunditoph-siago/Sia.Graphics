from pathlib import Path
import subprocess,json
root=Path('.work/naga-csharp/float-atomic');dotnet='.dotnet/dotnet.exe';harness='.work/naga-csharp/harness/bin/Release/net10.0/harness.dll';oracle='.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe';validator='.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe'
records=[]
def run(name,args,accepted=True):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=40)
 passed=(p.returncode==0)==accepted
 records.append(dict(check=name,passed=passed,code=p.returncode,expected_accept=accepted,message=p.stdout+p.stderr))
 (root/'checks.json').write_text(json.dumps(records,indent=2))
 if not passed: raise RuntimeError(name+': '+p.stdout+p.stderr)
for row in json.loads((root/'admission.json').read_text()):
 stem=row['space'].split(',')[0]+'-'+row['op'];path=root/(stem+'.wgsl');managed=root/(stem+'-managed.spv');accepted=row['code']==0
 run(stem+'-admission',[dotnet,harness,path,managed],accepted)
 if not accepted:continue
 for label,spv in [('managed',managed),('reference',root/(stem+'-reference.spv'))]:
  if label=='reference':run(stem+'-reference-spv',[oracle,path,spv])
  run(stem+'-'+label+'-validate',[validator,'--target-env','vulkan1.1',spv])
  wgsl=root/(stem+'-'+label+'-back.wgsl');run(stem+'-'+label+'-wgsl',[dotnet,harness,spv,wgsl]);run(stem+'-'+label+'-reference-wgsl',[oracle,wgsl])
source=Path('.reference/wgpu/naga/tests/in/wgsl/atomicOps-float32.wgsl')
for label,producer in [('managed',[dotnet,harness]),('reference',[oracle])]:
 spv=root/(label+'-fixture.spv');run(label+'-fixture-compile',producer+[source,spv]);run(label+'-fixture-validate',[validator,'--target-env','vulkan1.1',spv])
 for language in ['spv','wgsl']:
  output=root/(label+'-fixture-back.'+language);run(label+'-fixture-'+language,[dotnet,harness,spv,output]);run(label+'-fixture-'+language+'-reference',[oracle,output])
  if language=='spv':run(label+'-fixture-reemitted-validate',[validator,'--target-env','vulkan1.1',output])
print(f'{sum(r["passed"] for r in records)}/{len(records)} checks passed')
