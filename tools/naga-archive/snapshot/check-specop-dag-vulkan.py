import hashlib,json,pathlib,struct,subprocess
from vulkan_compute import Vulkan
root=pathlib.Path(__file__).resolve().parents[2];task=root/'.work/naga-csharp';work=task/'specop-dag-gpu';work.mkdir(exist_ok=True)
assembler=task/'spirv-tools-local/tools/Release/spirv-as.exe';validator=assembler.with_name('spirv-val.exe');dotnet=root/'.dotnet/dotnet.exe';harness=task/'harness/bin/Release/net10.0/harness.dll';oracle=task/'oracle-target/release/sia-naga-reference-oracle.exe'
records=[]
def run(name,args):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=60)
 (work/f'{name}.log').write_text(p.stdout+p.stderr,encoding='utf-8')
 records.append(dict(check=name,passed=p.returncode==0,message=p.stdout+p.stderr));print(name,p.returncode,flush=True)
 if p.returncode:raise RuntimeError(p.stdout+p.stderr)
def fixture(name,types,constants,result,outputs,version='spv1.3'):
 # outputs is (source ID, source scalar width) per output lane, packed to pairs of u32.
 annotation='OpDecorate %array ArrayStride 4\nOpDecorate %buffer Block\nOpMemberDecorate %buffer 0 Offset 0\nOpDecorate %outputs DescriptorSet 0\nOpDecorate %outputs Binding 1\nOpDecorate %n SpecId 7\n'
 code=[];indices=[]
 for lane,(value,width) in enumerate(outputs):
  index=lane*2;indices.extend([f'%idx{index} = OpConstant %u32 {index}',f'%idx{index+1} = OpConstant %u32 {index+1}'])
  if width==32:code.append(f'%lo{lane} = OpCopyObject %u32 %{value}')
  else:code.append(f'%lo{lane} = OpUConvert %u32 %{value}')
  if width==64:code.extend([f'%hiwide{lane} = OpShiftRightLogical %u64 %{value} %shift32',f'%hi{lane} = OpUConvert %u32 %hiwide{lane}'])
  else:code.append(f'%hi{lane} = OpCopyObject %u32 %zero')
  code.extend([f'%ptr{index} = OpAccessChain %ptr_u32 %outputs %zero %idx{index}',f'OpStore %ptr{index} %lo{lane}',f'%ptr{index+1} = OpAccessChain %ptr_u32 %outputs %zero %idx{index+1}',f'OpStore %ptr{index+1} %hi{lane}'])
 interfaces=' %outputs' if version=='spv1.5' else ''
 text='OpCapability Shader\nOpCapability Int16\nOpCapability Int64\nOpMemoryModel Logical GLSL450\nOpEntryPoint GLCompute %main "main"'+interfaces+'\nOpExecutionMode %main LocalSize 1 1 1\n'+annotation+'''%void = OpTypeVoid
%fn = OpTypeFunction %void
%u16 = OpTypeInt 16 0
%u32 = OpTypeInt 32 0
%u64 = OpTypeInt 64 0
%i16 = OpTypeInt 16 1
%i32 = OpTypeInt 32 1
%i64 = OpTypeInt 64 1
%v2 = OpTypeVector %u32 2
%array = OpTypeRuntimeArray %u32
%buffer = OpTypeStruct %array
%ptr_buffer = OpTypePointer StorageBuffer %buffer
%ptr_u32 = OpTypePointer StorageBuffer %u32
%zero = OpConstant %u32 0
%one = OpConstant %u32 1
%shift32 = OpConstant %u32 32
'''+types+'\n'+constants+'\n'+'\n'.join(indices)+'\n%outputs = OpVariable %ptr_buffer StorageBuffer\n%main = OpFunction %void None %fn\n%entry = OpLabel\n'+result+'\n'+'\n'.join(code)+'\nOpReturn\nOpFunctionEnd\n'
 path=work/f'{name}.spvasm';path.write_text(text,encoding='utf-8');spv=path.with_suffix('.spv')
 run(name+'-assemble',[assembler,'--target-env',version,path,'-o',spv]);run(name+'-original-validate',[validator,'--target-env','vulkan1.2' if version=='spv1.5' else 'vulkan1.1',spv]);return spv
