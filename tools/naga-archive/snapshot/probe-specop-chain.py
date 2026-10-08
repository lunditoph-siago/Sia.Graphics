import pathlib,subprocess,json
root=pathlib.Path(__file__).resolve().parents[2];task=root/'.work/naga-csharp';work=task/'specop-chain';work.mkdir(exist_ok=True)
assembler=task/'spirv-tools-local/tools/Release/spirv-as.exe';harness=task/'harness/bin/Release/net10.0/harness.dll';dotnet=root/'.dotnet/dotnet.exe'
report=[]
for count in [1,2,3,4]:
 text='OpCapability Shader\nOpMemoryModel Logical GLSL450\nOpDecorate %n SpecId 7\n%u = OpTypeInt 32 0\n%ptr = OpTypePointer Private %u\n%n = OpSpecConstant %u 3\n%one = OpConstant %u 1\n'
 prev='n'
 for i in range(count):text+=f'%s{i} = OpSpecConstantOp %u IAdd %{prev} %one\n';prev=f's{i}'
 text+=f'%data = OpVariable %ptr Private %{prev}\n';asm=work/f'chain-{count}.spvasm';asm.write_text(text);spv=asm.with_suffix('.spv');out=asm.with_suffix('.wgsl')
 subprocess.run(list(map(str,[assembler,'--target-env','spv1.3',asm,'-o',spv])),check=True,capture_output=True)
 p=subprocess.run(list(map(str,[dotnet,harness,spv,out])),capture_output=True,text=True,timeout=20)
 report.append(dict(count=count,exit=p.returncode,bytes=out.stat().st_size if out.exists() else None,message=p.stdout+p.stderr));print(report[-1],flush=True)
(work/'after.json').write_text(json.dumps(report,indent=2))
