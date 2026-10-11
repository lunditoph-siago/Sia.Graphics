using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.WebGPU;

namespace SiaGpuDiagnostics;

internal static partial class CompilerGpuTests
{
    [ModuleInitializer]
    internal static void InitializeCallBinding() => TestModules.Register("call-binding", registry => {
        foreach (var binding in new[] { CallBinding.Function, CallBinding.Builtin })
            foreach (string variant in new[] { "wgsl", "spirv", "native-wgsl" })
                registry.Add(binding + "/" + variant, context => RunCallBinding(context, binding, variant), TestMode.Native, requiresGpu: true);
    });

    private static async Task RunCallBinding(TestContext context, CallBinding binding, string variant)
    {
        // The user function has the same signature/name as min but writes a counter
        // and adds its arguments. Builtin min must neither call it nor write the counter.
        var module = WgslReader.Parse("@group(0) @binding(1) var<storage,read_write> output:array<u32>; fn min(a:u32,b:u32)->u32{output[1]+=1u;return a+b;} @compute @workgroup_size(1) fn main(){}");
        var global = module.Globals.Single();
        var place = new Expression.Access(new Expression.Reference(global.Name, new ShaderType.Pointer(global.Type, global.Space)),
            Expression.U32(0), new ShaderType.Pointer(ShaderType.U32, global.Space));
        module.Functions.Single(f => f.Stage is not null).Body.Statements.Add(new Statement.Store(place,
            new Expression.Call("min", [Expression.U32(3), Expression.U32(7)], ShaderType.U32, binding)));
        var target = SpirvCompilationTarget.Default;
        byte[] native = SpirvWriter.Write(module, target);
        string source = WgslWriter.Write(variant == "native-wgsl" ? SpirvReader.Parse(native) : module, target);
        byte[]? spirv = variant == "spirv" ? native : null;
        uint[] expected = binding == CallBinding.Function ? [10, 1] : [3, 0];
        context.RecordShader(spirv is null ? "executed" : "wgsl-sidecar", source);
        context.Capture.Resources.Add(new { Binding = binding.ToString(), Variant = variant, TargetIdentity = target.Identity,
            NativeSha256 = Convert.ToHexString(SHA256.HashData(native)), Expected = expected,
            Purpose = "Explicit call identity: same-name function side effects/addition versus builtin minimum" });
        using var gpu = new GpuResources(context.Gpu.Device, context.Gpu.Queue);
        var dummy = gpu.Upload<uint>([0], WGPUBufferUsage.Storage);
        var output = gpu.Upload<uint>([0, 0], WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
        Dispatch(context, gpu, [dummy, output],
            [GpuBinding.Buffer(0, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute), GpuBinding.Buffer(1, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute)], source, spirv, "main");
        uint[] actual = await Read(context, gpu, output, 2);
        context.CompareWords("values", expected, actual);
    }
}
