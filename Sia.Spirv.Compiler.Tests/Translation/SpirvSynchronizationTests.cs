using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class SpirvSynchronizationTests
{
    [Fact]
    public void GeneratedCapturesAvoidConstantsAndFutureLocalNames()
    {
        var module = RawOrderedFixture();
        module.Constants.Add(new("sia_spv_sync_0",ShaderType.U32,Expression.U32(99)));
        module.Functions.Single(f => f.Stage == ShaderStage.Compute).Body.Statements.Add(new Statement.Declare("sia_spv_sync_1",ShaderType.U32,Expression.U32(100),false));
        var prepared = SpirvSynchronizationLowering.Run(module);
        ModuleValidator.Validate(prepared);
        var names = prepared.Functions.Single(f => f.Stage == ShaderStage.Compute).Body.Statements.OfType<Statement.Declare>().Select(d => d.Name).ToArray();
        Assert.DoesNotContain("sia_spv_sync_0",names);
        Assert.Equal(names.Length,names.Distinct(StringComparer.Ordinal).Count());
        Assert.Single(names,n => n == "sia_spv_sync_1");
    }

    internal const string AtomicSource = """
        var<workgroup> group_counter:atomic<u32>;
        @group(0) @binding(0) var<storage,read_write> output:array<u32>;
        @compute @workgroup_size(1) fn main(){atomicStore(&group_counter,9u);
            output[0]=workgroupUniformLoad(&group_counter);output[1]=atomicAdd(&group_counter,5u);
            output[2]=workgroupUniformLoad(&group_counter);}
        """;

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AtomicUniformLoadsKeepCollectiveReadsAndWorkgroupScope(bool vulkan)
    {
        var input = WgslReader.Parse(AtomicSource); input.VulkanMemoryModel = vulkan;
        var prepared = SpirvPhysicalLayoutLowering.Prepare(input);
        ModuleValidator.Validate(prepared.Module);
        var graph = prepared.ControlFlow["main"].Graph;
        ControlFlowVerifier.Validate(graph,prepared.Module);
        var operations = graph.Blocks.SelectMany(b => b.Instructions).Select(i => i.Operation).ToArray();
        Assert.DoesNotContain(operations,o => o is ValueOperation.Builtin { Function:"workgroupUniformLoad" });
        Assert.Equal(4,operations.OfType<ValueOperation.Barrier>().Count());
        var reads = operations.OfType<ValueOperation.Builtin>().Where(c => c.Function == "atomicLoad").ToArray();
        Assert.Equal(2,reads.Length);
        Assert.All(reads,c => Assert.Equal(new SpirvAtomicMemory(2,0),c.AtomicMemory));
        ModuleValidator.ValidateNative(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()));
    }

    [Fact]
    public void UserFunctionNamesDoNotTriggerSynchronizationLowering()
    {
        var module = new Module(); module.Functions.Add(new ShaderFunction("workgroupUniformLoad")); module.Functions.Add(new ShaderFunction("workgroupBarrier"));
        var caller = new ShaderFunction("caller"); module.Functions.Add(caller);
        caller.Body.Statements.Add(new Statement.Evaluate(new Expression.Call("workgroupUniformLoad",[],new ShaderType.Void())));
        caller.Body.Statements.Add(new Statement.Evaluate(new Expression.Call("workgroupBarrier",[],new ShaderType.Void())));
        Assert.Same(module,SpirvSynchronizationLowering.Run(module));
        Assert.All(caller.Body.Statements,s => Assert.IsType<Expression.Call>(Assert.IsType<Statement.Evaluate>(s).Value));
    }

    [Fact]
    public void ExistingNativeOperandsAndDefaultMemoryOnlyBarrierRemainExplicit()
    {
        var module = new Module(); var function = new ShaderFunction("main") { Stage = ShaderStage.Compute }; module.Functions.Add(function);
        var native = new SpirvBarrierMemory(1,72,2);
        function.Body.Statements.Add(new Statement.Barrier(true,false) { NativeMemory = native });
        function.Body.Statements.Add(new Statement.MemoryBarrier(false,true));
        var prepared = SpirvSynchronizationLowering.Run(module);
        Assert.Same(function.Body.Statements[0],prepared.Functions.Single().Body.Statements[0]);
        Assert.Equal(new SpirvBarrierMemory(2,264),Assert.IsType<Statement.MemoryBarrier>(prepared.Functions.Single().Body.Statements[1]).NativeMemory);
        ModuleValidator.Validate(prepared);
    }

    internal const string OrderedSource = """
        var<workgroup> group_data:array<u32,2>;
        @group(0) @binding(0) var<storage,read_write> output:array<u32>;
        @group(0) @binding(1) var<storage,read_write> counter:array<u32>;
        fn mark(code:u32)->u32{counter[0]=counter[0]*10u+code;return code;}
        fn get_index()->u32{counter[0]=counter[0]*10u+1u;return 1u;}
        fn get_target()->u32{counter[0]=counter[0]*10u+5u;return 2u;}
        @compute @workgroup_size(1) fn main(){
            group_data[0]=11u;group_data[1]=22u;
            output[0]=mark(3u)+workgroupUniformLoad(&group_data[get_index()]);
            output[1]=workgroupUniformLoad(&group_data[0])+mark(4u);
            output[get_target()]=workgroupUniformLoad(&group_data[1]);
            loop{let p=&group_data[0];continuing{mark(6u);break if workgroupUniformLoad(p)==11u;}}
            output[3]=workgroupUniformLoad(&group_data[0]);
        }
        """;

    internal static Module RawOrderedFixture()
    {
        var module = WgslReader.Parse(OrderedSource);
        var main = module.Functions.Single(f => f.Stage == ShaderStage.Compute); main.Body.Statements.Clear();
        Expression.Call Mark(uint code) => new("mark", [Expression.U32(code)], ShaderType.U32) { Binding = CallBinding.Function };
        Expression Place(string name, Expression index) {
            var g = module.Globals.Single(g => g.Name == name);
            return new Expression.Access(new Expression.Reference(name,new ShaderType.Pointer(g.Type,g.Space)),index,new ShaderType.Pointer(ShaderType.U32,g.Space));
        }
        Expression Address(Expression place) => new Expression.Unary("&",place,place.Type);
        Expression.Call Uniform(Expression pointer) => new("workgroupUniformLoad",[pointer],ShaderType.U32) { Binding = CallBinding.Builtin };
        Expression Group(uint index) => Place("group_data",Expression.U32(index));
        main.Body.Statements.Add(new Statement.Store(Group(0),Expression.U32(11)));
        main.Body.Statements.Add(new Statement.Store(Group(1),Expression.U32(22)));
        main.Body.Statements.Add(new Statement.Store(Place("output",Expression.U32(0)),new Expression.Binary("+",Mark(3),
            Uniform(Address(Place("group_data",new Expression.Call("get_index",[],ShaderType.U32) { Binding = CallBinding.Function }))),ShaderType.U32)));
        main.Body.Statements.Add(new Statement.Store(Place("output",Expression.U32(1)),new Expression.Binary("+",Uniform(Address(Group(0))),Mark(4),ShaderType.U32)));
        main.Body.Statements.Add(new Statement.Store(Place("output",new Expression.Call("get_target",[],ShaderType.U32) { Binding = CallBinding.Function }),Uniform(Address(Group(1)))));
        var body = new Block(); var continuing = new Block(); var pointer = new ShaderType.Pointer(ShaderType.U32,AddressSpace.Workgroup);
        body.Statements.Add(new Statement.Declare("p",pointer,Address(Group(0)),false));
        continuing.Statements.Add(new Statement.Evaluate(Mark(6)));
        main.Body.Statements.Add(new Statement.Loop(body,continuing,new Expression.Binary("==",Uniform(new Expression.Reference("p",pointer)),Expression.U32(11),ShaderType.Bool)));
        main.Body.Statements.Add(new Statement.Store(Place("output",Expression.U32(3)),Uniform(Address(Group(0)))));
        return module;
    }

    internal static byte[] RawOrderedBinary() => SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(
        RawOrderedFixture(), version: SpirvCompilationTarget.Default.Version)).ToBytes();

    [Fact]
    public void RawOrderedExpansionKeepsCapturedOperandsAndContinuingScope()
    {
        var module = RawOrderedFixture(); ModuleValidator.Validate(module);
        string before = WgslWriter.Write(module);
        var prepared = SpirvPhysicalLayoutLowering.Prepare(module);
        ModuleValidator.Validate(prepared.Module);
        var graph = prepared.ControlFlow["main"].Graph;
        ControlFlowVerifier.Validate(graph,prepared.Module);
        var operations = graph.Blocks.SelectMany(b => b.Instructions).Select(i => i.Operation).ToArray();
        Assert.DoesNotContain(operations,o => o is ValueOperation.Builtin { Function:"workgroupUniformLoad" });
        Assert.Equal(10,operations.OfType<ValueOperation.Barrier>().Count());
        Assert.Single(operations.OfType<ValueOperation.Call>(),c => c.Function == "get_index");
        Assert.Single(operations.OfType<ValueOperation.Call>(),c => c.Function == "get_target");
        Assert.Equal(3,operations.OfType<ValueOperation.Call>().Count(c => c.Function == "mark"));
        string once = ControlFlowPrinter.Write(graph);
        SpirvSynchronizationLowering.Run(graph,prepared.Module);
        Assert.Equal(once,ControlFlowPrinter.Write(graph));
        Assert.Equal(before,WgslWriter.Write(module));
        var binary = RawOrderedBinary();
        var words = SpirvBinary.Parse(binary);
        Assert.Equal(SpirvCompilationTarget.Default.Version, words.Version);
        var entry = Assert.Single(words.Instructions, instruction => (Op)instruction.Opcode == Op.EntryPoint);
        SpirvBinary.ReadString(entry.Operands.AsSpan(2), out int nameWords);
        Assert.Equal(3, entry.Operands.Length - 2 - nameWords);
        ModuleValidator.ValidateNative(SpirvReader.Parse(binary));
    }

    [Theory]
    [InlineData("workgroupBarrier",2u,264u)] [InlineData("storageBarrier",2u,72u)]
    [InlineData("textureBarrier",2u,2056u)] [InlineData("subgroupBarrier",3u,264u)]
    public void BuiltinBarrierOperandsAreExplicit(string name,uint scope,uint semantics)
    {
        var module = new Module(); var function = new ShaderFunction("main") { Stage = ShaderStage.Compute }; module.Functions.Add(function);
        function.Body.Statements.Add(new Statement.Evaluate(new Expression.Call(name,[],new ShaderType.Void()) { Binding = CallBinding.Builtin }));
        var prepared = SpirvSynchronizationLowering.Run(module);
        var barrier = Assert.IsType<Statement.Barrier>(Assert.Single(prepared.Functions.Single().Body.Statements));
        Assert.Equal(new SpirvBarrierMemory(scope,semantics,scope),barrier.NativeMemory);
        Assert.IsType<Statement.Evaluate>(Assert.Single(module.Functions.Single().Body.Statements));
        ModuleValidator.Validate(prepared);
    }

    [Fact]
    public void UniformLoadHasExplicitBarrierReadBarrierSequence()
    {
        var input = WgslReader.Parse("var<workgroup> group_value:u32; @group(0) @binding(0) var<storage,read_write> output:u32; @compute @workgroup_size(1) fn main(){group_value=7u; output=workgroupUniformLoad(&group_value);}");
        var prepared = SpirvPhysicalLayoutLowering.Prepare(input);
        var graph = prepared.ControlFlow["main"].Graph;
        ControlFlowVerifier.Validate(graph, prepared.Module);
        var operations = graph.Blocks.SelectMany(b => b.Instructions).Select(i => i.Operation).ToArray();
        Assert.DoesNotContain(operations, o => o is ValueOperation.Builtin { Function: "workgroupUniformLoad" });
        var barriers = operations.Select((o,i) => (o,i)).Where(p => p.o is ValueOperation.Barrier).ToArray();
        Assert.Equal(2, barriers.Length);
        Assert.All(barriers, p => Assert.Equal(new SpirvBarrierMemory(2,264,2), Assert.IsType<ValueOperation.Barrier>(p.o).NativeMemory));
        var load = Assert.Single(operations.Select((o,i) => (o,i)), p => p.o is ValueOperation.Load);
        Assert.InRange(load.i, barriers[0].i+1, barriers[1].i-1);
    }

    [Theory]
    [InlineData(false,1u)] [InlineData(true,5u)]
    public void StorageAtomicDefaultsAreChosenBeforeSerialization(bool vulkan, uint scope)
    {
        var input = WgslReader.Parse("@group(0) @binding(0) var<storage,read_write> counter:atomic<u32>; @compute @workgroup_size(1) fn main(){atomicStore(&counter,7u);}");
        input.VulkanMemoryModel = vulkan;
        var prepared = SpirvPhysicalLayoutLowering.Prepare(input);
        var call = Assert.Single(prepared.ControlFlow["main"].Graph.Blocks.SelectMany(b => b.Instructions)
            .Select(i => i.Operation).OfType<ValueOperation.Builtin>());
        Assert.Equal(new SpirvAtomicMemory(scope,0), call.AtomicMemory);
        Assert.Null(Assert.IsType<Expression.Call>(Assert.Single(input.Functions.Single().Body.Statements.OfType<Statement.Evaluate>()).Value).AtomicMemory);
    }
}
