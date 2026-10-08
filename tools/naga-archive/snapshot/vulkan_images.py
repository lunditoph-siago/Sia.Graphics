"""Task-only Vulkan storage image atomics/readback. ABI fields checked against local Vulkan headers 1.4.324."""
from vulkan_compute import *
Extent=S('Extent',[('x',u),('y',u),('z',u)])
Offset=S('Offset',[('x',i),('y',i),('z',i)])
Range=S('Range',[('aspect',u),('mip',u),('levels',u),('layer',u),('layers',u)])
Layers=S('Layers',[('aspect',u),('mip',u),('layer',u),('layers',u)])
ImageInfo=S('ImageInfo',head+[('flags',u),('type',u),('format',u),('extent',Extent),('mips',u),('layers',u),('samples',u),('tiling',u),('usage',u),('sharing',u),('families',u),('indices',p),('layout',u)])
ViewInfo=S('ViewInfo',head+[('flags',u),('image',q),('type',u),('format',u),('components',u*4),('range',Range)])
ImageBarrier=S('ImageBarrier',head+[('source',u),('destination',u),('old',u),('new',u),('sourceFamily',u),('destinationFamily',u),('image',q),('range',Range)])
Copy=S('Copy',[('offset',q),('rowLength',u),('imageHeight',u),('layers',Layers),('imageOffset',Offset),('extent',Extent)])
ImageDescriptor=S('ImageDescriptor',[('sampler',q),('view',q),('layout',u)])
assert [C.sizeof(t) for t in [ImageInfo,ViewInfo,ImageBarrier,Copy,ImageDescriptor]]==[88,80,72,56,24]

