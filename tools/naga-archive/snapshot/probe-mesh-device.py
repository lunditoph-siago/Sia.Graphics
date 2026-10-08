import ctypes as C,json
from vulkan_compute import Vulkan,S,head,u,p
with Vulkan() as vk:
 enum=vk.lib.vkEnumerateDeviceExtensionProperties;enum.restype=C.c_int32;enum.argtypes=[p,p,p,p];count=u();enum(vk.physical,None,C.byref(count),None);buf=C.create_string_buffer(260*count.value);enum(vk.physical,None,C.byref(count),buf);extensions=[buf.raw[n*260:n*260+256].split(b'\0')[0].decode() for n in range(count.value)]
 names=['taskShader','meshShader','multiviewMeshShader','primitiveFragmentShadingRateMeshShader','meshShaderQueries'];F=S('Mesh',head+[(name,u) for name in names]);features=F(1000328000,None);Q=S('Q',head+[('features',u*55)]);query=Q(1000059000,C.addressof(features));vk.vkGetPhysicalDeviceFeatures2(vk.physical,C.byref(query));report=dict(device=vk.name,extension='VK_EXT_mesh_shader' in extensions,**{name:bool(getattr(features,name)) for name in names});print(json.dumps(report,indent=2));open('.work/naga-csharp/mesh/device.json','w').write(json.dumps(report,indent=2))
