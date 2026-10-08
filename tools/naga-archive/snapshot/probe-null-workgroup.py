from pathlib import Path
import hashlib,json,struct,subprocess,sys
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
import vulkan_compute as compute
p=Path('.work/naga-csharp/pointer-null');rows=[];original_pipeline=compute.PipelineInfo
for label,suffix in [('input','.spv'),('normalized','-normalized.spv'),('roundtrip','-back.spv'),('reference','-reference.spv'),('managed','-managed.spv')]:
 path=p/('workgroup-select-False-False'+suffix)
 validation=subprocess.run(['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.2',str(path)],capture_output=True,text=True,timeout=60)
 rows.append(dict(check=label+'-validation',code=validation.returncode,message=validation.stdout+validation.stderr))
 if validation.returncode:continue
 for noopt in [False,True]:
  def pipeline(*args):
   a=list(args);a[2]|=int(noopt);return original_pipeline(*a)
  compute.PipelineInfo=pipeline
  with compute.Vulkan(variablePointers='full' if label in ('input','normalized') else False) as vk:
   for seed in [0,1]:
    flags=[seed&1,seed%4,(seed+1)%4,0,(seed+2)%4,0,0,0]
    a=[11+10*j+100*seed for j in range(4)];b=[111+10*j+100*seed for j in range(4)]
    data=struct.pack('<17I',*(flags+a+b+[919]));expected=[a[seed%4],a[seed%4]+100,919,1] if seed else [0xdeadbeef]*3+[0]
    actual=list(struct.unpack('<4I',vk.execute(path.read_bytes(),data,16,1)))
    rows.append(dict(check=label+'-output-'+str(seed),noopt=noopt,actual=actual,expected=expected,matches=actual==expected,device=vk.name))
compute.PipelineInfo=original_pipeline
digest=hashlib.sha256(Path('.work/naga-csharp/harness/bin/Release/net10.0/Sia.Spirv.Naga.dll').read_bytes()).hexdigest()
(p/'workgroup-native-difference.json').write_text(json.dumps(dict(sha256=digest,rows=rows),indent=2));print(json.dumps(rows,indent=2))