def check(name,original,base,width,expected,keys=None):
 paths=[('original',original,True)]
 for mode in ['managed','resolved']:
  path=work/f'{name}-{mode}.spv';args=[dotnet,harness,original,path]+([f'7={base}'] if mode=='resolved' else [])
  run(name+'-'+mode,args);run(name+'-'+mode+'-validate',[validator,'--target-env','vulkan1.1',path]);paths.append((mode,path,mode!='resolved'))
 wgsl=work/f'{name}.wgsl';run(name+'-wgsl',[dotnet,harness,original,wgsl,f'7={base}'])
 run(name+'-reference-wgsl',[oracle,wgsl]);path=work/f'{name}-reference.spv';run(name+'-reference',[oracle,wgsl,path]);run(name+'-reference-validate',[validator,'--target-env','vulkan1.1',path]);paths.append(('reference',path,False))
 expectedWords=[]
 for value in expected:expectedWords.extend([value&0xffffffff,value>>32])
 expectedWords.extend([0xdeadbeef]*4)
 for label,path,specialize in paths:
  with Vulkan() as gpu:
   actual=struct.unpack('<%dI'%len(expectedWords),gpu.execute(path.read_bytes(),bytes(16),len(expectedWords)*4,1,{7:base.to_bytes(width//8,'little')} if specialize else None))
   errors=[dict(index=i,expected=e,actual=a) for i,(e,a) in enumerate(zip(expectedWords,actual)) if e!=a]
   records.append(dict(check=name+'-'+label+'-gpu',passed=not errors,device=gpu.name,sha256=hashlib.sha256(path.read_bytes()).hexdigest(),expected=expectedWords,actual=actual,mismatches=errors));print(name,label,len(errors),'mismatches',flush=True)
   (work/'report.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
for vector in [False,True]:
 count=128;constants='%n = OpSpecConstant %u32 3\n%initial = OpSpecConstantComposite %v2 %n %n\n%ones = OpConstantComposite %v2 %one %one\n';prev='initial' if vector else 'n'
 for i in range(count):constants+=f'%s{i} = OpSpecConstantOp %{ "v2" if vector else "u32"} IAdd %{prev} %{ "ones" if vector else "one"}\n';prev=f's{i}'
 if vector:
  constants+=f'%x = OpSpecConstantOp %u32 CompositeExtract %{prev} 0\n%y = OpSpecConstantOp %u32 CompositeExtract %{prev} 1\n';outputs=[('x',32),('y',32)]
 else:outputs=[(prev,32)]
 original=fixture('chain-'+str(vector),'',constants,'',outputs)
 for base in [3,0xfffffffb]:check(f'chain-{vector}-{base}',original,base,32,[(base+count)&0xffffffff]*len(outputs))
for source,destination in [(16,32),(16,64),(32,16),(32,64),(64,16),(64,32)]:
 for op in ['SConvert','UConvert']:
  name=f'convert-{source}-{destination}-{op}';constants=f'%n = OpSpecConstant %u{source} 3\n%value = OpSpecConstantOp %u{destination} {op} %n\n'
  original=fixture(name,'',constants,'',[('value',destination)],'spv1.5' if op=='UConvert' else 'spv1.3')
  base=(1<<source)-1 if source!=64 else (1<<53)-1 # Exactly representable through the public f64 API.
  expected=(-1 if op=='SConvert' and base>>(source-1) else base)&((1<<destination)-1)
  check(name,original,base,source,[expected])
(work/'report.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
raise SystemExit(0 if all(r['passed'] for r in records) else 1)
