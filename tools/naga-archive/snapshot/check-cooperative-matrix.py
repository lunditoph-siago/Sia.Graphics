from pathlib import Path
import ctypes as C, hashlib, json, re, struct, subprocess
from vulkan_compute import Vulkan, Features2, S, head, u, p, i, addr, check

root=Path('.work/naga-csharp/cooperative');root.mkdir(exist_ok=True)
rows=[]
if (root/'checks.json').exists() and not (root/'initial-checks.json').exists():
 (root/'initial-checks.json').write_text((root/'checks.json').read_text())
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll']
oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
native=['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.1']
def save(): (root/'checks.json').write_text(json.dumps(rows,indent=2))
def run(name,args,rejection=None):
 result=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=45)
 log=result.stdout+result.stderr
 passed=result.returncode==0 if rejection is None else result.returncode!=0 and rejection in log
 rows.append(dict(check=name,passed=passed,code=result.returncode,expected_rejection=rejection,message=log));save()
 if not passed:raise RuntimeError(name+': '+log)
 return result

old=root/'operations.wgsl'
if old.exists() and not (root/'initial-unary-source.wgsl').exists():
 (root/'initial-unary-source.wgsl').write_text(old.read_text())
 run('initial-unary-reference-rejection',oracle+[root/'initial-unary-source.wgsl'], 'InvalidUnaryOperandType')
tests=Path('Sia.Graphics/Sia.Spirv.Naga.Tests/CooperativeMatrixTests.cs').read_text()
source=re.search(r'internal const string Source = """\n(.*?)\n\s*""";',tests,re.S)[1]
source='\n'.join(line[8:] for line in source.splitlines())
cases={}
for size in (8,16):
 for half in (False,True):
  text=source.replace('8x8',f'{size}x{size}')
  if half:text='enable f16;\n'+text.replace('f32','f16')
  cases[f'operations-{size}-'+('f16' if half else 'f32')]=text
cases['mixed']='enable f16;\n'+source.replace('var<storage,read> inputs:array<f32>','var<storage,read> inputs:array<f16>').replace('coop_mat8x8<f32,A>','coop_mat8x8<f16,A>').replace('coop_mat8x8<f32,B>','coop_mat8x8<f16,B>')
cases['workgroup']=source.replace('@group(0) @binding(0) var<storage,read> inputs:array<f32>','var<workgroup> inputs:array<f32,256>')
cases['vector-memory']=source.replace('inputs:array<f32>','inputs:array<vec4f>').replace('outputs:array<f32>','outputs:array<vec4f>')
cases['override-stride']=source.replace('enable wgpu_cooperative_matrix;','enable wgpu_cooperative_matrix;\n@id(3) override stride=8u;').replace('16u','stride')
cases['device-scope']=source.replace('fn multiply(', '@group(0) @binding(2) var<storage,read_write> count:atomic<u32>; fn multiply(').replace('var c=', '_=atomicAdd(&count,1u); storageBarrier(); var c=')
cases['inferred-component']='enable f16; enable wgpu_cooperative_matrix; @group(0) @binding(0) var<storage,read> data:array<f16>; @compute @workgroup_size(32) fn main(){let value=coopLoad<coop_mat8x8<f32,C>>(&data[0]);}'
cases['containers']='enable wgpu_cooperative_matrix; alias M=coop_mat16x16<f32,C>; struct S{value:M} var<private> values:array<S,2>; @compute @workgroup_size(32) fn main(){var x:M; values[0].value=x;}'
for fixture in ('cooperative-matrix','cooperative-matrix-mixed-accumulator'):
 cases['upstream-'+fixture]=Path('.reference/wgpu/naga/tests/in/wgsl/'+fixture+'.wgsl').read_text()

