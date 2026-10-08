from pathlib import Path
import hashlib,json,struct,subprocess,sys
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
from vulkan_compute import Vulkan
root=Path('.work/naga-csharp/runtime-integer-conversion');root.mkdir(exist_ok=True);rows=[]
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll']
oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
native=Path('.work/naga-csharp/spirv-tools-local/tools/Release')
assembly=Path(harness[1]).with_name('Sia.Spirv.Naga.dll');digest=hashlib.sha256(assembly.read_bytes()).hexdigest()
def save():(root/'checks.json').write_text(json.dumps(rows,indent=2))
def run(name,args,external=False):
 r=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=60)
 rows.append(dict(check=name,passed=r.returncode==0,external=external,code=r.returncode,message=r.stdout+r.stderr,command=list(map(str,args))))
 if r.returncode and not external:save();raise RuntimeError(rows[-1])
 return r
for sourceWidth,resultWidth in [(16,32),(16,64),(32,16),(32,64),(64,16),(64,32)]:
 for sourceSigned in [False,True]:
  for opcode,resultSigned in [('UConvert',False),('SConvert',False),('SConvert',True)]:
   name=f'{sourceWidth}-{sourceSigned}-{opcode}-{resultWidth}-{resultSigned}'
   capabilities='OpCapability Int16\n' if 16 in (sourceWidth,resultWidth) else ''
   if 64 in (sourceWidth,resultWidth):capabilities+='OpCapability Int64\n'
   types=''
   for width in sorted(set([32,sourceWidth,resultWidth])):
    for signed in [False,True]:types+=f'%{"i" if signed else "u"}{width} = OpTypeInt {width} {int(signed)}\n'
   body='%p = OpAccessChain %ptr %input %zero %zero\n%low = OpLoad %u32 %p\n'
   if sourceWidth==32:source='%low'
   else:
    body+=f'%sourceLow = OpUConvert %u{sourceWidth} %low\n';source='%sourceLow'
    if sourceWidth==64:
     body+='%pHigh = OpAccessChain %ptr %input %zero %one\n%high = OpLoad %u32 %pHigh\n%sourceHigh = OpUConvert %u64 %high\n%shift = OpShiftLeftLogical %u64 %sourceHigh %thirtytwo\n%joined = OpBitwiseOr %u64 %sourceLow %shift\n';source='%joined'
   if sourceSigned:body+=f'%signed = OpBitcast %i{sourceWidth} {source}\n';source='%signed'
   body+=f'%converted = Op{opcode} %{"i" if resultSigned else "u"}{resultWidth} {source}\n';result='%converted'
   if resultSigned:body+=f'%unsigned = OpBitcast %u{resultWidth} {result}\n';result='%unsigned'
   low=result;high='%zero'
   if resultWidth!=32:body+=f'%resultLow = OpUConvert %u32 {result}\n';low='%resultLow'
   if resultWidth==64:
    body+=f'%resultShift = OpShiftRightLogical %u64 {result} %thirtytwo\n%resultHigh = OpUConvert %u32 %resultShift\n';high='%resultHigh'
   body+=f'%outLow = OpAccessChain %ptr %output %zero %zero\nOpStore %outLow {low}\n%outHigh = OpAccessChain %ptr %output %zero %one\nOpStore %outHigh {high}\n'
   text=f'''OpCapability Shader
{capabilities}OpMemoryModel Logical GLSL450
OpEntryPoint GLCompute %main "main"
OpExecutionMode %main LocalSize 1 1 1
OpDecorate %array ArrayStride 4
OpDecorate %block Block
OpMemberDecorate %block 0 Offset 0
OpDecorate %input DescriptorSet 0
OpDecorate %input Binding 0
OpDecorate %output DescriptorSet 0
OpDecorate %output Binding 1
%void = OpTypeVoid
{types}%array = OpTypeRuntimeArray %u32
%block = OpTypeStruct %array
%buffer = OpTypePointer StorageBuffer %block
%ptr = OpTypePointer StorageBuffer %u32
%function = OpTypeFunction %void
%zero = OpConstant %u32 0
%one = OpConstant %u32 1
%thirtytwo = OpConstant %u32 32
%input = OpVariable %buffer StorageBuffer
%output = OpVariable %buffer StorageBuffer
%main = OpFunction %void None %function
%entry = OpLabel
{body}OpReturn
OpFunctionEnd
'''
   asm=root/(name+'.spvasm');asm.write_text(text);original=asm.with_suffix('.spv');back=root/(name+'-back.spv');wgsl=root/(name+'.wgsl');managed=root/(name+'-managed.spv');reference=root/(name+'-reference.spv')
   run(name+'-assemble',[native/'spirv-as.exe','--target-env','spv1.3',asm,'-o',original])
   run(name+'-input-native',[native/'spirv-val.exe','--target-env','vulkan1.1',original])
   run(name+'-back',harness+[original,back]);run(name+'-back-native',[native/'spirv-val.exe','--target-env','vulkan1.2',back])
   run(name+'-wgsl',harness+[original,wgsl]);run(name+'-managed',harness+[wgsl,managed]);run(name+'-managed-native',[native/'spirv-val.exe','--target-env','vulkan1.2',managed])
   ref=run(name+'-reference',oracle+[wgsl,reference],external=True);paths=[('input',original),('roundtrip',back),('managed',managed)]
   if not ref.returncode:
    run(name+'-reference-native',[native/'spirv-val.exe','--target-env','vulkan1.2',reference]);paths.append(('reference',reference))
   for label,path in paths:
    with Vulkan() as vk:
     for value in [0,1,(1<<sourceWidth)-1,(1<<(sourceWidth-1))+3,0x0123456789abcdef]:
      raw=value&((1<<sourceWidth)-1);number=raw-(1<<sourceWidth) if opcode=='SConvert' and raw>>(sourceWidth-1) else raw
      bits=number&((1<<resultWidth)-1);expected=[bits&0xffffffff,bits>>32,0xdeadbeef,0xdeadbeef]
      data=struct.pack('<17I',value&0xffffffff,(value>>32)&0xffffffff,*([0]*15))
      actual=list(struct.unpack('<4I',vk.execute(path.read_bytes(),data,16,1)))
      rows.append(dict(check=f'{name}-{label}-gpu-{value}',passed=actual==expected,actual=actual,expected=expected,device=vk.name))
      if not rows[-1]['passed']:save();raise RuntimeError(rows[-1])
   save();print(name,'checks recorded',flush=True)
assert digest==hashlib.sha256(assembly.read_bytes()).hexdigest()
(root/'assembly.json').write_text(json.dumps(dict(sha256=digest),indent=2))
print(sum(r['passed'] for r in rows),'/',len(rows),'runtime conversion checks passed',flush=True)
