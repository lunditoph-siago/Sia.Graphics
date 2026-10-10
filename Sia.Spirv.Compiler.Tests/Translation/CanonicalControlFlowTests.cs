using System.Globalization;
using Sia.Spirv.Compiler.Tests;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalControlFlowTests
{
    private const string Resources = "@group(0) @binding(0) var<storage,read> inputs:array<u32>; @group(0) @binding(1) var<storage,read_write> outputs:array<u32>;";
    internal const string SwapLoop = Resources + "@compute @workgroup_size(1) fn main(){var a=1u;var b=2u;var i=0u;loop{if i>=inputs[0]{break;}let old=a;a=b;b=old;i++;}outputs[0]=a*10u+b;}";
    internal const string EarlyExit = Resources + "@compute @workgroup_size(1) fn main(){let n=inputs[0];switch n{case 0u:{outputs[0]=10u;return;}case 1u:{outputs[0]=20u;}default:{var sum=0u;var i=0u;loop{if i>=n{break;}sum+=i;i++;}outputs[0]=sum;}}outputs[1]=99u;}";
    internal const string CapturedIndex = Resources + "@compute @workgroup_size(1) fn main(){var index=inputs[0];let captured=inputs[index];index=0u;outputs[0]=captured+inputs[index];}";
    internal const string SignedShift = Resources + "@compute @workgroup_size(1) fn main(){let x=bitcast<i32>(inputs[0]);outputs[0]=bitcast<u32>(x>>1u);}";
    internal const string ShortCircuitOr = Resources + "@compute @workgroup_size(1) fn main(){if inputs[0]==0u || inputs[99]>0u {outputs[0]=7u;}else{outputs[0]=9u;}}";
    internal const string ShortCircuitAnd = Resources + "@compute @workgroup_size(1) fn main(){if inputs[0]!=0u && inputs[99]>0u {outputs[0]=7u;}else{outputs[0]=9u;}}";
    internal const string ScalarHelpers = Resources + "fn first(n:u32)->u32 {if n>0u{return n+1u;}return 7u;}fn second()->u32 {outputs[1]+=1u;return first(inputs[0]);}@compute @workgroup_size(1) fn main(){outputs[0]=second()+second();}";
    internal const string NestedLoops = Resources + "@compute @workgroup_size(1) fn main(){var sum=0u;var i=0u;loop{if i>=inputs[0]{break;}var j=0u;loop{if j>=3u{break;}j++;switch j{case 2u:{continue;}default:{sum+=i*10u+j;}}}i++;continuing{if i>=2u{outputs[1]+=1u;}break if i>=4u;}}outputs[0]=sum;}";
    internal const string ContinuingBreakIf = Resources + "@compute @workgroup_size(1) fn main(){var stop=false;var count=0u;loop{count++;continuing{let previous=stop;stop=count>=2u;break if previous;}}outputs[0]=count;}";
    internal const string BodyContinuingScope = Resources + "@compute @workgroup_size(1) fn main(){var count=0u;loop{let previous=count;count++;continuing{let limit=2u;break if previous>=limit;}}outputs[0]=count;}";

    private static ControlFlowFunction Graph(Module module)
    {
        Assert.True(StructuredControlFlowReader.TryRead(Assert.Single(module.Functions), module, out var graph, out var deferred), deferred);
        ControlFlowAnalysis.RemoveUnreachable(graph!); ControlFlowVerifier.Validate(graph!, module);
        return graph!;
    }
    [Theory]
    [InlineData(0u, 12u)] [InlineData(1u, 21u)] [InlineData(2u, 12u)] [InlineData(5u, 21u)]
    public void LoopCarriedValuesKeepParallelMergeSemanticsThroughBothTargets(uint count, uint expected)
    {
        var module = WgslReader.Parse(SwapLoop); var graph = Graph(module);
        LocalValuePromotion.Run(graph); ControlFlowVerifier.Validate(graph, module);
        Assert.Contains(graph.Blocks, b => b.Parameters.Count >= 2);
        Assert.DoesNotContain(graph.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Local
            || i.Operation is ValueOperation.Load l && l.Pointer.Type is ShaderType.Pointer { Space: AddressSpace.Function });
        CheckRoutes(module, [count], [expected, 0]);
    }
    [Theory]
    [InlineData("||", 7u)] [InlineData("&&", 9u)]
    public void ShortCircuitDoesNotReadTheUnselectedInvalidAddress(string op, uint expected)
    {
        var module = WgslReader.Parse(op == "||" ? ShortCircuitOr : ShortCircuitAnd);
        foreach (var candidate in Routes(module)) {
            var result = new CanonicalExecution(candidate, [0u]).Run();
            Assert.Equal(new uint[] { expected, 0 }, result.Output); Assert.Equal(new[] { 0 }, result.Reads);
        }
    }
    [Theory]
    [InlineData(0u, 10u, 0u)] [InlineData(1u, 20u, 99u)] [InlineData(4u, 6u, 99u)]
    public void SwitchBreakLoopAndEarlyReturnKeepTheirDistinctExits(uint selector, uint first, uint second)
    {
        var module = WgslReader.Parse(EarlyExit);
        CheckRoutes(module, [selector], [first, second]);
    }
    [Fact]
    public void CapturedIndexAndSignedWidthSurviveTargetAdaptation()
    {
        var indexed = WgslReader.Parse(CapturedIndex);
        foreach (var candidate in Routes(indexed)) {
            var result = new CanonicalExecution(candidate, [1u, 5u]).Run();
            Assert.Equal(new uint[] { 6, 0 }, result.Output); Assert.Equal(new[] { 0, 1, 0 }, result.Reads);
        }
        var signed = WgslReader.Parse(SignedShift);
        CheckRoutes(signed, [0xfffffff8u], [0xfffffffcu, 0]);
    }
    [Theory]
    [InlineData(0u, 0u, 0u)] [InlineData(1u, 4u, 0u)]
    [InlineData(2u, 28u, 1u)] [InlineData(10u, 136u, 3u)]
    public void NestedLoopsSwitchContinueAndContinuingBreakIfPreserveEffects(uint input, uint first, uint second)
        => CheckRoutes(WgslReader.Parse(NestedLoops), [input], [first, second]);

    [Fact]
    public void ContinuingBreakIfUsesTheValueBeforeLoopCarriedAssignments()
        => CheckRoutes(WgslReader.Parse(ContinuingBreakIf), [0], [3, 0]);

    [Fact]
    public void LoopBodyAndContinuingDeclarationsRemainInScopeForBreakIf()
        => CheckRoutes(WgslReader.Parse(BodyContinuingScope), [0], [3, 0]);

    [Theory]
    [InlineData("SwapLoop", 1, 0)] [InlineData("EarlyExit", 1, 1)]
    [InlineData("ShortCircuitOr", 0, 0)] [InlineData("ScalarHelpers", 0, 0)]
    [InlineData("NestedLoops", 2, 1)]
    public void CanonicalTargetsRebuildSourceControlWithoutExtraDispatcherLoopsOrSwitches(string fixture, int loops, int switches)
    {
        string source = fixture switch { "SwapLoop" => SwapLoop, "EarlyExit" => EarlyExit, "ShortCircuitOr" => ShortCircuitOr, "ScalarHelpers" => ScalarHelpers, _ => NestedLoops };
        var module = WgslReader.Parse(source);
        foreach (var input in new[] { module, SpirvReader.Parse(SpirvWriter.Write(module)) }) {
            var deferrals = new List<CanonicalDeferral>(); var prepared = CanonicalShaderPipeline.Run(input, deferrals: deferrals);
            Assert.Empty(deferrals);
            IEnumerable<Statement> Walk(Block block) {
                foreach (var statement in block.Statements) {
                    yield return statement;
                    IEnumerable<Block> children = statement switch {
                        Statement.Nested n => [n.Body], Statement.If i => [i.Accept, i.Reject], Statement.Loop l => [l.Body, l.Continuing],
                        Statement.Switch s => s.Cases.Select(c => c.Body), _ => []
                    };
                    foreach (var child in children) foreach (var nested in Walk(child)) yield return nested;
                }
            }
            var statements = prepared.Functions.SelectMany(f => Walk(f.Body)).ToArray();
            Assert.Equal(loops, statements.Count(s => s is Statement.Loop)); Assert.Equal(switches, statements.Count(s => s is Statement.Switch));
        }
    }

    [Theory]
    [InlineData("selection")] [InlineData("loop-merge")] [InlineData("continuing")] [InlineData("backedge")]
    public void VerifierRejectsInvalidStructuredBoundaries(string defect)
    {
        var module = WgslReader.Parse(SwapLoop); var graph = Graph(module);
        var (header, loop) = Assert.Single(graph.Loops);
        switch (defect) {
            case "selection": graph.SelectionMerges[graph.SelectionMerges.Keys.First()] = graph.Entry; break;
            case "loop-merge": graph.Loops[header] = loop with { Merge = header }; break;
            case "continuing": graph.Loops[header] = loop with { Continuing = loop.Merge }; break;
            default: graph.Loops[header] = loop with { Continuing = null }; break;
        }
        Assert.Throws<ShaderException>(() => ControlFlowVerifier.Validate(graph, module));
    }

    [Fact]
    public void MissingStructureIsDiagnosedInsteadOfFallingBackToAStateMachine()
    {
        var module = WgslReader.Parse(ShortCircuitOr); var graph = Graph(module); graph.SelectionMerges.Clear();
        ControlFlowVerifier.Validate(graph, module);
        Assert.Contains("missing selection merge", Assert.Throws<ShaderException>(() => StructuredControlFlowLowering.Run(graph, module)).Message);
    }

    [Fact]
    public void BreakIfCapturesAHeaderPhiBeforeBackedgeCopiesOverwriteIt()
    {
        var module = WgslReader.Parse(Resources + "@compute @workgroup_size(1) fn main(){}");
        var graph = new ControlFlowFunction(module.Functions[0]);
        var entry = graph.Block(); var header = graph.Block(); var tail = graph.Block(); var exit = graph.Block(); graph.Entry = entry.Id;
        var zero = graph.Value(ShaderType.U32); var initial = graph.Value(ShaderType.Bool);
        entry.Instructions.Add(new(zero, new ValueOperation.Literal(0u))); entry.Instructions.Add(new(initial, new ValueOperation.Literal(false)));
        var stop = graph.Value(ShaderType.Bool); var count = graph.Value(ShaderType.U32); header.Parameters.AddRange([stop, count]);
        entry.Terminator = new ControlFlowTerminator.Branch(new(header.Id, [initial, zero]));
        var one = graph.Value(ShaderType.U32); var next = graph.Value(ShaderType.U32);
        var output = graph.Value(new ShaderType.Pointer(module.Globals[1].Type, AddressSpace.Storage));
        var address = graph.Value(new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage));
        header.Instructions.Add(new(one, new ValueOperation.Literal(1u))); header.Instructions.Add(new(next, new ValueOperation.Binary("+", count, one)));
        header.Instructions.Add(new(output, new ValueOperation.Symbol("outputs"))); header.Instructions.Add(new(address, new ValueOperation.Access(output, zero)));
        header.Instructions.Add(new(null, new ValueOperation.Store(address, next))); header.Terminator = new ControlFlowTerminator.Branch(new(tail.Id));
        var changed = graph.Value(ShaderType.Bool); tail.Instructions.Add(new(changed, new ValueOperation.Literal(true)));
        tail.Terminator = new ControlFlowTerminator.Conditional(stop, new(exit.Id), new(header.Id, [changed, next])); exit.Terminator = new ControlFlowTerminator.Return();
        graph.Loops.Add(header.Id, new(tail.Id, exit.Id)); ControlFlowVerifier.Validate(graph, module);
        var lowered = StructuredControlFlowLowering.Run(graph, module); module.Functions.Clear(); module.Functions.Add(lowered);
        CheckRoutes(module, [0], [2, 0]);
    }
    private static IEnumerable<Module> Routes(Module module)
    {
        yield return module;
        yield return CanonicalShaderPipeline.Run(module);
        yield return WgslReader.Parse(WgslWriter.Write(module));
        var native = SpirvReader.Parse(SpirvWriter.Write(module));
        yield return native;
        yield return CanonicalShaderPipeline.Run(native);
        yield return WgslReader.Parse(WgslWriter.Write(native));
    }
    private static void CheckRoutes(Module module, uint[] input, uint[] expected)
    {
        foreach (var candidate in Routes(module)) {
            ModuleValidator.Validate(candidate);
            Assert.Equal(expected, new CanonicalExecution(candidate, input).Run().Output);
        }
    }
    [Fact]
    public void DirectCilIntegerControlFlowActuallyEntersTheCanonicalSlice()
    {
        var kernel = SpirvTestAssembly.GetKernel(typeof(ControlFlowShaders), nameof(ControlFlowShaders.IntegerControlFlow));
        var module = new SpirvCompiler().CompileModule(new SpirvModuleCompilationRequest(File.ReadAllBytes(SpirvTestAssembly.Path),
            kernel.MetadataToken, File.ReadAllBytes(typeof(SpirvKernelAttribute).Assembly.Location)));
        var graph = Graph(module); LocalValuePromotion.Run(graph); ControlFlowVerifier.Validate(graph, module);
        Assert.Contains(graph.Blocks, b => b.Parameters.Count != 0);
        var prepared = CanonicalShaderPipeline.Run(module);
        Assert.NotSame(module.Functions[0], prepared.Functions[0]);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module)));
    }
    [Fact]
    public void IrDumpIsDeterministicAndIndependentOfNumericCulture()
    {
        string Dump() { var module = WgslReader.Parse(SwapLoop); var graph = Graph(module); LocalValuePromotion.Run(graph); return ControlFlowPrinter.Write(graph); }
        string original = Dump(); var culture = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-EG"); Assert.Equal(original, Dump()); }
        finally { CultureInfo.CurrentCulture = culture; }
        Assert.Contains("effects=ReadMemory", original); Assert.Contains("effects=WriteMemory", original);
        Assert.DoesNotContain("local ", original);
    }
    [Fact]
    public void PassTracesIncludeQueryAllocationPerCompilation()
    {
        var module = WgslReader.Parse(SwapLoop); var traces = new List<CanonicalPassTrace>(); var deferrals = new List<CanonicalDeferral>();
        _ = CanonicalShaderPipeline.Run(module, traces, deferrals);
        Assert.Empty(deferrals); Assert.Equal(new[] { "remove-unreachable", "local-value-promotion", "canonical-call-effects" }, traces.Select(t => t.Pass));
        Assert.Contains("local ", traces[1].Before); Assert.DoesNotContain("local ", traces[1].After);
        Assert.Contains("value dominance", traces[1].InvalidatedAnalyses);
        var advanced = WgslReader.Parse("enable wgpu_ray_query;fn helper(p:ptr<function,ray_query>){var query:ray_query;} @compute @workgroup_size(1) fn main(){var query:ray_query;helper(&query);}");
        traces.Clear(); deferrals.Clear(); var prepared = CanonicalShaderPipeline.Run(advanced, traces, deferrals);
        Assert.NotSame(advanced.Functions[1], prepared.Functions[1]); Assert.Empty(deferrals);
        Assert.Contains(traces, t => t.Function == "main" && t.Pass == "local-value-promotion");
        Assert.Contains(traces, t => t.Function == "helper" && t.Pass == "local-value-promotion");
    }
    [Theory]
    [InlineData(0u, 14u)] [InlineData(2u, 6u)]
    public void ScalarHelpersKeepReturnValuesAndOrderedEffects(uint input, uint expected)
    {
        var module = WgslReader.Parse(ScalarHelpers);
        var deferrals = new List<CanonicalDeferral>();
        var prepared = CanonicalShaderPipeline.Run(module, deferrals: deferrals); Assert.Empty(deferrals);
        var helper = prepared.Functions.Single(f => f.Name == "first");
        Assert.Equal(ShaderType.U32, Assert.IsType<Statement.Return>(helper.Body.Statements.Last()).Value!.Type);
        var entry = module.Functions.Single(f => f.Stage == ShaderStage.Compute);
        Assert.True(StructuredControlFlowReader.TryRead(entry, module, out var graph, out var deferred), deferred);
        ControlFlowAnalysis.RemoveUnreachable(graph!); ControlFlowVerifier.Validate(graph!, module);
        var calls = graph!.Blocks.SelectMany(b => b.Instructions).Where(i => i.Operation is ValueOperation.Call).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, c => Assert.Equal(ShaderEffects.ReadMemory | ShaderEffects.WriteMemory | ShaderEffects.UnknownCall, c.Effects));
        CheckRoutes(module, [input], [expected, 2]);
    }
    [Theory]
    [InlineData(nameof(SwapLoop))] [InlineData(nameof(EarlyExit))] [InlineData(nameof(CapturedIndex))]
    [InlineData(nameof(SignedShift))] [InlineData(nameof(ShortCircuitOr))] [InlineData(nameof(ShortCircuitAnd))]
    [InlineData(nameof(ScalarHelpers))]
    public void NativeSpirvEntryWrappersAndScalarHelpersAlsoEnterCanonicalIr(string fixture)
    {
        string source = fixture switch {
            nameof(SwapLoop) => SwapLoop, nameof(EarlyExit) => EarlyExit, nameof(CapturedIndex) => CapturedIndex,
            nameof(SignedShift) => SignedShift, nameof(ShortCircuitOr) => ShortCircuitOr, nameof(ShortCircuitAnd) => ShortCircuitAnd,
            _ => ScalarHelpers
        };
        var native = SpirvReader.Parse(SpirvWriter.Write(WgslReader.Parse(source)));
        var deferrals = new List<CanonicalDeferral>(); var traces = new List<CanonicalPassTrace>();
        var prepared = CanonicalShaderPipeline.Run(native, traces, deferrals);
        Assert.Empty(deferrals); Assert.Equal(native.Functions.Count * 3, traces.Count);
        for (int i = 0; i < native.Functions.Count; i++) Assert.NotSame(native.Functions[i], prepared.Functions[i]);
    }
    [Theory]
    [InlineData("missing")] [InlineData("argument")] [InlineData("return")]
    public void VerifierRejectsBrokenCallSignatures(string defect)
    {
        var module = WgslReader.Parse(ScalarHelpers); var entry = module.Functions.Single(f => f.Stage == ShaderStage.Compute);
        Assert.True(StructuredControlFlowReader.TryRead(entry, module, out var graph, out var deferred), deferred);
        ControlFlowAnalysis.RemoveUnreachable(graph!);
        var block = graph!.Blocks.Single(b => b.Instructions.Any(i => i.Operation is ValueOperation.Call));
        int index = block.Instructions.FindIndex(i => i.Operation is ValueOperation.Call);
        var instruction = block.Instructions[index]; var call = (ValueOperation.Call)instruction.Operation;
        var argument = graph.Value(ShaderType.U32);
        block.Instructions.Insert(index++, new(argument, new ValueOperation.Literal(1u)));
        block.Instructions[index] = instruction with { Operation = defect switch {
            "missing" => call with { Function = "missing" }, "argument" => call with { Arguments = [argument] },
            _ => call with { ReturnType = ShaderType.I32 }
        }};
        Assert.Contains("illegal Call", Assert.Throws<ShaderException>(() => ControlFlowVerifier.Validate(graph, module)).Message);
    }
    [Theory]
    [InlineData("terminator", "terminator")] [InlineData("arity", "arity")]
    [InlineData("type", "type")] [InlineData("dominance", "dominate")]
    [InlineData("undefined", "undefined")] [InlineData("duplicate", "duplicate")]
    public void VerifierRejectsBrokenSsaAndEdges(string defect, string diagnostic)
    {
        var module = WgslReader.Parse(SwapLoop); var graph = Graph(module); LocalValuePromotion.Run(graph);
        var target = graph.Blocks.First(b => b.Parameters.Count != 0);
        var predecessor = ControlFlowAnalysis.Predecessors(graph)[target.Id][0];
        switch (defect) {
            case "terminator": target.Terminator = null; break;
            case "arity": predecessor.Edge.Arguments.Clear(); break;
            case "type": predecessor.Edge.Arguments[0] = predecessor.Edge.Arguments[0] with { Type = ShaderType.I32 }; break;
            case "dominance": graph.Blocks.First(b => b.Id == graph.Entry).Instructions.Add(new(graph.Value(target.Parameters[0].Type), new ValueOperation.Unary("~", target.Parameters[0]))); break;
            case "undefined": target.Instructions.Add(new(graph.Value(ShaderType.U32), new ValueOperation.Unary("~", new(1000000, ShaderType.U32)))); break;
            case "duplicate": target.Instructions.Add(new(target.Parameters[0], new ValueOperation.Literal(0u))); break;
        }
        Assert.Contains(diagnostic, Assert.Throws<ShaderException>(() => ControlFlowVerifier.Validate(graph, module)).Message);
    }
    [Fact]
    public void VerifierRejectsWriteThroughReadOnlyMemory()
    {
        var module = new Module(); module.Globals.Add(new("input", ShaderType.U32, AddressSpace.Storage, StorageAccess.Read, new(0, 0)));
        var signature = new ShaderFunction("main") { Stage = ShaderStage.Compute }; module.Functions.Add(signature);
        var graph = new ControlFlowFunction(signature); var block = graph.Block(); graph.Entry = block.Id;
        var pointer = graph.Value(new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage, StorageAccess.Read)); var value = graph.Value(ShaderType.U32);
        block.Instructions.Add(new(pointer, new ValueOperation.Symbol("input"))); block.Instructions.Add(new(value, new ValueOperation.Literal(1u)));
        block.Instructions.Add(new(null, new ValueOperation.Store(pointer, value))); block.Terminator = new ControlFlowTerminator.Return();
        Assert.Contains("illegal Store", Assert.Throws<ShaderException>(() => ControlFlowVerifier.Validate(graph, module)).Message);
    }
}
