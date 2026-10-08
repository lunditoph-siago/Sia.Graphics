import ctypes as C,json
from vulkan_compute import Vulkan,S,head,u,p
with Vulkan() as vk:
 enum=vk.lib.vkEnumerateDeviceExtensionProperties;enum.restype=C.c_int32;enum.argtypes=[p,p,p,p];count=u();enum(vk.physical,None,C.byref(count),None);buf=C.create_string_buffer(260*count.value);enum(vk.physical,None,C.byref(count),buf);extensions=[buf.raw[n*260:n*260+256].split(b'\0')[0].decode() for n in range(count.value)]
 F=S('F',head+[('imageAtomics',u),('sparseAtomics',u)]);features=F(1000234000,None);Q=S('Q',head+[('features',u*55)]);query=Q(1000059000,C.addressof(features));vk.vkGetPhysicalDeviceFeatures2(vk.physical,C.byref(query));report=dict(device=vk.name,extension='VK_EXT_shader_image_atomic_int64' in extensions,imageInt64Atomics=bool(features.imageAtomics),sparseImageInt64Atomics=bool(features.sparseAtomics));print(json.dumps(report,indent=2));open('.work/naga-csharp/image-atomic/device64.json','w').write(json.dumps(report,indent=2))
