from pathlib import Path
import json, re, struct, subprocess

root=Path('.work/naga-csharp/mesh');root.mkdir(exist_ok=True);rows=[]
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll']
oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
tools=Path('.work/naga-csharp/spirv-tools-local/tools/Release')
def save():(root/'checks.json').write_text(json.dumps(rows,indent=2))
def run(name,args,rejection=None):
 result=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=60);log=result.stdout+result.stderr
 passed=result.returncode==0 if rejection is None else result.returncode!=0 and rejection in log
 rows.append(dict(check=name,passed=passed,code=result.returncode,expected_rejection=rejection,message=log));save()
 if not passed:raise RuntimeError(name+': '+log)
 return result
def native(name,path,known=()):
 args=[tools/'spirv-val.exe','--target-env','vulkan1.2',path]
 if 'local-size-id' in name:args.insert(1, '--allow-localsizeid')
 result=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=60);log=result.stdout+result.stderr
 matched=next((issue for issue in known if issue in log),None)
 passed=result.returncode==0 or result.returncode!=0 and matched is not None
 rows.append(dict(check=name,passed=passed,code=result.returncode,expected_rejection=matched,message=log));save()
 if not passed:raise RuntimeError(name+': '+log)
def instructions(data):
 words=list(struct.unpack('<'+'I'*(len(data)//4),data));offset=5;result=[]
 while offset<len(words):
  count=words[offset]>>16;result.append((words[offset]&65535,words[offset+1:offset+count]));offset+=count
 return words[:5],result
def isolate(name,path,task_import_rejection=None):
 header,ops=instructions(path.read_bytes())
 for op,args in ops:
  if op!=15:continue
  entry=args[1];entry_name=struct.pack('<'+'I'*len(args[2:]),*args[2:]).split(b'\0')[0].decode();label=name+'-'+entry_name
  output=header[:]
  for opcode,operands in ops:
   if opcode in (15,16,331) and operands[1 if opcode==15 else 0]!=entry:continue
   output+=[((len(operands)+1)<<16)|opcode]+operands
  selected=root/(label+'-selected.spv');selected.write_bytes(struct.pack('<'+'I'*len(output),*output))
  isolated=root/(label+'-isolated.spv')
  passes=['--eliminate-dead-functions','--eliminate-dead-variables','--eliminate-dead-const']
  if name.endswith('-reference'):passes+=['--eliminate-dead-code-aggressive']
  run(label+'-isolate',[tools/'spirv-opt.exe','--skip-validation',*passes,selected,'-o',isolated])
  known=['duplicate input variables with LocalInvocationIndex builtin','Invalid explicit layout decorations'] if name.endswith('-reference') and args[0]==5365 else []
  native(label+'-native',isolated,known)
  if name.endswith('-reference') and args[0]!=5365:
   imported=root/(label+'-imported.wgsl');rejection=task_import_rejection if args[0]==5364 else None
   run(label+'-import',harness+[isolated,imported],rejection)
   if rejection is None:run(label+'-import-reference',oracle+[imported])

cases={fixture:Path('.reference/wgpu/naga/tests/in/wgsl/'+fixture+'.wgsl').read_text() for fixture in ('mesh-shader','mesh-shader-empty','mesh-shader-lines','mesh-shader-points')}
test=Path('Sia.Graphics/Sia.Spirv.Naga.Tests/MeshShaderTests.cs').read_text();source=re.search(r'internal const string Source = """\n(.*?)\n\s*""";',test,re.S)[1];source='\n'.join(line[8:] for line in source.splitlines())
cases['custom-triangles']=source
cases['custom-lines']=source.replace('triangle_indices','line_indices').replace('indices:vec3u','indices:vec2u').replace('vec3u(0,1,2)','vec2u(0,1)')
cases['custom-points']=source.replace('triangle_indices','point_index').replace('indices:vec3u','indices:u32').replace('vec3u(0,1,2)','0u')
cases['custom-half']=source.replace('enable wgpu_mesh_shader;','enable wgpu_mesh_shader; enable f16;').replace('@location(0) color:vec4f','@location(0) color:vec4h').replace('@per_primitive @location(1) color:vec4f','@per_primitive @location(1) color:vec4h').replace('->@location(0) vec4f {return color;}','->@location(0) vec4f {return vec4f(color);}')
cases['custom-specialized']=source.replace('struct Payload', '@id(3) override threads=2u; struct Payload').replace('@workgroup_size(2)', '@workgroup_size(threads)')
cases['custom-resources']=source.replace('fn visible()->bool { return payload.visible; }', '@group(0) @binding(0) var<storage,read> config:vec4u; var<private> scratch:u32; fn visible()->bool { scratch=config.x; return payload.visible && scratch!=0u; }')
cases['custom-clip']=source.replace('enable wgpu_mesh_shader;', 'enable wgpu_mesh_shader; enable clip_distances;').replace('@location(0) color:vec4f }', '@location(0) color:vec4f, @builtin(clip_distances) clip:array<f32,2> }')
for name,text in cases.items():
 path=root/(name+'.wgsl');path.write_text(text);canonical=root/(name+'-canonical.wgsl')
 run(name+'-reference-source',oracle+[path]);run(name+'-canonical',harness+[path,canonical]);run(name+'-reference-canonical',oracle+[canonical])
 for implementation,command in [('managed',harness),('reference',oracle)]:
  if name=='custom-specialized' and implementation=='reference':
   run(name+'-reference-unresolved',command+[path,root/(name+'-unresolved-reference.spv')], 'Override');continue
  label=name+'-'+implementation;spv=root/(label+'.spv');run(label+'-compile',command+[path,spv]);run(label+'-universal',[tools/'spirv-val.exe','--target-env','spv1.4',spv])
  known=['VUID-StandaloneSpirv-OpEntryPoint-09658','VUID-PrimitiveTriangleIndicesEXT-PrimitiveTriangleIndicesEXT-07054','VUID-PrimitiveLineIndicesEXT-PrimitiveLineIndicesEXT-07048','VUID-PrimitivePointIndicesEXT-PrimitivePointIndicesEXT-07042']
  if implementation=='reference':known+=['Invalid explicit layout decorations']
  native(label+'-whole-vulkan',spv,known)
  isolate(label,spv)
  back=root/(label+'-back.wgsl');reemit=root/(label+'-roundtrip.spv')
  if implementation=='reference' and name.startswith('mesh-shader'):
   run(label+'-import-invalid',harness+[spv,back], 'Duplicate entry IO binding')
  else:
   run(label+'-import',harness+[spv,back]);run(label+'-reference-back',oracle+[back])
   run(label+'-roundtrip',harness+[spv,reemit]);run(label+'-roundtrip-universal',[tools/'spirv-val.exe','--target-env','spv1.4',reemit]);isolate(label+'-roundtrip',reemit)
 print(name,'passed',flush=True)
for name in [f'native-aggregate-{mode}' for mode in range(4)]+['native-fragment-block']:
 spv=root/(name+'.spv');back=root/(name+'.wgsl');reemit=root/(name+'-back.spv')
 native(name+'-input',spv)
 run(name+'-read',harness+[spv,back]);run(name+'-reference',oracle+[back]);run(name+'-emit',harness+[spv,reemit]);native(name+'-output',reemit)
for implementation,command in [('managed',harness),('reference',oracle)]:
 label='custom-specialized-'+implementation+'-resolved';output=root/(label+'.spv');run(label+'-compile',command+[root/'custom-specialized.wgsl',output,'3=4']);isolate(label if implementation=='managed' else label+'-reference',output)
label='custom-specialized-local-size-id';output=root/(label+'.spv');run(label+'-compile',harness+[root/'custom-specialized.wgsl',output,'unresolved','local-size-id']);isolate(label,output)
print(sum(row['passed'] for row in rows),'/',len(rows),'checks passed',flush=True)
