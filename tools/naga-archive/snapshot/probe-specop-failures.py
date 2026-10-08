import pathlib,subprocess,struct,json
from vulkan_compute import Vulkan
root=pathlib.Path(__file__).resolve().parents[2];task=root/'.work/naga-csharp';work=task/'specop-probes';work.mkdir(exist_ok=True)
dotnet=root/'.dotnet/dotnet.exe';harness=task/'harness/bin/Release/net10.0/harness.dll';oracle=task/'oracle-target/release/sia-naga-reference-oracle.exe';validator=task/'spirv-tools-local/tools/Release/spirv-val.exe'
records=[]
for label,expression in [('min-sub','(-9223372036854775807li - 1li)'),('min-literal','-9223372036854775808li'),('min-convert','i64(9223372036854775808lu)'),('min-bitcast','bitcast<i64>(9223372036854775808lu)')]:
 path=work/f'{label}.wgsl';path.write_text(f'var<private> value:i64={expression}; @compute @workgroup_size(1) fn main() {{}}',encoding='utf-8')
 p=subprocess.run([str(oracle),str(path)],capture_output=True,text=True);records.append(dict(case=label,exit=p.returncode,message=p.stdout+p.stderr));print(records[-1],flush=True)
for label,stores in [('adjacent','outputs[120]=2147483649u;outputs[121]=0u;outputs[122]=2147483649u;outputs[123]=0u;outputs[124]=2147483648u;outputs[125]=0u;'),('minimal-constants','outputs[24]=1u;outputs[25]=0u;outputs[26]=1u;outputs[27]=0u;outputs[120]=2147483649u;outputs[121]=0u;outputs[122]=2147483649u;outputs[123]=0u;outputs[124]=2147483648u;'),('dispersed','outputs[0]=2147483649u;outputs[2]=2147483649u;outputs[4]=2147483648u;'),('input','outputs[24]=1u;outputs[25]=0u;outputs[26]=1u;outputs[27]=0u;outputs[120]=inputs[0];outputs[121]=0u;outputs[122]=inputs[0];outputs[123]=0u;outputs[124]=inputs[1];outputs[125]=0u;')]:
 path=work/f'{label}.wgsl';path.write_text('@group(0) @binding(0) var<storage,read> inputs:array<u32>; @group(0) @binding(1) var<storage,read_write> outputs:array<u32>; @compute @workgroup_size(1) fn main(){'+stores+'}',encoding='utf-8')
 for writer,compiler in [('managed',[dotnet,harness]),('reference',[oracle])]:
  spv=work/f'{label}-{writer}.spv'
  for args in [compiler+[path,spv],[validator,'--target-env','vulkan1.1',spv]]:
   p=subprocess.run(list(map(str,args)),capture_output=True,text=True)
   if p.returncode:raise RuntimeError(p.stdout+p.stderr)
  with Vulkan() as gpu:
   raw=gpu.execute(spv.read_bytes(),struct.pack('<4I',2147483649,2147483648,0,0),130*4,1);words=struct.unpack('<130I',raw)
   indices=[0,2,4] if label=='dispersed' else [120,122,124]
   records.append(dict(case=f'{label}-{writer}',device=gpu.name,values=[words[i] for i in indices]));print(records[-1],flush=True)
(work/'report.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
assembler=validator.with_name('spirv-as.exe')
text=(task/'specop-gpu/int32.spvasm').read_text(); prefix,body=text.split('%main = OpFunction')
for mode in ['sparse','constant','prefix']:
 lines=body.splitlines();kept=[]
 for line in lines:
  if line.startswith('OpStore '):
   index=int(line.split()[1][4:])
   if mode!='prefix' and index not in [120,122,124]:continue
   if mode=='prefix' and index>124:continue
   if mode=='constant' and index in [120,122]:line=line.rsplit(' ',1)[0]+' %test_const'
  kept.append(line)
 source=work/f'isolated-{mode}.spvasm';source.write_text(prefix+'%test_const = OpConstant %u32 2147483649\n%main = OpFunction'+'\n'.join(kept),encoding='utf-8');spv=source.with_suffix('.spv')
 for args in [[assembler,'--target-env','spv1.3',source,'-o',spv],[validator,'--target-env','vulkan1.1',spv]]:
  p=subprocess.run(list(map(str,args)),capture_output=True,text=True)
  if p.returncode:raise RuntimeError(p.stdout+p.stderr)
 with Vulkan() as gpu:
  raw=gpu.execute(spv.read_bytes(),bytes(16),208*4,1);words=struct.unpack('<208I',raw)
  records.append(dict(case=f'isolated-{mode}',values=[words[i] for i in [120,122,124]]));print(records[-1],flush=True)
(work/'report.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
