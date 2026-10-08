"""Separate diagnostic: only original/normalized inputs disable driver optimization.
The ordinary cross-buffer failure gate and its reports are not overwritten.
"""
from pathlib import Path
import json,shutil,sys
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
import vulkan_compute as compute
source=Path('.work/naga-csharp/pointer-cross-buffer');target=Path('.work/naga-csharp/pointer-cross-buffer-noopt');target.mkdir(exist_ok=True)
fixtures=json.loads((source/'fixtures.json').read_text())
for case in fixtures:
 for suffix in ['.spv','-normalized.spv']:shutil.copy2(source/(case['name']+suffix),target/(case['name']+suffix))
(target/'fixtures.json').write_text(json.dumps(fixtures,indent=2))
class Vulkan(compute.Vulkan):
 def execute(self,*args,**kwargs):
  previous=compute.PipelineInfo
  if self.features.get('variablePointers'):
   def pipeline(*a):
    values=list(a);values[2]|=1;return previous(*values)
   compute.PipelineInfo=pipeline
  try:return super().execute(*args,**kwargs)
  finally:compute.PipelineInfo=previous
text=Path('.work/naga-csharp/check-pointer-cross-buffer.py').read_text()
text=text.replace('from vulkan_compute import Vulkan','')
text=text.replace("root = Path('.work/naga-csharp/pointer-cross-buffer')","root = Path('.work/naga-csharp/pointer-cross-buffer-noopt')")
text=text.replace('device=vk.name, actual=',"device=vk.name, pipeline_flags=1 if vk.features.get('variablePointers') else 0, actual=")
text=text.replace('dict(sha256=digest)','dict(sha256=digest, original_normalized_pipeline_flags=1, translated_pipeline_flags=0)')
exec(compile(text,str(Path(__file__).resolve()),'exec'),globals())
