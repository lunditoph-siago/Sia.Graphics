from pathlib import Path
import ctypes as C,json,sys
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
from vulkan_compute import Vulkan,S,head,addr,u,Features2
names=['shaderInputAttachmentArrayDynamicIndexing','shaderUniformTexelBufferArrayDynamicIndexing','shaderStorageTexelBufferArrayDynamicIndexing','shaderUniformBufferArrayNonUniformIndexing','shaderSampledImageArrayNonUniformIndexing','shaderStorageBufferArrayNonUniformIndexing','shaderStorageImageArrayNonUniformIndexing','shaderInputAttachmentArrayNonUniformIndexing','shaderUniformTexelBufferArrayNonUniformIndexing','shaderStorageTexelBufferArrayNonUniformIndexing','descriptorBindingUniformBufferUpdateAfterBind','descriptorBindingSampledImageUpdateAfterBind','descriptorBindingStorageImageUpdateAfterBind','descriptorBindingStorageBufferUpdateAfterBind','descriptorBindingUniformTexelBufferUpdateAfterBind','descriptorBindingStorageTexelBufferUpdateAfterBind','descriptorBindingUpdateUnusedWhilePending','descriptorBindingPartiallyBound','descriptorBindingVariableDescriptorCount','runtimeDescriptorArray']
DescriptorIndexing=S('DescriptorIndexing',head+[(name,u) for name in names])
with Vulkan() as vk:
 descriptor=DescriptorIndexing(1000161001,None);features=Features2(1000059000,addr(descriptor));vk.vkGetPhysicalDeviceFeatures2(vk.physical,C.byref(features))
 report=dict(device=vk.name,features={name:bool(getattr(descriptor,name)) for name in names})
Path('.work/naga-csharp/descriptor-features.json').write_text(json.dumps(report,indent=2));print(json.dumps(report,indent=2))
