import pathlib,subprocess,json,struct
root=pathlib.Path(__file__).resolve().parents[2];work=root/'.work/naga-csharp/override-array-checks';work.mkdir(exist_ok=True)
probes=root/'.work/naga-csharp/override-array-probes'
dotnet=root/'.dotnet/dotnet.exe';harness=root/'.work/naga-csharp/harness/bin/Release/net10.0/harness.dll';oracle=root/'.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe'
native=root/'.work/naga-csharp/spirv-tools-local/tools/Release'
records=[]
def check(name,step,args,expected=True):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=30)
 log=p.stdout+p.stderr;(work/f'{name}-{step}.log').write_text(log,encoding='utf-8')
 records.append(dict(case=name,step=step,expected_accept=expected,code=p.returncode,passed=(p.returncode==0)==expected,message=log.splitlines()[0] if log else ''))
 return p.returncode==0
for name in ['direct','expression','missing','negative-default','zero-default','private-pointer','by-value','binding']:
 path=probes/f'{name}.wgsl';wgsl=work/f'{name}.wgsl';spv=work/f'{name}.spv';back=work/f'{name}-back.wgsl'
 check(name,'reference-source',[oracle,path])
 if check(name,'managed-unresolved-wgsl',[dotnet,harness,path,work/f'{name}-unresolved.wgsl']):check(name,'reference-unresolved-wgsl',[oracle,work/f'{name}-unresolved.wgsl'])
 if check(name,'managed-resolved-wgsl',[dotnet,harness,path,wgsl,'n=3']):check(name,'reference-wgsl',[oracle,wgsl])
 if check(name,'managed-spv',[dotnet,harness,path,spv,'n=3']):
  check(name,'native',[native/'spirv-val.exe','--target-env','vulkan1.1',spv]);check(name,'reference-spv',[oracle,spv])
  if check(name,'roundtrip',[dotnet,harness,spv,back]):check(name,'reference-roundtrip',[oracle,back])
 check(name,'reference-resolved-spv',[oracle,path,work/f'{name}-reference.spv','n=3'])
for name in ['nested','struct','local','immutable','return','storage','constructed','pointer']:
 path=probes/f'{name}.wgsl'
 check(name,'reference-rejection',[oracle,path],False);check(name,'managed-rejection',[dotnet,harness,path,work/f'{name}.spv','n=3'],False)
for name in ['zero-default','negative-default']:
 path=probes/f'{name}.wgsl'
 check(name,'reference-default-rejection',[oracle,path,work/f'{name}-default-reference.spv',''],False)
 check(name,'managed-default-rejection',[dotnet,harness,path,work/f'{name}-default.spv',''],False)
# Change the fixed array's OpConstant to OpSpecConstant and give its length a SpecId.
raw=list(struct.unpack('<%dI'%(len((work/'direct.spv').read_bytes())//4),(work/'direct.spv').read_bytes()));inst=[];cursor=5
while cursor<len(raw):
 count=raw[cursor]>>16;inst.append(raw[cursor:cursor+count]);cursor+=count
length=next(i[3] for i in inst if i[0]&65535==28)
for i in inst:
 if i[0]&65535==43 and i[2]==length:i[0]=(i[0]&0xffff0000)|50
first_type=next(i for i,item in enumerate(inst) if item[0]&65535 in [19,20,21,22]);inst.insert(first_type,[4<<16|71,length,1,7])
words=raw[:5]+[word for i in inst for word in i];path=work/'specialized-input.spv';path.write_bytes(struct.pack('<%dI'%len(words),*words))
check('specialized-input','native-input',[native/'spirv-val.exe','--target-env','vulkan1.1',path])
check('specialized-input','reference-input',[oracle,path])
if check('specialized-input','managed-resolved-wgsl',[dotnet,harness,path,work/'specialized-input.wgsl','7=5']):check('specialized-input','reference-wgsl',[oracle,work/'specialized-input.wgsl'])
if check('specialized-input','managed-resolved-spv',[dotnet,harness,path,work/'specialized-input-resolved.spv','7=5']):check('specialized-input','native-output',[native/'spirv-val.exe','--target-env','vulkan1.1',work/'specialized-input-resolved.spv'])
(work/'manifest.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
print(f'{sum(r["passed"] for r in records)}/{len(records)} checks passed');print(json.dumps([r for r in records if not r['passed']],indent=2))
raise SystemExit(0 if all(r['passed'] for r in records) else 1)