for name,text in cases.items():
 path=root/(name+'.wgsl');path.write_text(text)
 canonical=root/(name+'-canonical.wgsl');spv=root/(name+'-managed.spv');back=root/(name+'-back.wgsl');ref=root/(name+'-reference.spv')
 run(name+'-reference-input',oracle+[path])
 run(name+'-canonical',harness+[path,canonical]);run(name+'-reference-canonical',oracle+[canonical])
 run(name+'-managed-compile',harness+[path,spv]);run(name+'-managed-native',native+[spv])
 run(name+'-binary-roundtrip',harness+[spv,root/(name+'-roundtrip.spv')]);run(name+'-roundtrip-native',native+[root/(name+'-roundtrip.spv')])
 run(name+'-reverse',harness+[spv,back]);run(name+'-reverse-reference',oracle+[back])
 reference_native_rejection={'workgroup':'Invalid explicit layout decorations','device-scope':'requires the VulkanMemoryModelDeviceScopeKHR capability'}.get(name)
 run(name+'-reverse-reference-compile',oracle+[back,root/(name+'-back-reference.spv'),'']);run(name+'-reverse-reference-native',native+[root/(name+'-back-reference.spv')],reference_native_rejection)
 run(name+'-reference-compile',oracle+[path,ref,'']);run(name+'-reference-native',native+[ref], 'Capability CooperativeMatrixKHR is not allowed' if name=='inferred-component' else reference_native_rejection)
 imported=root/(name+'-reference-import.wgsl');run(name+'-reference-import',harness+[ref,imported]);run(name+'-reference-import-validation',oracle+[imported])
 run(name+'-reference-import-compile',oracle+[imported,root/(name+'-import-reference.spv'),'']);run(name+'-reference-import-native',native+[root/(name+'-import-reference.spv')],reference_native_rejection)
 run(name+'-reference-binary-reemit',harness+[ref,root/(name+'-reference-reemit.spv')]);run(name+'-reference-reemit-native',native+[root/(name+'-reference-reemit.spv')])
 print(name,'passed',flush=True)