class ImageVulkan(Vulkan):
    def __init__(self):
        super().__init__()
        signatures={
            'vkGetPhysicalDeviceFormatProperties':(None,p,u,p),
            'vkCreateImage':(i,p,p,p,p),'vkDestroyImage':(None,p,q,p),'vkGetImageMemoryRequirements':(None,p,q,p),'vkBindImageMemory':(i,p,q,q,q),
            'vkCreateImageView':(i,p,p,p,p),'vkDestroyImageView':(None,p,q,p),
            'vkCmdClearColorImage':(None,p,q,u,p,u,p),'vkCmdCopyImageToBuffer':(None,p,q,u,q,u,p),
        }
        for name,signature in signatures.items():
            function=getattr(self.lib,name);function.restype=signature[0];function.argtypes=signature[1:];self.functions[name]=function

    def execute_image(self,spirv,signed=False,arrayed=False,format=None,bytesPerTexel=4,outputSize=40,requireAtomic=True,rawImage=False):
        format=(99 if signed else 98) if format is None else format;properties=(u*3)();self.vkGetPhysicalDeviceFormatProperties(self.physical,format,properties)
        if not properties[1]&(4 if requireAtomic else 2):raise RuntimeError('Device lacks required optimal storage image format support')
        width,height,layers=8,2,3 if arrayed else 1;imageBytes=width*height*layers*bytesPerTexel;subrange=Range(1,0,1,0,layers)
        image=self.create('Image',ImageInfo(14,None,0,1,format,Extent(width,height,1),1,layers,1,0,1|2|8,0,0,None,0))
        req=Requirements();self.vkGetImageMemoryRequirements(self.device,image,C.byref(req));memoryType=next(n for n in range(struct.unpack_from('<I',self.memory.raw)[0]) if req.types&(1<<n))
        imageMemory=q();allocation=Allocation(5,None,req.size,memoryType);check(self.vkAllocateMemory(self.device,C.byref(allocation),None,C.byref(imageMemory)),'allocate image memory')
        # Move image destruction after freeing memory in the reverse-order cleanup list.
        releaseImage=self.cleanup.pop();self.cleanup.append(lambda:self.vkFreeMemory(self.device,imageMemory,None));self.cleanup.append(releaseImage)
        check(self.vkBindImageMemory(self.device,image,imageMemory,0),'bind image memory')
        view=self.create('ImageView',ViewInfo(15,None,0,image.value,5 if arrayed else 1,format,(u*4)(0,0,0,0),subrange))
        readback,imageReadMemory=self.buffer(b'\x00'*imageBytes,2)
        outputs,outputMemory=self.buffer(struct.pack('<I',0xdeadbeef)*(outputSize//4))
        bindings=(Binding*2)(Binding(0,3,1,0x20,None),Binding(1,7,1,0x20,None))
        layout=self.create('DescriptorSetLayout',LayoutInfo(32,None,0,2,addr(bindings)));pipelineLayout=self.create('PipelineLayout',PipelineLayoutInfo(30,None,0,1,addr(layout),0,None))
        code=C.create_string_buffer(spirv);module=self.create('ShaderModule',ModuleInfo(16,None,0,len(spirv),addr(code)))
        entry=C.create_string_buffer(b'main');stage=Stage(18,None,0,0x20,module.value,addr(entry),None);pipelineInfo=PipelineInfo(29,None,0,stage,pipelineLayout.value,0,-1);pipeline=q()
        check(self.vkCreateComputePipelines(self.device,0,1,C.byref(pipelineInfo),None,C.byref(pipeline)),'create image pipeline');self.cleanup.append(lambda:self.vkDestroyPipeline(self.device,pipeline,None))
        sizes=(PoolSize*2)(PoolSize(3,1),PoolSize(7,1));pool=self.create('DescriptorPool',PoolInfo(33,None,0,1,2,addr(sizes)));setInfo=SetInfo(34,None,pool.value,1,addr(layout));descriptor=q();check(self.vkAllocateDescriptorSets(self.device,C.byref(setInfo),C.byref(descriptor)),'allocate image set')
        imageDesc=ImageDescriptor(0,view.value,1);bufferDesc=BufferDescriptor(outputs.value,0,outputSize)
        writes=(Write*2)(Write(35,None,descriptor.value,0,0,1,3,addr(imageDesc),None,None),Write(35,None,descriptor.value,1,0,1,7,None,addr(bufferDesc),None));self.vkUpdateDescriptorSets(self.device,2,writes,0,None)
        commandPool=self.create('CommandPool',CommandPoolInfo(39,None,0,self.family));commandAllocation=CommandInfo(40,None,commandPool.value,0,1);command=p();check(self.vkAllocateCommandBuffers(self.device,C.byref(commandAllocation),C.byref(command)),'allocate image command');begin=Begin(42,None,1,None);check(self.vkBeginCommandBuffer(command,C.byref(begin)),'begin image commands')
        barrier=ImageBarrier(45,None,0,0x1000,0,1,0xffffffff,0xffffffff,image.value,subrange);self.vkCmdPipelineBarrier(command,1,0x1000,0,0,None,0,None,1,C.byref(barrier))
        clear=(u*4)(0x13579bdf,0,0,0);self.vkCmdClearColorImage(command,image,1,clear,1,C.byref(subrange))
        barrier=ImageBarrier(45,None,0x1000,0x20|0x40,1,1,0xffffffff,0xffffffff,image.value,subrange);self.vkCmdPipelineBarrier(command,0x1000,0x800,0,0,None,0,None,1,C.byref(barrier))
        self.vkCmdBindPipeline(command,1,pipeline);self.vkCmdBindDescriptorSets(command,1,pipelineLayout,0,1,C.byref(descriptor),0,None);self.vkCmdDispatch(command,1,1,1)
        barrier=ImageBarrier(45,None,0x40,0x800,1,1,0xffffffff,0xffffffff,image.value,subrange);self.vkCmdPipelineBarrier(command,0x800,0x1000,0,0,None,0,None,1,C.byref(barrier))
        region=Copy(0,0,0,Layers(1,0,0,layers),Offset(0,0,0),Extent(width,height,1));self.vkCmdCopyImageToBuffer(command,image,1,readback,1,C.byref(region))
        memoryBarrier=Barrier(46,None,0x40|0x1000,0x2000);self.vkCmdPipelineBarrier(command,0x800|0x1000,0x4000,0,1,C.byref(memoryBarrier),0,None,0,None)
        check(self.vkEndCommandBuffer(command),'end image commands');fence=self.create('Fence',FenceInfo(8,None,0));submit=Submit(4,None,0,None,None,1,addr(command),0,None);check(self.vkQueueSubmit(self.queue,1,C.byref(submit),fence),'submit image commands');check(self.vkWaitForFences(self.device,1,C.byref(fence),1,10_000_000_000),'image fence (10 seconds)')
        def read(memory,size,raw=False):
            mapped=p();check(self.vkMapMemory(self.device,memory,0,size,0,C.byref(mapped)),'map image readback')
            try:return C.string_at(mapped,size) if raw else list(struct.unpack('<'+str(size//4)+'I',C.string_at(mapped,size)))
            finally:self.vkUnmapMemory(self.device,memory)
        return read(outputMemory,outputSize),read(imageReadMemory,imageBytes,rawImage)
