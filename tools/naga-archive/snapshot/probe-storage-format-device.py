from pathlib import Path
import re,json,ctypes as C
from vulkan_images import ImageVulkan,u
root=Path('.work/naga-csharp/storage-formats');header=Path('.reference/SakuraEngine-engine/engine/packages/vulkan-headers/1.4.324/vulkan/vulkan_core.h').read_text();codes={name:int(value) for name,value in re.findall(r'(VK_FORMAT_[A-Z0-9_]+) = (\d+),',header)};rows=[]
for row in json.loads((root/'baseline.json').read_text()):
 fmt=row['format'];suffix=next(s for s in ['unorm','snorm','uint','sint','float'] if fmt.endswith(s));prefix=fmt[:-len(suffix)]
 if fmt=='rg11b10ufloat':name='B10G11R11_UFLOAT_PACK32';channels=3;bits=0;texel=4
 elif fmt.startswith('rgb10a2'):name='A2B10G10R10_'+suffix.upper()+'_PACK32';channels=4;bits=0;texel=4
 elif fmt=='bgra8unorm':name='B8G8R8A8_UNORM';channels=4;bits=8;texel=4
 else:
  channel,bits=re.fullmatch(r'(r|rg|rgba)(8|16|32|64)',prefix).groups();bits=int(bits);channels=len(channel) if channel!='rgba' else 4;name=''.join(c.upper()+str(bits) for c in channel)+('_SFLOAT' if suffix=='float' else '_'+suffix.upper());texel=channels*bits//8
 code=codes['VK_FORMAT_'+name]
 with ImageVulkan() as vk:
  properties=(u*3)();vk.vkGetPhysicalDeviceFormatProperties(vk.physical,code,properties);supported=bool(properties[1]&2);features=vk.features
  if fmt=='r64uint':supported=False
  readWithoutFormat=None;writeWithoutFormat=None;supportedWrite=supported
  if fmt=='bgra8unorm':
   # VkPhysicalDeviceFeatures fields 31/32 are shaderStorageImageRead/WriteWithoutFormat.
   from vulkan_compute import Features2
   q=Features2(1000059000,None);vk.vkGetPhysicalDeviceFeatures2(vk.physical,C.byref(q));readWithoutFormat=bool(q.features[31]);writeWithoutFormat=bool(q.features[32]);supportedWrite=supported and writeWithoutFormat;supported=supportedWrite and readWithoutFormat
  rows.append(dict(format=fmt,vulkan=code,name=name,channels=channels,bits=bits,texel=texel,supported=supported,supportedWrite=supportedWrite,readWithoutFormat=readWithoutFormat,writeWithoutFormat=writeWithoutFormat,properties=list(properties),device=vk.name))
(root/'device-formats.json').write_text(json.dumps(rows,indent=2));print('supported',sum(r['supported'] for r in rows),'/',len(rows));print('unsupported',[(r['format'],r['properties']) for r in rows if not r['supported']])
