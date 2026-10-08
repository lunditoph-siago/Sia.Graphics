import ctypes as C,struct,json
from vulkan_compute import Vulkan,S,head,u,p
with Vulkan() as vk:
 props=C.create_string_buffer(4096);vk.vkGetPhysicalDeviceProperties(vk.physical,props);version=struct.unpack_from('<I',props.raw)[0];F=S('F',head+[('maintenance4',u)]);f=F(1000413000,None);Q=S('Q',head+[('features',u*55)]);query=Q(1000059000,C.addressof(f));vk.vkGetPhysicalDeviceFeatures2(vk.physical,C.byref(query));print(json.dumps(dict(device=vk.name,api=[version>>22,(version>>12)&1023,version&4095],maintenance4=bool(f.maintenance4))))