original=(root/'operations-8-f32-managed.spv').read_bytes()
def mutate(data,change):
 words=list(struct.unpack('<'+'I'*(len(data)//4),data));output=words[:5];offset=5
 while offset<len(words):
  count=words[offset]>>16;op=words[offset]&65535;operands=words[offset+1:offset+count]
  operands=change(op,operands)
  if isinstance(operands,tuple):op,operands=operands
  output += [((len(operands)+1)<<16)|op]+operands;offset+=count
 return struct.pack('<'+'I'*len(output),*output)
for label,change in [('explicit-none',lambda op,args:args+[0] if op in (4457,4458) else args),('aligned',lambda op,args:args+[2,4] if op in (4457,4458) else args)]:
 path=root/(label+'.spv');path.write_bytes(mutate(original,change));run(label+'-native',native+[path])
 if label=='explicit-none':
  run(label+'-import',harness+[path,root/(label+'.wgsl')]);run(label+'-reference',oracle+[root/(label+'.wgsl')])
 else:run(label+'-explicit-rejection',harness+[path,root/(label+'.wgsl')],'per-access memory operands')

words=list(struct.unpack('<'+'I'*(len(original)//4),original));instructions=[];offset=5
while offset<len(words):
 count=words[offset]>>16;instructions.append((words[offset]&65535,words[offset+1:offset+count]));offset+=count
coop_ids={args[0] for op,args in instructions if op==4456}
float_id=next(args[0] for op,args in instructions if op==22 and args[1]==32)
uint_id=next(args[0] for op,args in instructions if op==21 and args[1:]==[32,0])
scalar_id=next(args[1] for op,args in instructions if op==43 and args[0]==float_id)
variants={
 'native-negation':(lambda op,args:(127,args[:3]) if op==131 and args[0] in coop_ids else args,None),
 'scalar-splat':(lambda op,args:(44,args+[scalar_id]) if op==46 and args[0] in coop_ids else args,'cannot express a scalar splat'),
 'pointer-reinterpret':(lambda op,args:[args[0],uint_id] if op==29 and args[1]==float_id else [args[0],args[1],uint_id] if op==32 and args[1:]==[12,float_id] else args,'reinterpretation requires equivalent lowering')
}
for label,(change,rejection) in variants.items():
 path=root/(label+'.spv');data=mutate(original,change)
 if label=='scalar-splat':
  # A new constant dependency must be declared before its composite use.
  declaration=next(args for op,args in instructions if op==43 and args[1]==scalar_id)
  words=list(struct.unpack('<'+'I'*(len(data)//4),data));output=words[:5];offset=5;inserted=False
  while offset<len(words):
   count=words[offset]>>16;op=words[offset]&65535;args=words[offset+1:offset+count]
   if op==44 and args[0] in coop_ids and not inserted:
    output += [((len(declaration)+1)<<16)|43]+declaration;inserted=True
   if not (op==43 and args[1]==scalar_id):output+=words[offset:offset+count]
   offset+=count
  assert inserted;data=struct.pack('<'+'I'*len(output),*output)
 if label=='pointer-reinterpret':
  declaration=next(args for op,args in instructions if op==21 and args[0]==uint_id)
  words=list(struct.unpack('<'+'I'*(len(data)//4),data));output=words[:5];offset=5;inserted=False
  while offset<len(words):
   count=words[offset]>>16;op=words[offset]&65535;args=words[offset+1:offset+count]
   if op==29 and not inserted:output += [((len(declaration)+1)<<16)|21]+declaration;inserted=True
   if not (op==21 and args[0]==uint_id):output+=words[offset:offset+count]
   offset+=count
  assert inserted;data=struct.pack('<'+'I'*len(output),*output)
 path.write_bytes(data);run(label+'-native',native+[path])
 reemit=root/(label+'-reemitted.spv');run(label+'-reemit',harness+[path,reemit]);run(label+'-reemit-native',native+[reemit])
 wgsl=root/(label+'.wgsl');run(label+'-wgsl',harness+[path,wgsl],rejection)
 if rejection is None:
  run(label+'-wgsl-reference',oracle+[wgsl]);run(label+'-reference-compile',oracle+[wgsl,root/(label+'-reference.spv')]);run(label+'-reference-native',native+[root/(label+'-reference.spv')])

for value in (8,16):
 for implementation,command in [('managed',harness),('reference',oracle)]:
  label=f'stride-resolved-{value}-{implementation}';path=root/(label+'.spv')
  run(label+'-compile',command+[root/'override-stride.wgsl',path,f'3={value}']);run(label+'-native',native+[path])
  run(label+'-reverse',harness+[path,root/(label+'.wgsl')]);run(label+'-reverse-reference',oracle+[root/(label+'.wgsl')])

for attribute in ('coherent','volatile'):
 path=root/(attribute+'.wgsl');path.write_text(source.replace('@group(0) @binding(0)','@'+attribute+' @group(0) @binding(0)'))
 run(attribute+'-reference-source',oracle+[path]);run(attribute+'-managed-rejection',harness+[path,root/(attribute+'.spv')],'per-access lowering')
 ref=root/(attribute+'-reference.spv');run(attribute+'-reference-compile',oracle+[path,ref])
 run(attribute+'-reference-native-rejection',native+[ref],attribute.capitalize())

Coop=S('CooperativeMatrixFeatures',head+[('cooperativeMatrix',u),('robust',u)])
Extension=S('Extension',[('name',C.c_char*256),('version',u)])
with Vulkan() as vk:
 feature=Coop(1000506000,None,0,0);features=Features2(1000059000,addr(feature));vk.vkGetPhysicalDeviceFeatures2(vk.physical,C.byref(features))
 enumerate_ext=vk.lib.vkEnumerateDeviceExtensionProperties;enumerate_ext.restype=i;enumerate_ext.argtypes=[p,p,p,p]
 count=u();check(enumerate_ext(vk.physical,None,C.byref(count),None),'enumerate extension count');exts=(Extension*count.value)();check(enumerate_ext(vk.physical,None,C.byref(count),exts),'enumerate extensions')
 names=[ext.name.decode() for ext in exts]
 device=dict(name=vk.name,cooperativeMatrix=bool(feature.cooperativeMatrix),robust=bool(feature.robust),extension='VK_KHR_cooperative_matrix' in names)
 (root/'device.json').write_text(json.dumps(device,indent=2));print(device,flush=True)
 if device['cooperativeMatrix'] and device['extension']:raise RuntimeError('Native cooperative device execution still needs an enabled-feature runner')
print(sum(row['passed'] for row in rows),'/',len(rows),'expected outcomes; cooperative GPU execution not run (unsupported device)',flush=True)
