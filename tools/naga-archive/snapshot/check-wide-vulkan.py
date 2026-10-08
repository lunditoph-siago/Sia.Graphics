import hashlib, json, pathlib, random, struct, subprocess
from vulkan_compute import Vulkan
root=pathlib.Path(__file__).resolve().parents[2]; work=root/'.work/naga-csharp'; fixtures=work/'wide-gpu'; fixtures.mkdir(exist_ok=True)
records=[]
def run(args):
    result=subprocess.run(list(map(str,args)),capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=30)
    if result.returncode: raise RuntimeError(result.stdout+result.stderr)
    return result
for width in [16,64]:
    mask=(1<<width)-1; sign=1<<(width-1); t=f'u{width}'; s=f'i{width}'
    value='u16(data.x)' if width==16 else 'u64(data.x) | (u64(data.y) << 32u)'
    pattern='u16(0xaaaau)' if width==16 else '0xaaaaaaaa55555555lu'
    upper='0u' if width==16 else 'u32(value >> 32u)'
    shader=f'''{'enable wgpu_int16;' if width==16 else ''}
@group(0) @binding(0) var<storage,read> inputs: array<vec4u>;
@group(0) @binding(1) var<storage,read_write> outputs: array<u32>;
fn write(index:u32, value:{t}) {{ outputs[index*2u] = u32(value); outputs[index*2u+1u] = {upper}; }}
@compute @workgroup_size(64) fn main(@builtin(global_invocation_id) id:vec3u) {{
 if (id.x >= 257u) {{ return; }}
 let data=inputs[id.x]; let v={value}; let arg=v ^ {pattern}; let signed=bitcast<{s}>(v);
 var d={s}(i32(data.y)); if (data.z % 4u == 0u) {{ d={s}(0); }} else if (data.z % 4u == 1u) {{ d={s}(-1); }}
 let ud=bitcast<{t}>(d); let base=id.x*15u;
 write(base,countLeadingZeros(v)); write(base+1u,countTrailingZeros(v)); write(base+2u,firstLeadingBit(v)); write(base+3u,firstTrailingBit(v));
 write(base+4u,countOneBits(v)); write(base+5u,reverseBits(v)); write(base+6u,extractBits(v,data.z,data.w)); write(base+7u,insertBits(v,arg,data.z,data.w));
 write(base+8u,bitcast<{t}>(extractBits(signed,data.z,data.w))); write(base+9u,bitcast<{t}>(signed/d)); write(base+10u,bitcast<{t}>(signed%d));
 write(base+11u,v/ud); write(base+12u,v%ud); write(base+13u,bitcast<{t}>(firstLeadingBit(signed))); write(base+14u,arg);
}}'''
    path=fixtures/f'int{width}.wgsl'; path.write_text(shader,encoding='utf-8'); spv=path.with_suffix('.spv')
    run([root/'.dotnet/dotnet.exe',work/'harness/bin/Release/net10.0/harness.dll',path,spv])
    run([work/'spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.1',spv])
    edges=[0,1,2,3,7,15,16,31,32,33,63,64,65,sign-1,sign,mask,mask//3,2*(mask//3)]
    generator=random.Random(0x1234+width); inputWords=[]; expected=[]
    for index in range(257):
        v=edges[index] if index<len(edges) else generator.getrandbits(width)
        high=v>>32 if width==64 else generator.getrandbits(32)
        offset=1 if v==sign else edges[index*3%len(edges)]&0xffffffff
        count=edges[index*5%len(edges)]&0xffffffff
        inputWords.extend([v&0xffffffff,high,offset,count])
        signed=v if v<sign else v-(1<<width)
        rawD=high if high<0x80000000 else high-(1<<32); d=rawD&mask; d=d if d<sign else d-(1<<width)
        if offset%4==0: d=0
        elif offset%4==1: d=-1
        divisor=1 if d==0 or signed==-sign and d==-1 else d
        quotient=(abs(signed)//abs(divisor))*(-1 if (signed<0)!=(divisor<0) else 1)
        remainder=signed-quotient*divisor
        ud=d&mask; ud=ud or 1
        arg=v ^ (0xaaaa if width==16 else 0xaaaaaaaa55555555)
        off=min(width,offset); cnt=min(width-off,count); bits=(1<<cnt)-1
        extracted=(v>>off)&bits
        inserted=(v&~(bits<<off))|((arg&bits)<<off)
        signedExtract=extracted-(1<<cnt) if cnt and extracted&(1<<(cnt-1)) else extracted
        leading=(~v)&mask if signed<0 else v
        results=[width-v.bit_length(),width if not v else (v&-v).bit_length()-1,mask if not v else v.bit_length()-1,
                 mask if not v else (v&-v).bit_length()-1,v.bit_count(),int(f'{v:0{width}b}'[::-1],2),extracted,inserted,
                 signedExtract&mask,quotient&mask,remainder&mask,v//ud,v%ud,mask if not leading else leading.bit_length()-1,arg]
        for result in results: expected.extend([result&0xffffffff,result>>32])
    expected.extend([0xdeadbeef]*4); expectedBytes=struct.pack('<%dI'%len(expected),*expected)
    with Vulkan() as gpu:
        if not gpu.features[f'int{width}']: raise RuntimeError(f'GPU lacks int{width}')
        actualBytes=gpu.execute(spv.read_bytes(),struct.pack('<%dI'%len(inputWords),*inputWords),len(expectedBytes),5)
        actual=struct.unpack('<%dI'%len(expected),actualBytes)
        mismatches=[dict(index=index,expected=e,actual=a) for index,(e,a) in enumerate(zip(expected,actual)) if e!=a]
        record=dict(width=width,device=gpu.name,features=gpu.features,spirv_sha256=hashlib.sha256(spv.read_bytes()).hexdigest(),inputs=inputWords,expected=expected,actual=actual,mismatches=len(mismatches),samples=mismatches[:16])
        records.append(record); print(f'int{width}: {len(mismatches)} mismatched words on {gpu.name}',flush=True)
        (fixtures/'report.json').write_text(json.dumps(records,indent=2))
raise SystemExit(0 if all(r['mismatches']==0 for r in records) else 1)
