from pathlib import Path
import json,struct,subprocess,sys
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
from vulkan_compute import Vulkan
root=Path('.work/naga-csharp/pointer-comparison');native=Path('.work/naga-csharp/spirv-tools-local/tools/Release');rows=[]
text=(root/'workgroup-original.spvasm').read_text()
for stride in [1,4,8]:
 source=root/f'workgroup-pointer-stride-{stride}.spvasm';binary=source.with_suffix('.spv')
 position=text.index('       %uint = OpTypeInt')
 source.write_text(text[:position]+f'OpDecorate %_ptr_Workgroup_uint ArrayStride {stride}\n'+text[position:])
 for label,command in [('assemble',[native/'spirv-as.exe','--target-env','spv1.4',source,'-o',binary]),('validate',[native/'spirv-val.exe','--target-env','vulkan1.2',binary])]:
  result=subprocess.run(list(map(str,command)),capture_output=True,text=True)
  rows.append(dict(stride=stride,check=label,code=result.returncode,message=result.stdout+result.stderr))
  if result.returncode:break
 else:
  with Vulkan(variablePointers='full') as vk:
   data=struct.pack('<17I',1,1,0,0,0,0,0,0,11,21,31,41,111,121,131,141,919)
   actual=list(struct.unpack('<4I',vk.execute(binary.read_bytes(),data,16,1)))
   rows.append(dict(stride=stride,check='gpu',actual=actual,expected=[0,1,1,21],matches=actual==[0,1,1,21]))
(root/'workgroup-pointer-stride-probe.json').write_text(json.dumps(rows,indent=2));print(json.dumps(rows,indent=2))
