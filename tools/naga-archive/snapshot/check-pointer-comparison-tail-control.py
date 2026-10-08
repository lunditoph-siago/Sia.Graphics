"""Retain the failed default-optimization report; separately finish the tail.
Only native original/normalized cross-buffer loop inputs disable optimization.
"""
from pathlib import Path
import hashlib,json,shutil,sys
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
import vulkan_compute as compute
source=Path('.work/naga-csharp/pointer-comparison');target=Path('.work/naga-csharp/pointer-comparison-tail-control');target.mkdir(exist_ok=True)
allcases=json.loads((source/'fixtures.json').read_text());start=next(i for i,c in enumerate(allcases) if c['name']=='scalar-loop-self-cross-32-False-32');fixtures=allcases[start:]
cross=set()
for case in fixtures:
 for suffix in ['.spv','-normalized.spv']:
  path=source/(case['name']+suffix);shutil.copy2(path,target/path.name)
  if case['relation']=='self-cross':cross.add(hashlib.sha256(path.read_bytes()).hexdigest())
(target/'fixtures.json').write_text(json.dumps(fixtures,indent=2))
class Vulkan(compute.Vulkan):
 def execute(self,*args,**kwargs):
  self.last_pipeline_flags=1 if hashlib.sha256(args[0]).hexdigest() in cross else 0
  previous=compute.PipelineInfo
  if self.last_pipeline_flags:
   def pipeline(*a):
    values=list(a);values[2]|=1;return previous(*values)
   compute.PipelineInfo=pipeline
  try:return super().execute(*args,**kwargs)
  finally:compute.PipelineInfo=previous
text=Path('.work/naga-csharp/check-pointer-comparison.py').read_text().replace('from vulkan_compute import Vulkan','')
text=text.replace("root=Path('.work/naga-csharp/pointer-comparison');rows=[]","root=Path('.work/naga-csharp/pointer-comparison-tail-control');rows=[]")
text=text.replace('device=vk.name,actual=', 'device=vk.name,pipeline_flags=vk.last_pipeline_flags,actual=')
text=text.replace('dict(sha256=digest)', 'dict(sha256=digest, cross_original_normalized_pipeline_flags=1, other_pipeline_flags=0)')
exec(compile(text,str(Path(__file__).resolve()),'exec'),globals())
