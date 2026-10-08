import base64, hashlib, json, pathlib, struct
from vulkan_compute import Vulkan
root=pathlib.Path(__file__).resolve().parents[2]; work=root/'.work/naga-csharp'
records=[]
for family in ['integer','half']:
    report=json.loads((work/f'gpu-{family}-direct-spv.json').read_text())
    capture=next(c for c in report['Captures'] if c['Name']==f'naga/{family}/managed-spirv')
    inputs=next(r for r in capture['Resources'] if r['Name']=='inputs')['Words']
    if family=='integer': expected=base64.b64decode(capture['Readbacks'][0]['ExpectedRaw'])
    else:
        words=[]
        for index in range(257):
            value=inputs[index*4]
            values=[value,*[struct.unpack('<I',struct.pack('<f',struct.unpack('<e',struct.pack('<H',v))[0]))[0] for v in [value&65535,value>>16]],*[0]*12]
            words.extend(values)
        expected=struct.pack('<%dI'%len(words),*words)+struct.pack('<4I',*[0xdeadbeef]*4)
    for writer in ['managed','reference'] if family=='integer' else ['managed']:
        path=work/(f'gpu-{family}.spv' if writer=='managed' else 'integer-reference.spv')
        with Vulkan() as gpu:
            actual=gpu.execute(path.read_bytes(),struct.pack('<%dI'%len(inputs),*inputs),len(expected),5)
            mismatches=[dict(index=index,expected=e,actual=a) for index,(e,a) in enumerate(zip(struct.unpack('<%dI'%(len(expected)//4),expected),struct.unpack('<%dI'%(len(actual)//4),actual))) if e!=a]
            record=dict(family=family,writer=writer,device=gpu.name,features=gpu.features,spirv_sha256=hashlib.sha256(path.read_bytes()).hexdigest(),mismatches=len(mismatches),samples=mismatches[:16],expected=base64.b64encode(expected).decode(),actual=base64.b64encode(actual).decode())
            records.append(record); print(f'{family}/{writer}: {len(mismatches)} mismatches on {gpu.name}',flush=True)
(work/'vulkan-compute.json').write_text(json.dumps(records,indent=2))
raise SystemExit(0 if all(r['mismatches']==0 for r in records) else 1)
