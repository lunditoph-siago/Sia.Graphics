import hashlib,json,pathlib,struct,subprocess
from vulkan_compute import Vulkan
root=pathlib.Path(__file__).resolve().parents[2]; work=root/'.work/naga-csharp'; fixtures=work/'wide-gpu'
scalarRecords=json.loads((fixtures/'report.json').read_text()); records=[]
for scalar in scalarRecords:
    width=scalar['width']; t=f'u{width}'; s=f'i{width}'; mask=(1<<width)-1; sign=1<<(width-1)
    pattern='u16(0xaaaau)' if width==16 else '0xaaaaaaaa55555555lu'; patternValue=0xaaaa if width==16 else 0xaaaaaaaa55555555
    value='u16(data.x)' if width==16 else 'u64(data.x) | (u64(data.y) << 32u)'
    shader=(fixtures/f'int{width}.wgsl').read_text()
    shader=shader.replace('fn write(', 'fn scalar_write(',1)
    shader=shader.replace('@compute',f'fn write(index:u32,value:vec2<{t}>) {{ scalar_write(index*2u,value.x); scalar_write(index*2u+1u,value.y); }}\n@compute',1)
    shader=shader.replace(f'let v={value};',f'let v=vec2<{t}>({value}, ({value}) ^ {pattern});')
    shader=shader.replace(f'let arg=v ^ {pattern};',f'let arg=v ^ vec2<{t}>({pattern});')
    shader=shader.replace(f'bitcast<{s}>',f'bitcast<vec2<{s}>>').replace(f'bitcast<{t}>',f'bitcast<vec2<{t}>>')
    shader=shader.replace(f'var d={s}(i32(data.y));',f'var d=vec2<{s}>({s}(i32(data.y)));')
    shader=shader.replace(f'd={s}(0);',f'd=vec2<{s}>({s}(0));').replace(f'd={s}(-1);',f'd=vec2<{s}>({s}(-1));')
    path=fixtures/f'vec2-int{width}.wgsl'; path.write_text(shader); spv=path.with_suffix('.spv')
    for args in [[root/'.dotnet/dotnet.exe',work/'harness/bin/Release/net10.0/harness.dll',path,spv], [work/'spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.1',spv]]:
        result=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=30)
        if result.returncode: raise RuntimeError(result.stdout+result.stderr)
    inputWords=scalar['inputs']; expected=[]
    def calculate(v,high,offset,count):
        signed=v if v<sign else v-(1<<width); d=(high if high<0x80000000 else high-(1<<32))&mask; d=d if d<sign else d-(1<<width)
        if offset%4==0: d=0
        elif offset%4==1: d=-1
        divisor=1 if d==0 or signed==-sign and d==-1 else d
        quotient=(abs(signed)//abs(divisor))*(-1 if (signed<0)!=(divisor<0) else 1); remainder=signed-quotient*divisor
        ud=(d&mask) or 1; arg=v^patternValue; off=min(width,offset); cnt=min(width-off,count); bits=(1<<cnt)-1
        extracted=(v>>off)&bits; inserted=(v&~(bits<<off))|((arg&bits)<<off)
        signedExtract=extracted-(1<<cnt) if cnt and extracted&(1<<(cnt-1)) else extracted; leading=(~v)&mask if signed<0 else v
        return [width-v.bit_length(),width if not v else (v&-v).bit_length()-1,mask if not v else v.bit_length()-1,mask if not v else (v&-v).bit_length()-1,
                v.bit_count(),int(f'{v:0{width}b}'[::-1],2),extracted,inserted,signedExtract&mask,quotient&mask,remainder&mask,v//ud,v%ud,mask if not leading else leading.bit_length()-1,arg]
    for index in range(257):
        low,high,offset,count=inputWords[index*4:index*4+4]; v=(low|(high<<32))&mask
        first=calculate(v,high,offset,count); second=calculate(v^patternValue,high,offset,count)
        for left,right in zip(first,second): expected.extend([left&0xffffffff,left>>32,right&0xffffffff,right>>32])
    expected.extend([0xdeadbeef]*4)
    with Vulkan() as gpu:
        actualBytes=gpu.execute(spv.read_bytes(),struct.pack('<%dI'%len(inputWords),*inputWords),len(expected)*4,5)
        actual=struct.unpack('<%dI'%len(expected),actualBytes)
        mismatches=[dict(index=index,expected=e,actual=a) for index,(e,a) in enumerate(zip(expected,actual)) if e!=a]
        records.append(dict(width=width,device=gpu.name,features=gpu.features,spirv_sha256=hashlib.sha256(spv.read_bytes()).hexdigest(),mismatches=len(mismatches),samples=mismatches[:16],expected=expected,actual=actual))
        print(f'vec2-int{width}: {len(mismatches)} mismatched words on {gpu.name}',flush=True)
        (fixtures/'vector-report.json').write_text(json.dumps(records,indent=2))
raise SystemExit(0 if all(r['mismatches']==0 for r in records) else 1)
