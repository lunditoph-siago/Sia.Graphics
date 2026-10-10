using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalPointerArgumentTests
{
    private const string Resources = "@group(0) @binding(0) var<storage,read> inputs:array<u32>;@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;";
    private const string Early = Resources + "fn edit(p:ptr<function,u32>,n:u32)->u32{if(n<3u){*p=*p+1u;return 2u;}*p=*p+4u;return *p;}@compute @workgroup_size(1) fn main(){var value=inputs[0];let result=edit(&value,inputs[0]);outputs[0]=result;outputs[1]=value;}";
    private const string Loop = Resources + "fn edit(p:ptr<function,u32>,n:u32)->u32{for(var i=0u;i<n;i++){*p=*p+i;}return *p;}@compute @workgroup_size(1) fn main(){var value=inputs[0];let result=edit(&value,3u);outputs[0]=result;outputs[1]=value;}";
    private const string Captured = "native captured storage address";

    private static CanonicalModule CapturedGraph()
    {
        var input = WgslReader.Parse(Resources + "override count=2u;var<private> index:u32;@compute @workgroup_size(count) fn main(){}");
        var main = input.Functions[0];
        var addressType = new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage, StorageAccess.ReadWrite);
        var helper = new ShaderFunction("edit") { ReturnType = ShaderType.U32 };
        helper.Arguments.Add(new("p", addressType)); helper.Arguments.Add(new("v", ShaderType.U32)); input.Functions.Add(helper);
        var helperGraph = new ControlFlowFunction(helper); var helperBlock = helperGraph.Block(); helperGraph.Entry = helperBlock.Id;
        SsaValue Value(ControlFlowFunction graph, ControlFlowBlock block, ShaderType type, ValueOperation operation) {
            var value = graph.Value(type); block.Instructions.Add(new(value, operation)); return value;
        }
        var p = Value(helperGraph, helperBlock, addressType, new ValueOperation.Symbol("p"));
        var v = Value(helperGraph, helperBlock, ShaderType.U32, new ValueOperation.Symbol("v"));
        var indexType = new ShaderType.Pointer(ShaderType.U32, AddressSpace.Private);
        var indexRoot = Value(helperGraph, helperBlock, indexType, new ValueOperation.Symbol("index"));
        var one = Value(helperGraph, helperBlock, ShaderType.U32, new ValueOperation.Literal(1u));
        helperBlock.Instructions.Add(new(null, new ValueOperation.Store(indexRoot, one)));
        helperBlock.Instructions.Add(new(null, new ValueOperation.Store(p, v))); helperBlock.Terminator = new ControlFlowTerminator.Return(one);
        var graph = new ControlFlowFunction(main); var block = graph.Block(); graph.Entry = block.Id;
        var capturedRoot = Value(graph, block, indexType, new ValueOperation.Symbol("index"));
        var capturedIndex = Value(graph, block, ShaderType.U32, new ValueOperation.Load(capturedRoot));
        var outputs = input.Globals.Single(g => g.Name == "outputs"); var inputs = input.Globals.Single(g => g.Name == "inputs");
        var outputRoot = Value(graph, block, new ShaderType.Pointer(outputs.Type, outputs.Space, outputs.Access), new ValueOperation.Symbol("outputs"));
        var capturedAddress = Value(graph, block, addressType, new ValueOperation.Access(outputRoot, capturedIndex));
        var zero = Value(graph, block, ShaderType.U32, new ValueOperation.Literal(0u));
        var inputRoot = Value(graph, block, new ShaderType.Pointer(inputs.Type, inputs.Space, inputs.Access), new ValueOperation.Symbol("inputs"));
        var inputAddress = Value(graph, block, new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage, StorageAccess.Read), new ValueOperation.Access(inputRoot, zero));
        var argument = Value(graph, block, ShaderType.U32, new ValueOperation.Load(inputAddress));
        var result = Value(graph, block, ShaderType.U32, new ValueOperation.Call(helper.Name, [capturedAddress, argument], ShaderType.U32, CalleeEffects: ShaderEffects.ReadMemory | ShaderEffects.WriteMemory));
        var second = Value(graph, block, ShaderType.U32, new ValueOperation.Literal(1u));
        var resultAddress = Value(graph, block, addressType, new ValueOperation.Access(outputRoot, second));
        block.Instructions.Add(new(null, new ValueOperation.Store(resultAddress, result))); block.Terminator = new ControlFlowTerminator.Return();
        var canonical = new CanonicalModule(input, new Dictionary<string, ControlFlowFunction> { [main.Name] = graph, [helper.Name] = helperGraph },
            new Dictionary<string, string>(), new HashSet<string> { main.Name, helper.Name });
        ModuleValidator.Validate(canonical, native: true); return canonical;
    }

    public static TheoryData<string, uint, uint, uint, bool> Cases {
        get {
            var cases = new TheoryData<string, uint, uint, uint, bool>();
            foreach (bool poison in new[] { false, true }) {
                cases.Add(CanonicalPointerTests.PointerScalar, 5, 5, 8, poison);
                // Native aliasing is injected into the distinct caller, whose
                // second output still encodes value*100 + the untouched other.
                cases.Add(CanonicalPointerTests.PointerAliased, 5, 7, 1606, poison);
                cases.Add(CanonicalPointerTests.PointerDistinct, 5, 6, 715, poison);
                cases.Add(CanonicalPointerTests.PointerPrivate, 5, 12, 12, poison);
                cases.Add(Early, 0, 2, 1, poison); cases.Add(Early, 5, 9, 9, poison);
                cases.Add(Loop, 5, 8, 8, poison); cases.Add(Captured, 7, 7, 1, poison);
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void OwnedPointerHelpersExpandWithoutReevaluatingAddressesOrReadingBorrowedBodies(string source, uint inputValue, uint first, uint second, bool poison)
    {
        var canonical = source == Captured ? CapturedGraph() : CanonicalShaderPipeline.Prepare(WgslReader.Parse(
            source == CanonicalPointerTests.PointerAliased ? CanonicalPointerTests.PointerDistinct : source));
        var input = canonical.Declarations;
        if (source == CanonicalPointerTests.PointerAliased) {
            // WGSL rejects overlapping writes; native SSA still represents them.
            foreach (var block in canonical.Functions["main"].Blocks)
                for (int i = 0; i < block.Instructions.Count; i++)
                    if (block.Instructions[i].Operation is ValueOperation.Call call)
                        block.Instructions[i] = block.Instructions[i] with { Operation = call with { Arguments = [call.Arguments[0], call.Arguments[0]] } };
        }
        Assert.Empty(canonical.DeferredFunctions);
        var snapshots = canonical.Functions.ToDictionary(p => p.Key, p => ControlFlowPrinter.Write(p.Value));
        var bodies = input.Functions.ToDictionary(f => f.Name, f => f.Body);
        var results = canonical.Functions["main"].Blocks.SelectMany(b => b.Instructions)
            .Where(i => i.Operation is ValueOperation.Call).Select(i => i.Result).Where(r => r is not null).ToArray();
        try {
            if (poison) foreach (var function in input.Functions) { function.Body = new(); function.Body.Statements.Add(new Statement.Return(Expression.U32(999))); }
            var borrowedBodies = input.Functions.ToDictionary(f => f.Name, f => f.Body);
            var lowered = ShaderTargetLowering.PrepareSpirv(canonical, source == Captured ? new Dictionary<string, double> { ["count"] = 3 } : null);
            ModuleValidator.Validate(lowered);
            Assert.All(canonical.Functions, p => Assert.Equal(snapshots[p.Key], ControlFlowPrinter.Write(p.Value)));
            Assert.All(input.Functions, f => Assert.Same(borrowedBodies[f.Name], f.Body));
            Assert.DoesNotContain(lowered.Declarations.Functions, f => f.Stage is null && f.Arguments.Any(a => a.Type is ShaderType.Pointer));
            Assert.DoesNotContain(lowered.Functions["main"].Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Call);
            foreach (var result in results) Assert.Contains(lowered.Functions["main"].Blocks.SelectMany(b => b.Parameters), v => v == result);
            var binary = SpirvWriter.Emit(SpirvEntryPointLowering.Run(lowered, true, true));
            var native = SpirvReader.Parse(binary.ToBytes()); ModuleValidator.Validate(native);
            Assert.Equal(new uint[] { first, second }, new CanonicalExecution(native, [inputValue]).Run().Output);
        }
        finally { foreach (var function in input.Functions) function.Body = bodies[function.Name]; }
    }

    [Fact]
    public void NativeTargetExpansionDoesNotRelaxWgslPointerLegality()
    {
        Assert.Throws<ShaderException>(() => WgslReader.Parse(CanonicalPointerTests.PointerAliased));
        Assert.Throws<ShaderException>(() => CanonicalShaderPipeline.Prepare(WgslReader.Parse("fn helper(p:ptr<storage,u32,read_write>){*p=1u;} @compute @workgroup_size(1) fn main(){}")));
    }

    [Fact]
    public void ReadableDeferredPointerCalleeImportsWithoutRebuildingTheOwnedCaller()
    {
        var input = WgslReader.Parse(CanonicalPointerTests.PointerScalar); var canonical = CanonicalShaderPipeline.Prepare(input);
        var deferred = new CanonicalModule(input, new Dictionary<string, ControlFlowFunction> { ["main"] = canonical.Functions["main"] },
            new Dictionary<string, string> { ["edit"] = "test helper migration" }, canonical.EntryFunctions);
        ModuleValidator.Validate(deferred);
        var expanded = CanonicalHelperInliner.RunPointers(deferred);
        Assert.DoesNotContain(expanded.Declarations.Functions, f => f.Name == "edit"); Assert.Empty(expanded.DeferredFunctions);
        var prepared = SpirvEntryPointLowering.Run(ShaderTargetLowering.PrepareSpirv(deferred, null), true, true);
        Assert.Equal(new uint[] { 5, 8 }, new CanonicalExecution(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()), [5]).Run().Output);
        Assert.Equal("test helper migration", deferred.DeferredFunctions["edit"]);
    }

    [Fact]
    public void UncalledFunctionPointerHelperRetainsItsExplicitDeferral()
    {
        var input = WgslReader.Parse("fn unused(p:ptr<function,u32>){*p=9u;}@compute @workgroup_size(1) fn main(){}");
        var canonical = CanonicalShaderPipeline.Prepare(input);
        Assert.Equal("outside shader entry call graph", canonical.DeferredFunctions["unused"]);
        var output = CanonicalHelperInliner.RunPointers(canonical);
        Assert.Same(canonical, output); Assert.Contains(output.Declarations.Functions, f => f.Name == "unused");
    }

    [Fact]
    public void RejectedCalleeLocalLifetimeLeavesAllBorrowedGraphsUnchanged()
    {
        var input = WgslReader.Parse("@compute @workgroup_size(1) fn main(){}"); var main = input.Functions[0];
        var pointer = new ShaderType.Pointer(ShaderType.U32, AddressSpace.Function);
        var helper = new ShaderFunction("bad") { ReturnType = pointer }; input.Functions.Add(helper);
        var callee = new ControlFlowFunction(helper); var calleeBlock = callee.Block(); callee.Entry = calleeBlock.Id;
        var local = callee.Value(pointer); calleeBlock.Instructions.Add(new(local, new ValueOperation.Local("expired", false)));
        calleeBlock.Terminator = new ControlFlowTerminator.Return(local);
        var caller = new ControlFlowFunction(main); var block = caller.Block(); caller.Entry = block.Id;
        block.Instructions.Add(new(caller.Value(pointer), new ValueOperation.Call(helper.Name, [], pointer)));
        block.Terminator = new ControlFlowTerminator.Return();
        var canonical = new CanonicalModule(input, new Dictionary<string, ControlFlowFunction> { [main.Name] = caller, [helper.Name] = callee },
            new Dictionary<string, string>(), new HashSet<string> { main.Name, helper.Name });
        ModuleValidator.Validate(canonical, native: true);
        var before = canonical.Functions.ToDictionary(p => p.Key, p => ControlFlowPrinter.Write(p.Value));
        Assert.Contains("callee-local returned address", Assert.Throws<ShaderException>(() => CanonicalHelperInliner.RunPointers(canonical)).Message);
        Assert.All(canonical.Functions, p => Assert.Equal(before[p.Key], ControlFlowPrinter.Write(p.Value)));
        Assert.All(input.Functions, f => Assert.Empty(f.Body.Statements));
    }

    [Fact]
    public void InlinedNativeMemoryAndDiagnosticOriginsUseTheCapturedCallerAddress()
    {
        var input = WgslReader.Parse(CanonicalPointerTests.PointerScalar); var canonical = CanonicalShaderPipeline.Prepare(input);
        var caller = canonical.Functions["main"];
        var call = Assert.Single(caller.Blocks.SelectMany(b => b.Instructions).Select(i => i.Operation).OfType<ValueOperation.Call>());
        var helper = canonical.Functions["edit"];
        var filter = new DiagnosticFilter(DiagnosticSeverity.Warning, "derivative_uniformity");
        foreach (var block in helper.Blocks)
            for (int i = 0; i < block.Instructions.Count; i++)
                if (block.Instructions[i].Operation is ValueOperation.Store store)
                    block.Instructions[i] = block.Instructions[i] with { Operation = store with { MemoryAccess = new(1) }, Span = new(31, 3), DiagnosticFilters = [filter] };
        string before = ControlFlowPrinter.Write(helper);
        var effects = ShaderEffectAnalysis.Compute(canonical)["edit"];
        foreach (var block in caller.Blocks)
            for (int i = 0; i < block.Instructions.Count; i++)
                if (block.Instructions[i].Operation is ValueOperation.Call actualCall)
                    block.Instructions[i] = block.Instructions[i] with { Operation = actualCall with { CalleeEffects = actualCall.CalleeEffects | effects } };
        var output = CanonicalHelperInliner.RunPointers(canonical);
        var actual = Assert.Single(output.Functions["main"].Blocks.SelectMany(b => b.Instructions), i => i.Span == new SourceSpan(31, 3));
        var memory = Assert.IsType<ValueOperation.Store>(actual.Operation);
        Assert.Equal(call.Arguments[0], memory.Pointer); Assert.Equal(1u, memory.MemoryAccess!.Flags);
        Assert.Contains(filter, actual.DiagnosticFilters); Assert.Equal(before, ControlFlowPrinter.Write(helper));
        ModuleValidator.Validate(output);
    }
}
