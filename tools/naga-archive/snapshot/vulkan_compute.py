"""Task-only Vulkan 1.2 compute/readback runner, ctypes and system Vulkan loader.
ABI checked against .reference/SakuraEngine-engine/engine/packages/vulkan-headers/1.4.324.
No shader translation is performed by this runner.
"""
import ctypes as C, struct
u=C.c_uint32; q=C.c_uint64; p=C.c_void_p; z=C.c_size_t; i=C.c_int32
def S(name,fields): return type(name,(C.Structure,),{'_fields_':fields})
head=[('sType',u),('pNext',p)]
App=S('App',head+[('name',p),('version',u),('engine',p),('engineVersion',u),('api',u)])
InstanceInfo=S('InstanceInfo',head+[('flags',u),('app',p),('layers',u),('layerNames',p),('extensions',u),('extensionNames',p)])
Features2=S('Features2',head+[('features',u*55)])
Float16=S('Float16',head+[('float16',u),('int8',u)])
Storage16=S('Storage16',head+[('storage',u),('uniform',u),('push',u),('io',u)])
Maintenance4=S('Maintenance4',head+[('maintenance4',u)])
MemoryModel=S('MemoryModel',head+[('memoryModel',u),('deviceScope',u),('availabilityVisibilityChains',u)])
VariablePointers=S('VariablePointers',head+[('storageBuffer',u),('variablePointers',u)])
DescriptorIndexing=S('DescriptorIndexing',head+[('features',u*20)])
QueueInfo=S('QueueInfo',head+[('flags',u),('family',u),('count',u),('priorities',p)])
DeviceInfo=S('DeviceInfo',head+[('flags',u),('queues',u),('queueInfos',p),('layers',u),('layerNames',p),('extensions',u),('extensionNames',p),('features',p)])
BufferInfo=S('BufferInfo',head+[('flags',u),('size',q),('usage',u),('sharing',u),('families',u),('familyIndices',p)])
Requirements=S('Requirements',[('size',q),('alignment',q),('types',u)])
Allocation=S('Allocation',head+[('size',q),('type',u)])
Binding=S('Binding',[('binding',u),('type',u),('count',u),('stages',u),('samplers',p)])
LayoutInfo=S('LayoutInfo',head+[('flags',u),('count',u),('bindings',p)])
PipelineLayoutInfo=S('PipelineLayoutInfo',head+[('flags',u),('count',u),('layouts',p),('pushCount',u),('pushRanges',p)])
ModuleInfo=S('ModuleInfo',head+[('flags',u),('size',z),('code',p)])
Stage=S('Stage',head+[('flags',u),('stage',u),('module',q),('name',p),('specialization',p)])
SpecializationEntry=S('SpecializationEntry',[('id',u),('offset',u),('size',z)])
SpecializationInfo=S('SpecializationInfo',[('count',u),('entries',p),('size',z),('data',p)])
PipelineInfo=S('PipelineInfo',head+[('flags',u),('stage',Stage),('layout',q),('base',q),('baseIndex',i)])
PoolSize=S('PoolSize',[('type',u),('count',u)])
PoolInfo=S('PoolInfo',head+[('flags',u),('maxSets',u),('count',u),('sizes',p)])
SetInfo=S('SetInfo',head+[('pool',q),('count',u),('layouts',p)])
BufferDescriptor=S('BufferDescriptor',[('buffer',q),('offset',q),('range',q)])
Write=S('Write',head+[('set',q),('binding',u),('element',u),('count',u),('type',u),('images',p),('buffers',p),('views',p)])
CommandPoolInfo=S('CommandPoolInfo',head+[('flags',u),('family',u)])
CommandInfo=S('CommandInfo',head+[('pool',q),('level',u),('count',u)])
Begin=S('Begin',head+[('flags',u),('inheritance',p)])
Barrier=S('Barrier',head+[('source',u),('destination',u)])
FenceInfo=S('FenceInfo',head+[('flags',u)])
Submit=S('Submit',head+[('waitCount',u),('waits',p),('stages',p),('commandCount',u),('commands',p),('signalCount',u),('signals',p)])
def addr(value): return C.addressof(value)
def check(code,name):
    if code: raise RuntimeError(f'{name}: VkResult {code}')

