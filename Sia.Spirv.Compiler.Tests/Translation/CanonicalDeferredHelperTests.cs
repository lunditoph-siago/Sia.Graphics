using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalDeferredHelperTests
{
    internal const string Source = "@group(0) @binding(0) var<storage,read> inputs:array<u32>;@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;"
        + "fn leaf(n:u32)->u32{return n+3u;}fn edit(p:ptr<function,u32>,n:u32)->u32{*p+=leaf(n);return *p;}"
        + "@compute @workgroup_size(1) fn main(){var value=inputs[0];let result=edit(&value,inputs[0]);outputs[0]=result;outputs[1]=value;}";
    internal const string Unused = "fn unused(p:ptr<function,u32>){*p=9u;}@compute @workgroup_size(1) fn main(){var x=1u;for(var i=0u;i<2u;i++){x+=i;}}";
    internal const string HandleAlias = "@group(0) @binding(0) var image:texture_2d<u32>;@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;"
        + "fn read(p:ptr<function,u32>){let handle=image;*p=textureLoad(handle,vec2i(0),0).x;}fn middle(p:ptr<function,u32>){read(p);}"
        + "fn untouched(n:u32)->u32{return n+1u;}@compute @workgroup_size(1) fn main(){var value=0u;middle(&value);outputs[0]=value;outputs[1]=untouched(2u);}";

    [Theory] [InlineData(false)] [InlineData(true)]
    public void ExplicitDeferredCalleesAndCallersUseOwnedEffectsWithoutRebuildingOtherGraphs(bool deferredCaller)
    {
        var input = WgslReader.Parse(Source); var prepared = CanonicalShaderPipeline.Prepare(input);
        var graphs = prepared.Functions.Where(p => p.Key != "edit" && (!deferredCaller || p.Key != "main"))
            .ToDictionary(p => p.Key, p => p.Value);
        var deferred = new Dictionary<string, string> { ["edit"] = "explicit migration boundary" };
        if (deferredCaller) deferred.Add("main", "explicit caller boundary");
        var canonical = new CanonicalModule(input, graphs, deferred, prepared.EntryFunctions);
        var before = graphs.ToDictionary(p => p.Key, p => ControlFlowPrinter.Write(p.Value));
        var bodies = input.Functions.ToDictionary(f => f.Name, f => f.Body);
        try {
            foreach (var function in input.Functions.Where(f => graphs.ContainsKey(f.Name))) {
                function.Body = new(); function.Body.Statements.Add(new Statement.Barrier(true, true));
                function.Body.Statements.Add(new Statement.Declare("obsolete", new ShaderType.Array(ShaderType.U32, null) { OverrideLength = "missing_length" }, null));
            }
            ModuleValidator.Validate(canonical);
            var output = CanonicalHelperInliner.RunPointers(canonical);
            Assert.DoesNotContain(output.Declarations.Functions, f => f.Name == "edit");
            Assert.Empty(output.DeferredFunctions);
            Assert.Same(graphs["leaf"], output.Functions["leaf"]);
            var main = output.Functions["main"];
            var leaf = Assert.Single(main.Blocks.SelectMany(b => b.Instructions).Select(i => i.Operation).OfType<ValueOperation.Call>());
            Assert.Equal("leaf", leaf.Function); Assert.Equal(ShaderEffects.None, leaf.CalleeEffects);
            if (!deferredCaller) {
                var result = Assert.Single(graphs["main"].Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Call).Result!.Value;
                Assert.Contains(main.Blocks.SelectMany(b => b.Parameters), p => p == result);
            }
            Assert.All(graphs, p => Assert.Equal(before[p.Key], ControlFlowPrinter.Write(p.Value)));
            ModuleValidator.Validate(output);
            var target = SpirvEntryPointLowering.Run(ShaderTargetLowering.PrepareSpirv(output, null), true, true);
            Assert.DoesNotContain(target.PhysicalLayout.EntryWrappers["main"].Function.Graph.Blocks.SelectMany(b => b.Instructions),
                i => i.Operation is ValueOperation.Call call && (call.CalleeEffects & ShaderEffects.Synchronization) != 0);
            var native = SpirvReader.Parse(SpirvWriter.Emit(target).ToBytes());
            Assert.Equal(new uint[] { 13, 13 }, new CanonicalExecution(native, [5]).Run().Output);
        } finally { foreach (var function in input.Functions) function.Body = bodies[function.Name]; }
    }

    [Fact]
    public void UncalledFunctionPointerHelperDoesNotRebuildTheEntryGraphAtTheTargetBoundary()
    {
        var canonical = CanonicalShaderPipeline.Prepare(WgslReader.Parse(Unused));
        var graph = canonical.Functions["main"]; string before = ControlFlowPrinter.Write(graph);
        var output = ShaderTargetLowering.PrepareSpirv(canonical, null);
        Assert.Equal(before, ControlFlowPrinter.Write(output.Functions["main"]));
        Assert.Empty(output.DeferredFunctions);
        Assert.Equal(ControlFlowPrinter.Write(canonical.Functions["unused"]), ControlFlowPrinter.Write(output.Functions["unused"]));
        Assert.Equal(before, ControlFlowPrinter.Write(graph));
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void HandleAliasesExpandOnTheirOwnedPointerCallClosure(bool nested, bool qualified)
    {
        var input = WgslReader.Parse(nested ? HandleAlias : HandleAlias.Replace("middle(&value)", "read(&value)"));
        var read = input.Functions.Single(f => f.Name == "read");
        if (qualified) {
            int index = read.Body.Statements.FindIndex(s => s is Statement.Store);
            read.Body.Statements[index] = ((Statement.Store)read.Body.Statements[index]) with { MemoryAccess = new(1), Span = new(31, 3) };
        }
        var canonical = CanonicalShaderPipeline.Prepare(input); Assert.Empty(canonical.DeferredFunctions);
        var before = canonical.Functions.ToDictionary(p => p.Key, p => ControlFlowPrinter.Write(p.Value));
        var bodies = input.Functions.ToDictionary(f => f.Name, f => f.Body);
        try {
            foreach (var function in input.Functions.Where(f => canonical.Functions.ContainsKey(f.Name))) function.Body = new();
            var output = CanonicalHelperInliner.RunPointers(canonical);
            Assert.Same(canonical.Functions["untouched"], output.Functions["untouched"]);
            Assert.Empty(output.DeferredFunctions);
            Assert.DoesNotContain(output.Declarations.Functions, f => f.Name == "read");
            if (nested) Assert.DoesNotContain(output.Declarations.Functions, f => f.Name == "middle");
            Assert.All(canonical.Functions, p => Assert.Equal(before[p.Key], ControlFlowPrinter.Write(p.Value)));
            if (qualified) {
                var instruction = Assert.Single(output.Functions["main"].Blocks.SelectMany(b => b.Instructions),
                    i => i.Operation is ValueOperation.Store && i.Span == new SourceSpan(31, 3));
                Assert.Equal(1u, ((ValueOperation.Store)instruction.Operation).MemoryAccess!.Flags);
            }
            ModuleValidator.Validate(output, native: true);
            var binary = SpirvWriter.Emit(SpirvEntryPointLowering.Run(ShaderTargetLowering.PrepareSpirv(output, null), true, true));
            ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
        } finally { foreach (var function in input.Functions) function.Body = bodies[function.Name]; }
    }

    internal static CanonicalModule QualifiedSlot(AddressSpace space = AddressSpace.Storage)
    {
        var input = WgslReader.Parse("@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;@compute @workgroup_size(1) fn main(){}");
        var pointer = new ShaderType.Pointer(ShaderType.U32, space);
        var slotType = new ShaderType.Pointer(pointer, AddressSpace.Function);
        var helper = new ShaderFunction("assign"); helper.Arguments.Add(new("slot", slotType)); helper.Arguments.Add(new("address", pointer));
        var slot = new Expression.Reference("slot", slotType); var address = new Expression.Reference("address", pointer);
        helper.Body.Statements.Add(new Statement.Store(new Expression.Unary("*", slot, slotType), address) { MemoryAccess = new(1), Span = new(31, 3) });
        input.Functions.Add(helper);
        var graph = new ControlFlowFunction(input.Functions[0]); var block = graph.Block(); graph.Entry = block.Id;
        SsaValue Value(ShaderType type, ValueOperation operation) { var value = graph.Value(type); block.Instructions.Add(new(value, operation)); return value; }
        var outputs = input.Globals.Single(); var root = Value(new ShaderType.Pointer(outputs.Type, AddressSpace.Storage), new ValueOperation.Symbol(outputs.Name));
        var index = Value(ShaderType.U32, new ValueOperation.Literal(0u));
        var target = Value(new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage), new ValueOperation.Access(root, index));
        SsaValue local;
        if (space == AddressSpace.Storage) local = target;
        else if (space == AddressSpace.Workgroup) {
            input.Globals.Add(new("shared", ShaderType.U32, AddressSpace.Workgroup)); input.WorkgroupInitializationRequired = false;
            local = Value(pointer, new ValueOperation.Symbol("shared"));
        }
        else local = Value(pointer, new ValueOperation.Local("value", false));
        var value = Value(ShaderType.U32, new ValueOperation.Literal(17u));
        block.Instructions.Add(new(null, new ValueOperation.Store(local, value)));
        var storage = Value(slotType, new ValueOperation.Local("slot", false));
        block.Instructions.Add(new(null, new ValueOperation.Call(helper.Name, [storage, local], new ShaderType.Void(), CalleeEffects: ShaderEffects.Volatile)));
        var captured = Value(pointer, new ValueOperation.Load(storage)); var result = Value(ShaderType.U32, new ValueOperation.Load(captured));
        block.Instructions.Add(new(null, new ValueOperation.Store(target, result))); block.Terminator = new ControlFlowTerminator.Return();
        return new(input, new Dictionary<string, ControlFlowFunction> { [graph.Signature.Name] = graph },
            new Dictionary<string, string> { [helper.Name] = "native qualified pointer-slot migration" }, new HashSet<string> { graph.Signature.Name, helper.Name });
    }

    [Theory] [InlineData(AddressSpace.Storage, 4441u)] [InlineData(AddressSpace.Workgroup, 4442u)]
    public void NativeDeferredPointerSlotStoresRetainCapturedAddressesAndMemoryOrigins(AddressSpace space, uint capability)
    {
        var input = QualifiedSlot(space); ModuleValidator.Validate(input, native: true);
        var main = input.Functions["main"]; string before = ControlFlowPrinter.Write(main);
        var call = Assert.Single(main.Blocks.SelectMany(b => b.Instructions).Select(i => i.Operation).OfType<ValueOperation.Call>());
        var output = CanonicalHelperInliner.RunPointers(input); Assert.Empty(output.DeferredFunctions);
        Assert.DoesNotContain(output.Declarations.Functions, f => f.Name == "assign");
        var store = Assert.Single(output.Functions["main"].Blocks.SelectMany(b => b.Instructions), i => i.Span == new SourceSpan(31, 3));
        var memory = Assert.IsType<ValueOperation.Store>(store.Operation);
        Assert.Equal(call.Arguments[0], memory.Pointer); Assert.Equal(call.Arguments[1], memory.Value); Assert.Equal(1u, memory.MemoryAccess!.Flags);
        Assert.Equal(before, ControlFlowPrinter.Write(main)); ModuleValidator.Validate(output, native: true);
        var prepared = SpirvEntryPointLowering.Run(ShaderTargetLowering.PrepareSpirv(output, null), true, true);
        Assert.Contains(capability, prepared.PhysicalLayout.EntryAbi!.Capabilities);
        var native = SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes());
        Assert.Equal(17u, new CanonicalExecution(native, []).Run().Output[0]);
    }

    [Fact]
    public void FunctionAddressLoadedFromNativePointerSlotIsRejectedBeforeEmission()
    {
        var input = QualifiedSlot(AddressSpace.Function);
        Assert.Contains("Pointer-slot values require storage or workgroup addresses", Assert.Throws<ShaderException>(() =>
            SpirvEntryPointLowering.Run(ShaderTargetLowering.PrepareSpirv(input, null), true, true)).Message);
    }

    internal static Sia.Spirv.Compiler.Translation.Spirv.SpirvBinary QualifiedSlotOutput(AddressSpace space)
        => SpirvWriter.Emit(SpirvEntryPointLowering.Run(ShaderTargetLowering.PrepareSpirv(QualifiedSlot(space), null), true, true));

    private static IEnumerable<Statement> Statements(Block body)
    {
        foreach (var statement in body.Statements) {
            yield return statement;
            foreach (var child in statement switch {
                Statement.Nested n => new[] { n.Body }, Statement.If i => new[] { i.Accept, i.Reject },
                Statement.Loop l => new[] { l.Body, l.Continuing }, Statement.Switch s => s.Cases.Select(c => c.Body), _ => [] })
                foreach (var nested in Statements(child)) yield return nested;
        }
    }
}
