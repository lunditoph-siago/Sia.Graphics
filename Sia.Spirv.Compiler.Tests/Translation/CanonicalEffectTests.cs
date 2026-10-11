using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Tests;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalEffectTests
{
    private const string Resources = "@group(0) @binding(0) var<storage,read> inputs:array<u32>; @group(0) @binding(1) var<storage,read_write> outputs:array<u32>;";
    internal const string AtomicSerial = Resources + "var<workgroup> value:atomic<u32>;@compute @workgroup_size(1) fn main(){atomicStore(&value,inputs[0]);let old=atomicAdd(&value,3u);loop{let r=atomicCompareExchangeWeak(&value,old+3u,9u);if r.exchanged{break;}}outputs[0]=old;outputs[1]=atomicLoad(&value);}";
    internal const string AtomicIndexCapture = Resources + "var<workgroup> values:array<atomic<u32>,2>;var<private> index:u32;fn change()->u32{index=1u;return 5u;}@compute @workgroup_size(1) fn main(){atomicStore(&values[0],inputs[0]);atomicStore(&values[1],20u);let p=&values[index];let old=atomicAdd(p,change());outputs[0]=old*100u+atomicLoad(&values[0]);outputs[1]=atomicLoad(&values[1]);}";
    internal const string WorkgroupReduction = Resources + "var<workgroup> count:atomic<u32>;var<workgroup> values:array<u32,4>;@compute @workgroup_size(4) fn main(@builtin(local_invocation_index) i:u32){values[i]=inputs[0]+i;_=atomicAdd(&count,i+1u);workgroupBarrier();if i==0u{outputs[0]=atomicLoad(&count);outputs[1]=values[0]+values[1]+values[2]+values[3];}}";
    internal const string UniformWorkgroupLoad = Resources + "var<workgroup> group_value:u32;@compute @workgroup_size(4) fn main(@builtin(local_invocation_index) i:u32){if i==0u{group_value=inputs[0];}workgroupBarrier();let v=workgroupUniformLoad(&group_value);if i==0u{outputs[0]=v;outputs[1]=v+1u;}}";
    internal const string TextureExplicit = "@group(0) @binding(0) var image:texture_2d<f32>;@group(0) @binding(1) var sample:sampler;@fragment fn main()->@location(0) vec4f{let size=textureDimensions(image);return textureLoad(image,vec2i(0),0)+textureSampleLevel(image,sample,vec2f(0.5),0.0f)+vec4f(f32(size.x));}";

    private static ControlFlowFunction[] Graphs(Module module) => module.Functions.Select(function => {
        Assert.True(StructuredControlFlowReader.TryRead(function, module, out var graph, out var reason), reason);
        ControlFlowAnalysis.RemoveUnreachable(graph!); ControlFlowVerifier.Validate(graph!, module); return graph!;
    }).ToArray();

    [Theory]
    [InlineData(AtomicSerial)] [InlineData(AtomicIndexCapture)] [InlineData(WorkgroupReduction)] [InlineData(UniformWorkgroupLoad)] [InlineData(TextureExplicit)]
    public void EffectsAndResourcesActuallyMigrateThroughBothFrontendRoutes(string source)
    {
        var input = WgslReader.Parse(source);
        foreach (var module in new[] { input, SpirvReader.Parse(SpirvWriter.Write(input, SpirvCompilationTarget.Default)) }) {
            var traces = new List<CanonicalPassTrace>(); var deferrals = new List<CanonicalDeferral>();
            var output = CanonicalShaderPipeline.Run(module, traces, deferrals);
            Assert.Empty(deferrals); Assert.Equal(module.Functions.Count * 4, traces.Count);
            Assert.All(module.Functions, f => Assert.NotSame(f, output.Functions.Single(p => p.Name == f.Name)));
            ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(output, SpirvCompilationTarget.Default)));
            ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(output, SpirvCompilationTarget.Default)));
        }
    }
    [Theory]
    [InlineData("atomicLoad", ShaderEffects.ReadMemory | ShaderEffects.Atomic)]
    [InlineData("atomicStore", ShaderEffects.WriteMemory | ShaderEffects.Atomic)]
    [InlineData("atomicAdd", ShaderEffects.ReadMemory | ShaderEffects.WriteMemory | ShaderEffects.Atomic)]
    [InlineData("textureLoad", ShaderEffects.ReadMemory | ShaderEffects.Resource)]
    [InlineData("textureStore", ShaderEffects.WriteMemory | ShaderEffects.Resource)]
    [InlineData("textureSample", ShaderEffects.ReadMemory | ShaderEffects.Resource | ShaderEffects.Convergent)]
    [InlineData("dpdx", ShaderEffects.Convergent)]
    [InlineData("subgroupAdd", ShaderEffects.Convergent)]
    public void EffectFactsSeparateAtomicResourceAndConvergenceRequirements(string name, object expected)
        => Assert.Equal((ShaderEffects)expected, ShaderBuiltinEffects.For(name));

    [Theory]
    [InlineData(true, 2u, 2u, 264u, false)] [InlineData(false, 0u, 1u, 72u, false)]
    [InlineData(true, 2u, 1u, 24648u, true)] [InlineData(false, 0u, 1u, 24648u, true)]
    public void NativeBarriersKeepTheirKindsAndExactScopeOrder(bool control, uint execution, uint scope, uint semantics, bool vulkan)
    {
        var module = SpirvReader.Parse(BarrierMemoryTests.Fixture(control, execution, scope, semantics, vulkan).ToBytes());
        var graphs = Graphs(module);
        var instruction = Assert.Single(graphs.SelectMany(g => g.Blocks).SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Barrier);
        var barrier = (ValueOperation.Barrier)instruction.Operation;
        Assert.Equal(control, barrier.Control); Assert.Equal(new(scope, semantics, control ? execution : null), barrier.NativeMemory);
        Assert.Equal(control, (instruction.Effects & ShaderEffects.Convergent) != 0);
        Assert.NotEqual(ShaderEffects.None, instruction.Effects & ShaderEffects.MemoryOrdering);
        var deferrals = new List<CanonicalDeferral>(); _ = CanonicalShaderPipeline.Run(module, deferrals: deferrals); Assert.Empty(deferrals);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        var emitted = Assert.Single(binary.Instructions, i => (Op)i.Opcode == (control ? Op.ControlBarrier : Op.MemoryBarrier));
        var constants = binary.Instructions.Where(i => (Op)i.Opcode == Op.Constant).ToDictionary(i => i.Operands[1], i => i.Operands[2]);
        Assert.Equal(control ? new[] { execution, scope, semantics } : new[] { scope, semantics }, emitted.Operands.Select(id => constants[id]));
    }
    [Theory]
    [InlineData(72u, 66u, false)] [InlineData(57416u, 49218u, true)]
    public void NativeAtomicOrderAndFailureOrderStayOnTheCanonicalInstruction(uint semantics, uint unequal, bool vulkan)
    {
        var module = SpirvReader.Parse(AtomicMemoryTests.Fixture(Op.AtomicCompareExchange, 1, semantics, unequal, vulkan).ToBytes());
        var graphs = Graphs(module);
        var graph = Assert.Single(graphs, g => g.Blocks.SelectMany(b => b.Instructions).Any(i => i.Operation is ValueOperation.Builtin { Function: "spirvAtomicCompareExchange" }));
        var instruction = Assert.Single(graph.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Builtin { Function: "spirvAtomicCompareExchange" });
        Assert.Equal(new(1, semantics, unequal), ((ValueOperation.Builtin)instruction.Operation).AtomicMemory);
        Assert.NotEqual(ShaderEffects.None, instruction.Effects & ShaderEffects.MemoryOrdering);
        Assert.Contains("UnequalSemantics", ControlFlowPrinter.Write(graph));
        var output = SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        var emitted = Assert.Single(output.Instructions, i => (Op)i.Opcode == Op.AtomicCompareExchange);
        var constants = output.Instructions.Where(i => (Op)i.Opcode == Op.Constant).ToDictionary(i => i.Operands[1], i => i.Operands[2]);
        Assert.Equal(new[] { 1u, semantics, unequal }, emitted.Operands.Skip(3).Take(3).Select(id => constants[id]));
    }
    [Fact]
    public void VerifierRejectsWrongAtomicValueTypesAndMixedBarrierScopes()
    {
        var module = WgslReader.Parse(AtomicSerial);
        Assert.True(StructuredControlFlowReader.TryRead(module.Functions.Single(), module, out var graph, out var reason), reason);
        ControlFlowAnalysis.RemoveUnreachable(graph!);
        var block = graph!.Blocks.First(b => b.Instructions.Any(i => i.Operation is ValueOperation.Builtin { Function: "atomicAdd" }));
        int index = block.Instructions.FindIndex(i => i.Operation is ValueOperation.Builtin { Function: "atomicAdd" });
        var instruction = block.Instructions[index]; var builtin = (ValueOperation.Builtin)instruction.Operation;
        block.Instructions[index] = instruction with { Operation = builtin with { Arguments = [builtin.Arguments[0], builtin.Arguments[0]] } };
        Assert.Contains("illegal Builtin", Assert.Throws<ShaderException>(() => ControlFlowVerifier.Validate(graph, module)).Message);
        block.Instructions[index] = new(null, new ValueOperation.Barrier(true, true, true, false, true));
        Assert.Contains("Mixed subgroup/workgroup", Assert.Throws<ShaderException>(() => ControlFlowVerifier.Validate(graph, module)).Message);
    }
    [Fact]
    public void HelperCallClosureRetainsConvergenceRequirementsAndRejectsTheirRemoval()
    {
        var module = WgslReader.Parse("fn fence(){workgroupBarrier();} fn nested(){fence();}@compute @workgroup_size(4) fn main(){nested();}");
        var graph = Graphs(module).Single(g => g.Signature.Stage is not null);
        var block = graph.Blocks.Single(b => b.Instructions.Any(i => i.Operation is ValueOperation.Call));
        int index = block.Instructions.FindIndex(i => i.Operation is ValueOperation.Call);
        var instruction = block.Instructions[index];
        Assert.Equal(ShaderEffects.Synchronization | ShaderEffects.Convergent | ShaderEffects.MemoryOrdering,
            instruction.Effects & (ShaderEffects.Synchronization | ShaderEffects.Convergent | ShaderEffects.MemoryOrdering));
        block.Instructions[index] = instruction with { Operation = ((ValueOperation.Call)instruction.Operation) with { CalleeEffects = ShaderEffects.None } };
        Assert.Contains("illegal Call", Assert.Throws<ShaderException>(() => ControlFlowVerifier.Validate(graph, module)).Message);
    }
    [Theory]
    [InlineData("Synchronize")] [InlineData("AtomicWorkgroup")]
    public void MaintainedCilSynchronizationAndAtomicKernelsActuallyMigrate(string kernelName)
    {
        var kernel = SpirvTestAssembly.GetKernel(typeof(ComputeShaders), kernelName);
        var module = new SpirvCompiler().CompileModule(new SpirvModuleCompilationRequest(File.ReadAllBytes(SpirvTestAssembly.Path), kernel.MetadataToken,
            File.ReadAllBytes(typeof(SpirvKernelAttribute).Assembly.Location)));
        var deferrals = new List<CanonicalDeferral>(); _ = CanonicalShaderPipeline.Run(module, deferrals: deferrals); Assert.Empty(deferrals);
    }
}
