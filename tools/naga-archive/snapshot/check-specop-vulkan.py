import hashlib,json,pathlib,struct,subprocess
from vulkan_compute import Vulkan
root=pathlib.Path(__file__).resolve().parents[2]; task=root/'.work/naga-csharp'; work=task/'specop-gpu';work.mkdir(exist_ok=True)
dotnet=root/'.dotnet/dotnet.exe';harness=task/'harness/bin/Release/net10.0/harness.dll';oracle=task/'oracle-target/release/sia-naga-reference-oracle.exe'
assembler=task/'spirv-tools-local/tools/Release/spirv-as.exe';validator=assembler.with_name('spirv-val.exe')
records=[]
def run(name,args,expected=True):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=60)
 (work/f'{name}.log').write_text(p.stdout+p.stderr,encoding='utf-8')
 records.append(dict(check=name,passed=p.returncode==0,exit=p.returncode,expected_pass=expected,message=(p.stdout+p.stderr)[:1000]))
 print(f'{name}: {p.returncode}',flush=True)
 return p.returncode==0
for bits in [16,32,64]:
 mask=(1<<bits)-1;sign=1<<(bits-1)
 declarations=[];annotations=[];body=[];expected=[];specid=0
 def decl(s):declarations.append(s)
 def signed(x):return x-(1<<bits) if x&sign else x
 def const(name,value,typ='u'):
  global specid
  decl(f'%{name} = OpSpecConstant %{typ} {value}');annotations.append(f'OpDecorate %{name} SpecId {specid}');specid+=1
 def store(name,typ,value):
  index=len(expected)
  decl(f'%idx{index} = OpConstant %u32 {index}'); decl(f'%idx{index+1} = OpConstant %u32 {index+1}')
  source=name
  if typ=='b': body.append(f'%val{index} = OpSelect %u32 %{name} %one32 %zero32');source=f'val{index}'
  elif typ=='i':body.append(f'%val{index} = OpBitcast %u %{name}');source=f'val{index}'
  if bits!=32 and typ!='b':body.append(f'%lo{index} = OpUConvert %u32 %{source}');lo=f'lo{index}'
  else:lo=source
  if bits==64 and typ!='b':
   body.extend([f'%hiwide{index} = OpShiftRightLogical %u %{source} %shift32',f'%hi{index} = OpUConvert %u32 %hiwide{index}']);hi=f'hi{index}'
  else:hi='zero32'
  body.extend([f'%ptr{index} = OpAccessChain %ptr_u32 %outputs %zero32 %idx{index}',f'OpStore %ptr{index} %{lo}',f'%ptr{index+1} = OpAccessChain %ptr_u32 %outputs %zero32 %idx{index+1}',f'OpStore %ptr{index+1} %{hi}'])
  value=int(value)&mask;expected.extend([value&0xffffffff,value>>32])
 seeds=[(mask,2),(sign,mask),(mask//3,mask//7),(1,sign),(0,1)]
 for j,(x,y) in enumerate(seeds):
  const(f'a{j}',x);const(f'b{j}',y);const(f'c{j}',signed(x),'i');const(f'd{j}',signed(y),'i');const(f's{j}',bits-1)
  sx,sy=signed(x),signed(y)
  q=(abs(sx)//abs(sy))*(-1 if (sx<0)!=(sy<0) else 1) if sy and not (sx==-sign and sy==-1) else None
  rem=sx-q*sy if q is not None else None
  cases=[('IAdd','u',x+y,'a','b'),('ISub','u',x-y,'a','b'),('IMul','u',x*y,'a','b'),('SNegate','i',-sx,'c',None),
   ('UDiv','u',x//y,'a','b'),('UMod','u',x%y,'a','b'),('BitwiseAnd','u',x&y,'a','b'),('BitwiseOr','u',x|y,'a','b'),('BitwiseXor','u',x^y,'a','b'),
   ('ShiftLeftLogical','u',x<<(bits-1),'a','s'),('ShiftRightLogical','u',x>>(bits-1),'a','s'),('ShiftRightArithmetic','u',sx>>(bits-1),'a','s'),
   ('SLessThan','b',sx<sy,'a','b'),('UGreaterThanEqual','b',x>=y,'c','d'),('IEqual','b',x==y,'a','d')]
  if q is not None:cases.extend([('SDiv','i',q,'c','d'),('SRem','i',rem,'c','d'),('SMod','i',rem+sy if rem and (sx<0)!=(sy<0) else rem,'c','d')])
  for k,(op,typ,value,l,r) in enumerate(cases):
   name=f'r{j}_{k}';decl(f'%{name} = OpSpecConstantOp %{typ} {op} %{l}{j}'+(f' %{r}{j}' if r else ''));store(name,typ,value)
  # Vector composite operations, scalar extraction, signedness-independent operands.
  decl(f'%va{j} = OpSpecConstantComposite %v2 %a{j} %b{j}');decl(f'%vb{j} = OpSpecConstantComposite %v2 %b{j} %a{j}')
  decl(f'%vp{j} = OpSpecConstantOp %v2 IMul %va{j} %vb{j}')
  decl(f'%vi{j} = OpSpecConstantOp %v2 CompositeInsert %a{j} %vp{j} 1')
  decl(f'%vs{j} = OpSpecConstantOp %v2 VectorShuffle %vi{j} %vp{j} 3 1')
  for lane,value in [(0,x*y),(1,x)]:
   name=f'vx{j}_{lane}';decl(f'%{name} = OpSpecConstantOp %u CompositeExtract %vs{j} {lane}');store(name,'u',value)
  name=f'mixed{j}';decl(f'%{name} = OpSpecConstantOp %u IAdd %c{j} %b{j}');store(name,'u',x+y)
 capabilities='OpCapability Shader\n'+(f'OpCapability Int{bits}\n' if bits!=32 else '')
 types=f'''%void = OpTypeVoid
%fn = OpTypeFunction %void
%u32 = OpTypeInt 32 0
{f'%u = OpTypeInt {bits} 0' if bits!=32 else ''}
%i = OpTypeInt {bits} 1
%b = OpTypeBool
%v2 = OpTypeVector %u 2
%array = OpTypeRuntimeArray %u32
%buffer = OpTypeStruct %array
%ptr_buffer = OpTypePointer StorageBuffer %buffer
%ptr_u32 = OpTypePointer StorageBuffer %u32
%zero32 = OpConstant %u32 0
%one32 = OpConstant %u32 1
%shift32 = OpConstant %u32 32
'''
 if bits==32:
  declarations=[s.replace('%u ', '%u32 ') for s in declarations];types=types.replace('%u ', '%u32 ');body=[s.replace('%u ', '%u32 ') for s in body]
 text=capabilities+'OpMemoryModel Logical GLSL450\nOpEntryPoint GLCompute %main "main"\nOpExecutionMode %main LocalSize 1 1 1\n'
 text+='OpDecorate %array ArrayStride 4\nOpDecorate %buffer Block\nOpMemberDecorate %buffer 0 Offset 0\nOpDecorate %outputs DescriptorSet 0\nOpDecorate %outputs Binding 1\n'+'\n'.join(annotations)+'\n'+types+'\n'.join(declarations)+'\n%outputs = OpVariable %ptr_buffer StorageBuffer\n%main = OpFunction %void None %fn\n%entry = OpLabel\n'+'\n'.join(body)+'\nOpReturn\nOpFunctionEnd\n'
 asm=work/f'int{bits}.spvasm';asm.write_text(text,encoding='utf-8');source=asm.with_suffix('.spv');wgsl=asm.with_suffix('.wgsl')
 if not run(f'int{bits}-assemble',[assembler,'--target-env','spv1.3',asm,'-o',source]):continue
 if not run(f'int{bits}-validate',[validator,'--target-env','vulkan1.1',source]):continue
 # Pinned Naga does not implement SpecConstantOp; retain this observed limitation.
 run(f'int{bits}-reference-input',[oracle,source],False)
 if bits==64:
  run(f'int{bits}-unresolved-wgsl',[dotnet,harness,source,work/'int64-unresolved.wgsl'],False)
 if not run(f'int{bits}-managed-wgsl',[dotnet,harness,source,wgsl]+([''] if bits==64 else [])):continue
 run(f'int{bits}-reference-wgsl',[oracle,wgsl])
 outputs=[('original',source)]
 for label,input_path,compiler,values in [('managed',source,[dotnet,harness],None),('resolved',source,[dotnet,harness],''),('wgsl',wgsl,[dotnet,harness],''),('reference-wgsl',wgsl,[oracle],'')]:
  spv=work/f'int{bits}-{label}.spv';args=compiler+[input_path,spv]+([] if values is None else [values])
  if run(f'int{bits}-{label}-compile',args) and run(f'int{bits}-{label}-validate',[validator,'--target-env','vulkan1.1',spv]):outputs.append((label,spv))
 expected.extend([0xdeadbeef]*4)
 for label,spv in outputs:
  with Vulkan() as gpu:
   raw=gpu.execute(spv.read_bytes(),bytes(16),len(expected)*4,1);actual=struct.unpack('<%dI'%len(expected),raw)
   errors=[dict(index=i,expected=e,actual=a) for i,(e,a) in enumerate(zip(expected,actual)) if e!=a]
   records.append(dict(check=f'int{bits}-{label}-gpu',passed=not errors,expected_pass=True,device=gpu.name,features=gpu.features,sha256=hashlib.sha256(spv.read_bytes()).hexdigest(),expected=expected,actual=actual,mismatches=errors))
   print(f'int{bits}-{label}-gpu: {len(errors)} mismatches',flush=True)
 (work/'report.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
(work/'report.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
raise SystemExit(0 if all(r['passed'] for r in records if r['expected_pass']) else 1)
