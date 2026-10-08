"""Task-only adapter: two storage descriptors at binding0, output at binding1.
Uses the existing Vulkan owner/cleanup; no translation or expected-value logic.
"""
import ctypes as C
from vulkan_compute import Vulkan as Base,Binding,LayoutInfo,PoolInfo,PoolSize,Write,BufferDescriptor,addr,check,p

class Vulkan(Base):
 def __init__(self,*args,**kwargs):
  super().__init__(*args,**kwargs);self.recording=False
  layout=self.functions['vkCreateDescriptorSetLayout'];pool=self.functions['vkCreateDescriptorPool'];update=self.functions['vkUpdateDescriptorSets']
  def create_layout(device,info,allocator,result):
   description=C.cast(info,C.POINTER(LayoutInfo)).contents;bindings=C.cast(description.bindings,C.POINTER(Binding));bindings[0].count=2
   return layout(device,info,allocator,result)
  def create_pool(device,info,allocator,result):
   description=C.cast(info,C.POINTER(PoolInfo)).contents;sizes=C.cast(description.sizes,C.POINTER(PoolSize));sizes[0].count=3
   return pool(device,info,allocator,result)
  def write(device,count,writes,copyCount,copies):
   descriptions=C.cast(writes,C.POINTER(Write));first=C.cast(descriptions[0].buffers,C.POINTER(BufferDescriptor)).contents
   second=first if self.alias else BufferDescriptor(self.extra[0].value,0,len(self.secondInput))
   descriptors=(BufferDescriptor*2)(first,second);descriptions[0].count=2;descriptions[0].buffers=addr(descriptors)
   return update(device,count,writes,copyCount,copies)
  self.functions.update(vkCreateDescriptorSetLayout=create_layout,vkCreateDescriptorPool=create_pool,vkUpdateDescriptorSets=write)
 def buffer(self,*args,**kwargs):
  result=super().buffer(*args,**kwargs)
  if self.recording:self.buffers.append(result)
  return result
 def read(self,memory,size):
  mapped=p();check(self.vkMapMemory(self.device,memory,0,size,0,C.byref(mapped)),'map descriptor readback')
  try:return C.string_at(mapped,size)
  finally:self.vkUnmapMemory(self.device,memory)
 def execute(self,spirv,firstInput,secondInput,alias=False,specialization=None):
  self.alias=alias;self.secondInput=secondInput
  if not alias:self.extra=self.buffer(secondInput)
  self.buffers=[];self.recording=True
  try:output=super().execute(spirv,firstInput,16,1,specialization=specialization)
  finally:self.recording=False
  first=self.read(self.buffers[0][1],len(firstInput));second=first if alias else self.read(self.extra[1],len(secondInput))
  return output,first,second