class Vulkan:
    def __init__(self,maintenance4=False,vulkanMemory=False,variablePointers=False,descriptorIndexing=False):
        self.lib=C.WinDLL('vulkan-1.dll'); self.cleanup=[]; self.instance=p(); self.device=p()
        self.functions={}
        def bind(name,result,*args):
            function=getattr(self.lib,name); function.restype=result; function.argtypes=args
            self.functions[name]=function; return function
        signatures={
          'vkCreateInstance':(i,p,p,p),'vkDestroyInstance':(None,p,p),
          'vkEnumeratePhysicalDevices':(i,p,p,p),'vkGetPhysicalDeviceProperties':(None,p,p),
          'vkGetPhysicalDeviceFeatures2':(None,p,p),'vkGetPhysicalDeviceMemoryProperties':(None,p,p),
          'vkGetPhysicalDeviceQueueFamilyProperties':(None,p,p,p),'vkCreateDevice':(i,p,p,p,p),
          'vkDestroyDevice':(None,p,p),'vkGetDeviceQueue':(None,p,u,u,p),
          'vkCreateBuffer':(i,p,p,p,p),'vkDestroyBuffer':(None,p,q,p),'vkGetBufferMemoryRequirements':(None,p,q,p),
          'vkAllocateMemory':(i,p,p,p,p),'vkFreeMemory':(None,p,q,p),'vkBindBufferMemory':(i,p,q,q,q),
          'vkMapMemory':(i,p,q,q,q,u,p),'vkUnmapMemory':(None,p,q),
          'vkCreateDescriptorSetLayout':(i,p,p,p,p),'vkDestroyDescriptorSetLayout':(None,p,q,p),
          'vkCreatePipelineLayout':(i,p,p,p,p),'vkDestroyPipelineLayout':(None,p,q,p),
          'vkCreateShaderModule':(i,p,p,p,p),'vkDestroyShaderModule':(None,p,q,p),
          'vkCreateComputePipelines':(i,p,q,u,p,p,p),'vkDestroyPipeline':(None,p,q,p),
          'vkCreateDescriptorPool':(i,p,p,p,p),'vkDestroyDescriptorPool':(None,p,q,p),
          'vkAllocateDescriptorSets':(i,p,p,p),'vkUpdateDescriptorSets':(None,p,u,p,u,p),
          'vkCreateCommandPool':(i,p,p,p,p),'vkDestroyCommandPool':(None,p,q,p),
          'vkAllocateCommandBuffers':(i,p,p,p),'vkBeginCommandBuffer':(i,p,p),'vkEndCommandBuffer':(i,p),
          'vkCmdBindPipeline':(None,p,u,q),'vkCmdBindDescriptorSets':(None,p,u,q,u,u,p,u,p),
          'vkCmdDispatch':(None,p,u,u,u),'vkCmdPipelineBarrier':(None,p,u,u,u,u,p,u,p,u,p),
          'vkCreateFence':(i,p,p,p,p),'vkDestroyFence':(None,p,q,p),'vkQueueSubmit':(i,p,u,p,q),
          'vkWaitForFences':(i,p,u,p,u,q),'vkDeviceWaitIdle':(i,p),
        }
        for name,signature in signatures.items(): bind(name,*signature)
        try:
            app=App(0,None,None,0,None,0,(1<<22)|((3 if maintenance4 else 2)<<12)); info=InstanceInfo(1,None,0,addr(app),0,None,0,None)
            check(self.vkCreateInstance(C.byref(info),None,C.byref(self.instance)),'create instance')
            count=u(); check(self.vkEnumeratePhysicalDevices(self.instance,C.byref(count),None),'enumerate count')
            if not count.value: raise RuntimeError('No Vulkan device')
            devices=(p*count.value)(); check(self.vkEnumeratePhysicalDevices(self.instance,C.byref(count),devices),'enumerate devices')
            self.physical=devices[0]; properties=C.create_string_buffer(4096); self.vkGetPhysicalDeviceProperties(self.physical,properties)
            self.name=properties.raw[20:276].split(b'\0')[0].decode(errors='replace')
            if maintenance4 and struct.unpack_from('<I',properties.raw)[0] < (1<<22)|(3<<12):raise RuntimeError('Device lacks Vulkan 1.3 for LocalSizeId')
            queues=u(); self.vkGetPhysicalDeviceQueueFamilyProperties(self.physical,C.byref(queues),None)
            families=C.create_string_buffer(24*queues.value); self.vkGetPhysicalDeviceQueueFamilyProperties(self.physical,C.byref(queues),families)
            self.family=next(index for index in range(queues.value) if struct.unpack_from('<I',families.raw,index*24)[0]&2)
            storage=Storage16(1000083000,None,0,0,0,0); f16=Float16(1000082000,addr(storage),0,0)
            maintenance=Maintenance4(1000413000,addr(f16),0)
            chain=addr(maintenance) if maintenance4 else addr(f16)
            memoryModel=MemoryModel(1000211000,chain,0,0,0)
            if vulkanMemory:chain=addr(memoryModel)
            descriptors=DescriptorIndexing(1000161001,chain)
            if descriptorIndexing:chain=addr(descriptors)
            pointers=VariablePointers(1000120000,chain,0,0)
            if variablePointers:chain=addr(pointers)
            features=Features2(1000059000,chain); self.vkGetPhysicalDeviceFeatures2(self.physical,C.byref(features))
            if maintenance4 and not maintenance.maintenance4:raise RuntimeError('Device lacks maintenance4 for LocalSizeId')
            if vulkanMemory and not memoryModel.memoryModel:raise RuntimeError('Device lacks Vulkan memory model')
            if variablePointers and not pointers.storageBuffer:raise RuntimeError('Device lacks variablePointersStorageBuffer')
            if variablePointers == 'full' and not pointers.variablePointers:raise RuntimeError('Device lacks variablePointers')
            if variablePointers and variablePointers != 'full':pointers.variablePointers=0
            if descriptorIndexing and not descriptors.features[5]:raise RuntimeError('Device lacks shaderStorageBufferArrayNonUniformIndexing')
            self.features=dict(float16=bool(f16.float16),int16=bool(features.features[41]),int64=bool(features.features[40]),float64=bool(features.features[39]))
            if vulkanMemory:self.features.update(vulkanMemoryModel=bool(memoryModel.memoryModel),vulkanMemoryModelDeviceScope=bool(memoryModel.deviceScope),vulkanMemoryModelAvailabilityVisibilityChains=bool(memoryModel.availabilityVisibilityChains))
            if variablePointers:self.features.update(variablePointersStorageBuffer=bool(pointers.storageBuffer),variablePointers=bool(pointers.variablePointers))
            if descriptorIndexing:self.features.update(shaderStorageBufferArrayNonUniformIndexing=bool(descriptors.features[5]))
            priority=C.c_float(1); queue=QueueInfo(2,None,0,self.family,1,addr(priority))
            deviceInfo=DeviceInfo(3,chain,0,1,addr(queue),0,None,0,None,addr(features.features))
            check(self.vkCreateDevice(self.physical,C.byref(deviceInfo),None,C.byref(self.device)),'create device')
            self.queue=p(); self.vkGetDeviceQueue(self.device,self.family,0,C.byref(self.queue))
            self.memory=C.create_string_buffer(1024); self.vkGetPhysicalDeviceMemoryProperties(self.physical,self.memory)
        except: self.close(); raise
    def __getattr__(self,name): return self.functions[name]
    def create(self,kind,info):
        handle=q(); check(getattr(self,'vkCreate'+kind)(self.device,C.byref(info),None,C.byref(handle)),'create '+kind)
        self.cleanup.append(lambda: getattr(self,'vkDestroy'+kind)(self.device,handle,None)); return handle
    def buffer(self,data,usage=0x20):
        info=BufferInfo(12,None,0,len(data),usage,0,0,None); handle=q()
        check(self.vkCreateBuffer(self.device,C.byref(info),None,C.byref(handle)),'create buffer')
        req=Requirements(); self.vkGetBufferMemoryRequirements(self.device,handle,C.byref(req))
        count=struct.unpack_from('<I',self.memory.raw)[0]
        memoryType=next(index for index in range(count) if req.types&(1<<index) and struct.unpack_from('<I',self.memory.raw,4+index*8)[0]&6==6)
        memory=q(); allocation=Allocation(5,None,req.size,memoryType)
        try: check(self.vkAllocateMemory(self.device,C.byref(allocation),None,C.byref(memory)),'allocate memory')
        except: self.vkDestroyBuffer(self.device,handle,None); raise
        self.cleanup.append(lambda: self.vkFreeMemory(self.device,memory,None)); self.cleanup.append(lambda: self.vkDestroyBuffer(self.device,handle,None))
        check(self.vkBindBufferMemory(self.device,handle,memory,0),'bind memory')
        mapped=p(); check(self.vkMapMemory(self.device,memory,0,len(data),0,C.byref(mapped)),'map upload')
        try: C.memmove(mapped,data,len(data))
        finally: self.vkUnmapMemory(self.device,memory)
        return handle,memory
    def execute(self,spirv,inputs,outputSize,groups,specialization=None,entryPoint='main',inputDescriptorType=7,returnInput=False):
        first,inputMemory=self.buffer(inputs,0x10 if inputDescriptorType==6 else 0x20); second,memory=self.buffer(struct.pack('<I',0xdeadbeef)*(outputSize//4))
        bindings=(Binding*2)(Binding(0,inputDescriptorType,1,0x20,None),Binding(1,7,1,0x20,None))
        layout=self.create('DescriptorSetLayout',LayoutInfo(32,None,0,2,addr(bindings)))
        pipelineLayout=self.create('PipelineLayout',PipelineLayoutInfo(30,None,0,1,addr(layout),0,None))
        code=C.create_string_buffer(spirv); module=self.create('ShaderModule',ModuleInfo(16,None,0,len(spirv),addr(code)))
        entry=C.create_string_buffer(entryPoint.encode('utf-8')); stage=Stage(18,None,0,0x20,module.value,addr(entry),None)
        if specialization:
            raw=b''; entries=[]
            for key,value in specialization.items():
                entries.append(SpecializationEntry(key,len(raw),len(value)));raw+=value
            specializationEntries=(SpecializationEntry*len(entries))(*entries);specializationData=C.create_string_buffer(raw)
            specializationInfo=SpecializationInfo(len(entries),addr(specializationEntries),len(raw),addr(specializationData))
            stage.specialization=addr(specializationInfo)
        pipelineInfo=PipelineInfo(29,None,0,stage,pipelineLayout.value,0,-1); pipeline=q()
        check(self.vkCreateComputePipelines(self.device,0,1,C.byref(pipelineInfo),None,C.byref(pipeline)),'create compute pipeline')
        self.cleanup.append(lambda: self.vkDestroyPipeline(self.device,pipeline,None))
        sizes=(PoolSize*1)(PoolSize(7,2)) if inputDescriptorType==7 else (PoolSize*2)(PoolSize(inputDescriptorType,1),PoolSize(7,1))
        pool=self.create('DescriptorPool',PoolInfo(33,None,0,1,len(sizes),addr(sizes)))
        setInfo=SetInfo(34,None,pool.value,1,addr(layout)); descriptor=q(); check(self.vkAllocateDescriptorSets(self.device,C.byref(setInfo),C.byref(descriptor)),'allocate set')
        buffers=(BufferDescriptor*2)(BufferDescriptor(first.value,0,len(inputs)),BufferDescriptor(second.value,0,outputSize))
        writes=(Write*2)(Write(35,None,descriptor.value,0,0,1,inputDescriptorType,None,addr(buffers[0]),None),Write(35,None,descriptor.value,1,0,1,7,None,addr(buffers[1]),None))
        self.vkUpdateDescriptorSets(self.device,2,writes,0,None)
        commandPool=self.create('CommandPool',CommandPoolInfo(39,None,0,self.family)); allocation=CommandInfo(40,None,commandPool.value,0,1)
        command=p(); check(self.vkAllocateCommandBuffers(self.device,C.byref(allocation),C.byref(command)),'allocate command buffer')
        begin=Begin(42,None,1,None); check(self.vkBeginCommandBuffer(command,C.byref(begin)),'begin commands')
        self.vkCmdBindPipeline(command,1,pipeline); self.vkCmdBindDescriptorSets(command,1,pipelineLayout,0,1,C.byref(descriptor),0,None)
        self.vkCmdDispatch(command,groups,1,1)
        barrier=Barrier(46,None,0x40,0x2000); self.vkCmdPipelineBarrier(command,0x800,0x4000,0,1,C.byref(barrier),0,None,0,None)
        check(self.vkEndCommandBuffer(command),'end commands')
        fence=self.create('Fence',FenceInfo(8,None,0)); submit=Submit(4,None,0,None,None,1,addr(command),0,None)
        check(self.vkQueueSubmit(self.queue,1,C.byref(submit),fence),'submit')
        check(self.vkWaitForFences(self.device,1,C.byref(fence),1,10_000_000_000),'wait (10 seconds)')
        if returnInput:memory=inputMemory;outputSize=len(inputs)
        mapped=p(); check(self.vkMapMemory(self.device,memory,0,outputSize,0,C.byref(mapped)),'map readback')
        try: return C.string_at(mapped,outputSize)
        finally: self.vkUnmapMemory(self.device,memory)
    def close(self):
        if self.device.value:
            self.vkDeviceWaitIdle(self.device)
            for release in reversed(self.cleanup): release()
            self.cleanup.clear(); self.vkDestroyDevice(self.device,None); self.device=p()
        if self.instance.value: self.vkDestroyInstance(self.instance,None); self.instance=p()
    def __enter__(self): return self
    def __exit__(self,*args): self.close()
