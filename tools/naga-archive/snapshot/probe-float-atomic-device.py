import ctypes as C,json
from vulkan_compute import Vulkan,S,head,u,p
with Vulkan() as vk:
 enum=vk.lib.vkEnumerateDeviceExtensionProperties;enum.restype=C.c_int32;enum.argtypes=[p,p,p,p]
 count=u();enum(vk.physical,None,C.byref(count),None)
 buf=C.create_string_buffer(260*count.value);enum(vk.physical,None,C.byref(count),buf)
 extensions=[buf.raw[n*260:n*260+256].split(b'\0')[0].decode() for n in range(count.value)]
 names=['bufferFloat32Atomics','bufferFloat32AtomicAdd','bufferFloat64Atomics','bufferFloat64AtomicAdd','sharedFloat32Atomics','sharedFloat32AtomicAdd','sharedFloat64Atomics','sharedFloat64AtomicAdd','imageFloat32Atomics','imageFloat32AtomicAdd','sparseImageFloat32Atomics','sparseImageFloat32AtomicAdd']
 Float=S('Float',head+[(n,u) for n in names]);features=Float(1000260000,None)
 F=S('F',head+[('features',u*55)]); f=F(1000059000,C.addressof(features));vk.vkGetPhysicalDeviceFeatures2(vk.physical,C.byref(f))
 report=dict(device=vk.name,extension='VK_EXT_shader_atomic_float' in extensions,features={n:bool(getattr(features,n)) for n in names});print(json.dumps(report,indent=2))
 open('.work/naga-csharp/float-atomic/device.json','w').write(json.dumps(report,indent=2))
