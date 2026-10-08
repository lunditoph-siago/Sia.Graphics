import pathlib,subprocess,json
root=pathlib.Path(__file__).resolve().parents[2];task=root/'.work/naga-csharp';work=task/'unresolved-binding-arrays';work.mkdir(exist_ok=True)
dotnet=root/'.dotnet/dotnet.exe';harness=task/'harness/bin/Release/net10.0/harness.dll';validator=task/'spirv-tools-local/tools/Release/spirv-val.exe';records=[]
for label,element,declaration in [('image','texture_2d<f32>',''),('sampler','sampler',''),('uniform','S','struct S { value:vec4u }')]:
 source=f'enable wgpu_binding_array; @id(7) override n=3u; {declaration} @group(0) @binding(0) var'+('<uniform>' if label=='uniform' else '')+f' data:binding_array<{element},n>; @compute @workgroup_size(1) fn main() {{}}'
 path=work/f'{label}.wgsl';path.write_text(source,encoding='utf-8');spv=path.with_suffix('.spv');roundtrip=work/f'{label}-roundtrip.spv';resolved=work/f'{label}-resolved.spv'
 for name,args in [('write',[dotnet,harness,path,spv]),('validate',[validator,'--target-env','vulkan1.1',spv]),('roundtrip',[dotnet,harness,spv,roundtrip]),('roundtrip-validate',[validator,'--target-env','vulkan1.1',roundtrip]),('resolve',[dotnet,harness,roundtrip,resolved,'7=5']),('resolved-validate',[validator,'--target-env','vulkan1.1',resolved])]:
  p=subprocess.run(list(map(str,args)),capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=30)
  records.append(dict(check=label+'-'+name,passed=p.returncode==0,message=p.stdout+p.stderr));print(label,name,p.returncode,flush=True)
  (work/f'{label}-{name}.log').write_text(p.stdout+p.stderr,encoding='utf-8')
  (work/'report.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
  if p.returncode:break
raise SystemExit(0 if all(r['passed'] for r in records) else 1)
