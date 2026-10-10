using System.Runtime.CompilerServices;
using System.Text;
using Sia.Spirv.Compiler;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.WebGPU;

namespace SiaGpuDiagnostics;

internal static partial class CompilerGpuTests
{
    [ModuleInitializer]
    internal static void Initialize() => TestModules.Register("compiler", registry => {
        foreach (string kernel in new[] { "Synchronize", "CopyVectors", "UseHelpers", "AtomicWorkgroup", "CopyStructs", "CopyPackedStructs", "CopyAlignedStructs", "CopyLogicalStructs", "CopyBoundedStructs", "IntegerControlFlow", "SpeculativeSelection" })
            foreach (string variant in new[] { "direct-wgsl", "direct-spirv", "static-wgsl", "static-spirv" })
                registry.Add(kernel+"/"+variant, context => Run(context, kernel, variant), TestMode.Native, requiresGpu:true);
    });

    private static byte[] Asset(string name) {
        using var stream = typeof(CompilerGpuTests).Assembly.GetManifestResourceStream("fixtures/"+name) ?? throw new FileNotFoundException(name);
        using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }
    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
    private static async Task Run(TestContext context, string name, string variant) {
        var assembly = Asset("shaders.dll"); var core = Asset("intrinsics.dll");
        var kernel = new SpirvFrontend().Analyze(assembly, core).Kernels.Single(k => k.Name==name);
        context.Capture.Resources.Add(new {
            kernel.QualifiedName, kernel.MetadataToken, Variant = variant,
            ShaderAssemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(assembly)),
            IntrinsicAssemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(core)),
            TargetIdentity = variant.StartsWith("direct", StringComparison.Ordinal) ? SpirvCompilationTarget.Default.Identity : null,
            KernelAbi = SpirvKernelAbi.WebGpu.ToString()
        });
        string source; byte[]? spirv;
        if (variant.StartsWith("direct", StringComparison.Ordinal)) {
            var request = new SpirvModuleCompilationRequest(assembly, kernel.MetadataToken, core);
            var module = new SpirvCompiler().CompileModule(request);
            source = WgslWriter.Write(module, request.Target); spirv = variant.EndsWith("spirv", StringComparison.Ordinal) ? SpirvWriter.Write(module, new() { Target = request.Target }) : null;
        } else {
            string file = kernel.QualifiedName;
            source = Encoding.UTF8.GetString(Asset(file+".wgsl"));
            spirv = variant.EndsWith("spirv", StringComparison.Ordinal) ? Asset(file+".spv") : null;
        }
        context.RecordShader("executed", source);
        using var gpu = new GpuResources(context.Gpu.Device, context.Gpu.Queue);
        var buffers = new List<GpuResource>(); var entries = new List<WGPUBindGroupLayoutEntry>();
        var expected = new Dictionary<int, uint[]>();
        var checkedIndices = new Dictionary<int, int[]>();
        void Buffer(uint[] words, WGPUBufferBindingType type, uint[]? result = null, int[]? indices = null) {
            int index = buffers.Count;
            buffers.Add(gpu.Upload<uint>(words, (type==WGPUBufferBindingType.Uniform ? WGPUBufferUsage.Uniform : WGPUBufferUsage.Storage)|WGPUBufferUsage.CopySrc));
            entries.Add(GpuBinding.Buffer((uint)index, type, WGPUShaderStage.Compute));
            if (result is not null) { expected.Add(index,result); if (indices is not null) checkedIndices.Add(index,indices); }
        }
        if (name=="Synchronize") {
            uint[] data=Enumerable.Range(0,64).Select(i=>Bits(i)).ToArray();
            Buffer(data,WGPUBufferBindingType.Storage,data); Buffer(new uint[4],WGPUBufferBindingType.Uniform);
        } else if (name=="CopyVectors") {
            uint[] data=Enumerable.Range(0,256).Select(i=>Bits(i*0.25f-20)).ToArray();
            Buffer(data,WGPUBufferBindingType.ReadOnlyStorage);
            Buffer(new uint[256],WGPUBufferBindingType.Storage,data.Select(v=>Bits(BitConverter.UInt32BitsToSingle(v)+1)).ToArray());
        } else if (name=="UseHelpers") {
            uint[] data=Enumerable.Range(0,64).Select(i=>Bits(i*0.5f-10)).ToArray();
            Buffer(data,WGPUBufferBindingType.Storage,data.Select(v=>{float x=BitConverter.UInt32BitsToSingle(v);return Bits(x*x+1);}).ToArray());
        } else if (name=="AtomicWorkgroup") {
            Buffer(new uint[2],WGPUBufferBindingType.Storage,new uint[]{528,0});
            checkedIndices.Add(0,new int[]{0}); // Exchange winner is nondeterministic, but must be a local invocation.
        } else if (name=="IntegerControlFlow") {
            int value=-1234567,shift=35; uint unsigned=0xabcdef01, iterations=13;
            Buffer(new uint[1],WGPUBufferBindingType.Storage,new uint[]{unchecked((uint)(~value ^ (value>>shift) ^ int.MinValue))});
            Buffer(new uint[2],WGPUBufferBindingType.Storage,new uint[]{(unsigned<<shift)^(unsigned>>shift)^(iterations*(iterations-1)/2),unsigned<<40});
            Buffer(new uint[]{unchecked((uint)value),unsigned,(uint)shift,iterations,1,0,0,0},WGPUBufferBindingType.Uniform);
        } else if (name=="SpeculativeSelection") {
            // condition < -1 takes the early-exit branch and adds throughput.
            float expectedValue=0.8f+0f+0.3f/MathF.Sqrt(1.13f)+0.8f;
            Buffer(new uint[1],WGPUBufferBindingType.Storage,new uint[]{Bits(expectedValue)});
            Buffer(new uint[]{Bits(-2),0,0,0},WGPUBufferBindingType.Uniform);
        } else {
            int count=name=="CopyBoundedStructs"?4:64;
            int stride=name is "CopyStructs" or "CopyAlignedStructs"?8:4;
            uint[] data=Enumerable.Range(0,count*stride).Select(i=>0x3f000000u+(uint)i*17).ToArray();
            int[] fields=name=="CopyStructs"?new[]{0,1,2,3,4}:name=="CopyAlignedStructs"?new[]{0,4,5,6}:new[]{0,1,2,3};
            Buffer(data,WGPUBufferBindingType.ReadOnlyStorage);
            Buffer(Enumerable.Repeat(0xdeadbeefu,data.Length).ToArray(),WGPUBufferBindingType.Storage,data,
                Enumerable.Range(0,count).SelectMany(i=>fields.Select(f=>i*stride+f)).ToArray());
        }
        Dispatch(context,gpu,buffers,entries,source,spirv,name);
        foreach (var pair in expected) {
            uint[] actual=await Read(context,gpu,buffers[pair.Key],pair.Value.Length);
            context.RecordBytes("buffer"+pair.Key,System.Runtime.InteropServices.MemoryMarshal.AsBytes(actual.AsSpan()));
            if (name=="SpeculativeSelection") TestContext.Require(MathF.Abs(BitConverter.UInt32BitsToSingle(actual[0])-BitConverter.UInt32BitsToSingle(pair.Value[0]))<0.00001f,"Float branch result differs");
            else if (checkedIndices.TryGetValue(pair.Key,out var indices)) foreach(int index in indices) TestContext.Equal(pair.Value[index],actual[index],"Buffer field "+index);
            else context.CompareWords("buffer"+pair.Key,pair.Value,actual);
            if (name=="AtomicWorkgroup") TestContext.Require(actual[1]<32,"Atomic exchange winner is outside the workgroup");
        }
    }
    private static unsafe void Dispatch(TestContext context,GpuResources gpu,List<GpuResource> buffers,List<WGPUBindGroupLayoutEntry> entries,string source,byte[]? spirv,string name) {
        var layout=GpuBinding.Layout(gpu,entries.ToArray());
        var group=GpuBinding.Group(gpu,layout,buffers.Select((b,i)=>GpuBinding.Buffer((uint)i,b)).ToArray());
        var shader=gpu.Own(spirv is null?Wgpu.CreateWgslShaderModule(gpu.Device,source,name):Wgpu.CreateSpirvShaderModule(gpu.Device,spirv,name));
        if(DeviceSession.Errors.TryPeek(out var error))throw new InvalidOperationException(error);
        var bytes=Encoding.UTF8.GetBytes(name);
        fixed(byte* text=bytes){
            var descriptor=WGPUComputePipelineDescriptor.Default;
            descriptor.Layout=(WGPUPipelineLayout*)GpuBinding.PipelineLayout(gpu,layout).Handle;
            descriptor.Compute.Module=(WGPUShaderModule*)shader.Handle;
            descriptor.Compute.EntryPoint=new(){Data=text,Length=(nuint)bytes.Length};
            var pipeline=gpu.Own(Wgpu.CreateComputePipeline(gpu.Device,descriptor));
            if(DeviceSession.Errors.TryPeek(out error))throw new InvalidOperationException(error);
            var encoder=Wgpu.CreateCommandEncoder(gpu.Device);var pass=Wgpu.BeginComputePass(encoder,WGPUComputePassDescriptor.Default);
            try{Wgpu.SetComputePipeline(pass,pipeline.GetWgpu<WGPUComputePipeline>());Wgpu.SetBindGroup(pass,0,group.GetWgpu<WGPUBindGroup>());Wgpu.DispatchWorkgroups(pass,1);Wgpu.EndComputePass(pass);Submit(context.Gpu,encoder);}
            finally{Wgpu.Release(ref pass);Wgpu.Release(ref encoder);}
            context.Capture.Commands.Add("dispatch(1,1,1); queue.submit");
        }
    }
    private static async Task<uint[]> Read(TestContext context,GpuResources gpu,GpuResource output,int words){
        ulong size=(ulong)words*4;var staging=gpu.Buffer(size,WGPUBufferUsage.MapRead|WGPUBufferUsage.CopyDst).GetWgpu<WGPUBuffer>();
        var encoder=Wgpu.CreateCommandEncoder(gpu.Device);
        try{Wgpu.CopyBufferToBuffer(encoder,output.GetWgpu<WGPUBuffer>(),0,staging,0,size);Submit(context.Gpu,encoder);}finally{Wgpu.Release(ref encoder);}
        await context.Gpu.WaitAsync(Wgpu.MapBufferReadAsync(staging,0,size));
        try{return Wgpu.GetMappedRangeReadOnly<uint>(staging,0,words).ToArray();}finally{Wgpu.UnmapBuffer(staging);}
    }
    private static void Submit(DeviceSession session,WgpuHandle<WGPUCommandEncoder> encoder){
        var command=Wgpu.FinishCommandEncoder(encoder,WGPUCommandBufferDescriptor.Default);try{Wgpu.Submit(session.Queue,[command]);}finally{Wgpu.Release(ref command);}
    }
}
