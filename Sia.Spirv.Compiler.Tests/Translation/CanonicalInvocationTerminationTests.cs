using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalInvocationTerminationTests
{
    public static TheoryData<bool, bool, uint, uint, uint, bool> ContinuingCases {
        get {
            var cases = new TheoryData<bool, bool, uint, uint, uint, bool>();
            foreach (bool terminate in new[] { false, true }) {
                cases.Add(false, terminate, 0, 158, 126, false);
                cases.Add(true, terminate, 0, 276, 132, false);
                cases.Add(false, terminate, 1, 40, 23, true);
                cases.Add(true, terminate, 1, 40, 23, true);
                cases.Add(false, terminate, 2, 40, 120, false);
                cases.Add(true, terminate, 2, 40, 120, false);
            }
            return cases;
        }
    }

    internal static SpirvBinary ContinuingFixture(bool repeat, bool terminate)
    {
        var binary = NativeInvocationKillTests.ContinuingFixture(repeat);
        if (!terminate) return binary;
        var code = binary.Instructions.Select(i => (Op)i.Opcode == Op.Kill
            ? new SpirvInstruction((ushort)Op.TerminateInvocation, []) : i).ToList();
        code.Insert(code.FindIndex(i => (Op)i.Opcode is not (Op.Capability or Op.Extension)),
            new((ushort)Op.Extension, SpirvBinary.StringWords("SPV_KHR_terminate_invocation")));
        return new() { Version = binary.Version, Bound = binary.Bound, Generator = binary.Generator, Instructions = code };
    }

    [Theory]
    [MemberData(nameof(ContinuingCases))]
    public void OwnedTerminatingHelpersKeepLoopScopeAndStoresWithStaleBodies(bool repeat, bool terminate, uint value, uint first, uint second, bool killed)
    {
        var canonical = CanonicalShaderPipeline.Prepare(SpirvReader.Parse(ContinuingFixture(repeat, terminate).ToBytes()));
        Assert.Empty(canonical.DeferredFunctions);
        var snapshots = canonical.Functions.ToDictionary(p => p.Key, p => ControlFlowPrinter.Write(p.Value));
        var kills = canonical.Functions.Values.SelectMany(g => g.Blocks).Select(b => b.Terminator)
            .OfType<ControlFlowTerminator.InvocationKill>().ToArray();
        Assert.NotEmpty(kills);
        var bodies = canonical.Declarations.Functions.ToDictionary(f => f.Name, f => f.Body);
        try {
            foreach (var function in canonical.Declarations.Functions) function.Body = new();
            var lowered = ShaderTargetLowering.PrepareSpirv(canonical, null);
            Assert.All(canonical.Functions, p => Assert.Equal(snapshots[p.Key], ControlFlowPrinter.Write(p.Value)));
            foreach (var kill in kills) Assert.Contains(lowered.Functions.Values.SelectMany(g => g.Blocks).Select(b => b.Terminator), t => t == kill);
            var binary = SpirvWriter.Emit(SpirvEntryPointLowering.Run(lowered, true, true));
            Assert.Contains(binary.Instructions, i => (Op)i.Opcode == (terminate ? Op.TerminateInvocation : Op.Kill));
            var machine = new CanonicalExecution(SpirvReader.Parse(binary.ToBytes()), [value]);
            Assert.Equal(new[] { first, second }, machine.Run().Output);
            Assert.Equal(killed, machine.InvocationKilled);
        } finally { foreach (var function in canonical.Declarations.Functions) function.Body = bodies[function.Name]; }
    }

    internal static CanonicalModule BranchingGraph(bool multiple, bool terminate = false)
    {
        var module = WgslReader.Parse("@group(0) @binding(0) var<storage,read> inputs:array<u32>;"
            + "@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;"
            + "@fragment fn main()->@location(0) u32{return 0u;}");
        var signature = module.Functions.Single(); signature.Body = new();
        var graph = new ControlFlowFunction(signature);
        var entry = graph.Block(); var header = graph.Block(); var continuing = graph.Block();
        var kill = graph.Block(); var choose = graph.Block(); var exit = graph.Block(); graph.Entry = entry.Id;
        SsaValue Value(ControlFlowBlock block, ShaderType type, ValueOperation operation) {
            var result = graph.Value(type); block.Instructions.Add(new(result, operation)); return result;
        }
        SsaValue Literal(ControlFlowBlock block, uint value) => Value(block, ShaderType.U32, new ValueOperation.Literal(value));
        var zero = Literal(entry, 0); var one = Literal(entry, 1); var two = Literal(entry, 2); var three = Literal(entry, 3);
        var input = module.Globals.Single(g => g.Name == "inputs"); var output = module.Globals.Single(g => g.Name == "outputs");
        var inputRoot = Value(entry, new ShaderType.Pointer(input.Type, input.Space, input.Access), new ValueOperation.Symbol(input.Name));
        var inputAddress = Value(entry, new ShaderType.Pointer(ShaderType.U32, input.Space, input.Access), new ValueOperation.Access(inputRoot, zero));
        var n = Value(entry, ShaderType.U32, new ValueOperation.Load(inputAddress));
        var outputRoot = Value(entry, new ShaderType.Pointer(output.Type, output.Space, output.Access), new ValueOperation.Symbol(output.Name));
        var first = Value(entry, new ShaderType.Pointer(ShaderType.U32, output.Space, output.Access), new ValueOperation.Access(outputRoot, zero));
        var second = Value(entry, new ShaderType.Pointer(ShaderType.U32, output.Space, output.Access), new ValueOperation.Access(outputRoot, one));
        entry.Instructions.Add(new(null, new ValueOperation.Store(second, Literal(entry, 8))));
        var count = graph.Value(ShaderType.U32); header.Parameters.Add(count);
        entry.Terminator = new ControlFlowTerminator.Branch(new(header.Id, [multiple ? zero : Literal(entry, 5)]));
        if (multiple) {
            var check = graph.Block(); header.Terminator = new ControlFlowTerminator.Branch(new(check.Id));
            var condition = Value(check, ShaderType.Bool, new ValueOperation.Binary("<", count, three));
            check.Terminator = new ControlFlowTerminator.Conditional(condition, new(continuing.Id), new(exit.Id));
            graph.SelectionMerges.Add(check.Id, continuing.Id);
        } else header.Terminator = new ControlFlowTerminator.Branch(new(continuing.Id));
        continuing.Instructions.Add(new(null, new ValueOperation.Store(first, count)));
        var stop = Value(continuing, ShaderType.Bool, new ValueOperation.Binary("==", n, one));
        continuing.Terminator = new ControlFlowTerminator.Conditional(stop, new(kill.Id), new(choose.Id));
        graph.SelectionMerges.Add(continuing.Id, choose.Id);
        kill.Terminator = new ControlFlowTerminator.InvocationKill(new SourceSpan(91, 4), terminate);
        if (multiple) {
            var left = graph.Block(); var right = graph.Block();
            var branch = Value(choose, ShaderType.Bool, new ValueOperation.Binary("==", n, two));
            choose.Terminator = new ControlFlowTerminator.Conditional(branch, new(left.Id), new(right.Id));
            graph.SelectionMerges.Add(choose.Id, null);
            var nextLeft = Value(left, ShaderType.U32, new ValueOperation.Binary("+", count, one));
            var nextRight = Value(right, ShaderType.U32, new ValueOperation.Binary("+", count, two));
            left.Terminator = new ControlFlowTerminator.Branch(new(header.Id, [nextLeft]));
            right.Terminator = new ControlFlowTerminator.Branch(new(header.Id, [nextRight]));
        } else choose.Terminator = new ControlFlowTerminator.Branch(new(exit.Id));
        exit.Terminator = new ControlFlowTerminator.Return(count);
        graph.Loops.Add(header.Id, new(continuing.Id, exit.Id));
        var result = new CanonicalModule(module, new Dictionary<string, ControlFlowFunction> { [signature.Name] = graph },
            new Dictionary<string, string>(), new HashSet<string> { signature.Name });
        ModuleValidator.Validate(result, native: true); return result;
    }

    internal static SpirvBinary BranchingFixture(bool multiple, bool terminate)
        => SpirvWriter.Emit(SpirvEntryPointLowering.Run(ShaderTargetLowering.PrepareSpirv(BranchingGraph(multiple, terminate), null), true, true));

    internal static CanonicalModule NestedGraph(bool terminate)
    {
        var canonical = BranchingGraph(true, terminate); var graph = canonical.Functions["main"];
        int continuing = graph.Loops.Single().Value.Continuing!.Value;
        var entry = graph.Blocks.Single(b => b.Id == graph.Entry);
        var n = entry.Instructions.Single(i => i.Operation is ValueOperation.Load).Result!.Value;
        var second = entry.Instructions.Select(i => i.Operation).OfType<ValueOperation.Store>().Single().Pointer;
        var check = graph.Blocks.Single(b => b.Instructions.Any(i => i.Operation is ValueOperation.Binary { Operator: "<" }));
        var outer = graph.Block(); var inner = graph.Block(); var innerMerge = graph.Block(); var outerMerge = graph.Block();
        var two = graph.Value(ShaderType.U32); outer.Instructions.Add(new(two, new ValueOperation.Literal(2u)));
        var earlyOuter = graph.Value(ShaderType.Bool); outer.Instructions.Add(new(earlyOuter, new ValueOperation.Binary("==", n, two)));
        outer.Terminator = new ControlFlowTerminator.Conditional(earlyOuter, new(continuing), new(inner.Id));
        var zero = graph.Value(ShaderType.U32); inner.Instructions.Add(new(zero, new ValueOperation.Literal(0u)));
        var earlyInner = graph.Value(ShaderType.Bool); inner.Instructions.Add(new(earlyInner, new ValueOperation.Binary("==", n, zero)));
        inner.Terminator = new ControlFlowTerminator.Conditional(earlyInner, new(continuing), new(innerMerge.Id));
        var value = graph.Value(ShaderType.U32); innerMerge.Instructions.Add(new(value, new ValueOperation.Literal(88u)));
        innerMerge.Instructions.Add(new(null, new ValueOperation.Store(second, value)));
        innerMerge.Terminator = new ControlFlowTerminator.Branch(new(outerMerge.Id));
        outerMerge.Terminator = new ControlFlowTerminator.Branch(new(continuing));
        check.Terminator = ((ControlFlowTerminator.Conditional)check.Terminator!) with { Accept = new(outer.Id) };
        graph.SelectionMerges[check.Id] = outer.Id;
        graph.SelectionMerges.Add(outer.Id, outerMerge.Id); graph.SelectionMerges.Add(inner.Id, innerMerge.Id);
        ModuleValidator.Validate(canonical, native: true); return canonical;
    }

    internal static SpirvBinary NestedFixture(bool terminate)
        => SpirvWriter.Emit(SpirvEntryPointLowering.Run(ShaderTargetLowering.PrepareSpirv(NestedGraph(terminate), null), true, true));

    [Theory]
    [InlineData(false, 0u, 2u, 8u, false)] [InlineData(true, 0u, 2u, 8u, false)]
    [InlineData(false, 1u, 0u, 88u, true)] [InlineData(true, 1u, 0u, 88u, true)]
    [InlineData(false, 2u, 2u, 8u, false)] [InlineData(true, 2u, 2u, 8u, false)]
    [InlineData(false, 3u, 2u, 88u, false)] [InlineData(true, 3u, 2u, 88u, false)]
    public void NestedContinueExitsSkipOnlyTheirOriginalMergeEffects(bool terminate, uint input, uint first, uint second, bool killed)
    {
        var canonical = NestedGraph(terminate); var graph = canonical.Functions["main"];
        string before = ControlFlowPrinter.Write(graph);
        var prepared = ShaderTargetLowering.PrepareSpirv(canonical, null);
        Assert.Equal(before, ControlFlowPrinter.Write(graph));
        var machine = new CanonicalExecution(SpirvReader.Parse(SpirvWriter.Emit(SpirvEntryPointLowering.Run(prepared, true, true)).ToBytes()), [input]);
        Assert.Equal(new[] { first, second }, machine.Run().Output); Assert.Equal(killed, machine.InvocationKilled);
    }

    [Fact]
    public void ExplicitDeferredTerminationUsesOnlyItsDeclaredAdapter()
    {
        var module = WgslReader.Parse("@fragment fn main(){loop{}}");
        var function = module.Functions.Single(); var loop = Assert.IsType<Statement.Loop>(function.Body.Statements.Single());
        loop.Continuing.Statements.Add(new Statement.InvocationKill { Span = new(21, 4) });
        var canonical = new CanonicalModule(module, new Dictionary<string, ControlFlowFunction>(),
            new Dictionary<string, string> { [function.Name] = "legacy family" }, new HashSet<string> { function.Name });
        var body = function.Body;
        var prepared = InvocationTerminationControlFlow.PrepareSpirv(canonical, DiagnosticStage.SpirvWrite);
        Assert.Empty(prepared.DeferredFunctions); Assert.Empty(canonical.Functions); Assert.Same(body, function.Body);
        Assert.Contains(prepared.Functions[function.Name].Blocks.Select(b => b.Terminator), t => t is ControlFlowTerminator.InvocationKill { Span.Start: 21 });
    }

    [Theory]
    [InlineData(false, false, 0u, 5u, false)] [InlineData(false, true, 1u, 5u, true)]
    [InlineData(true, false, 0u, 2u, false)] [InlineData(true, true, 1u, 0u, true)]
    [InlineData(true, false, 2u, 2u, false)] [InlineData(true, true, 2u, 2u, false)]
    public void MultipleAndAbsentBackedgesKeepTypedLoopParameters(bool multiple, bool terminate, uint input, uint first, bool killed)
    {
        var canonical = BranchingGraph(multiple, terminate); var graph = canonical.Functions["main"];
        var before = ControlFlowPrinter.Write(graph); var header = graph.Loops.Single().Key;
        var parameter = graph.Blocks.Single(b => b.Id == header).Parameters.Single();
        var lowered = InvocationTerminationControlFlow.PrepareSpirv(canonical, DiagnosticStage.SpirvWrite);
        Assert.Equal(before, ControlFlowPrinter.Write(graph)); Assert.NotSame(graph, lowered.Functions["main"]);
        Assert.Equal(parameter, lowered.Functions["main"].Blocks.Single(b => b.Id == header).Parameters.Single());
        Assert.Contains(lowered.Functions["main"].Blocks.Select(b => b.Terminator), t => t is ControlFlowTerminator.InvocationKill { Span.Start: 91 });
        if (multiple) {
            int target = lowered.Functions["main"].Loops[header].Continuing!.Value;
            var latch = lowered.Functions["main"].Blocks.Single(b => b.Id == target);
            Assert.Equal(ShaderType.U32, latch.Parameters.Single().Type);
            Assert.Equal(2, ControlFlowAnalysis.Predecessors(lowered.Functions["main"])[target].Count);
        } else Assert.Null(lowered.Functions["main"].Loops[header].Continuing);
        Assert.Same(lowered, InvocationTerminationControlFlow.PrepareSpirv(lowered, DiagnosticStage.SpirvWrite));
        var binary = BranchingFixture(multiple, terminate);
        var machine = new CanonicalExecution(SpirvReader.Parse(binary.ToBytes()), [input]);
        Assert.Equal(new uint[] { first, 8 }, machine.Run().Output); Assert.Equal(killed, machine.InvocationKilled);
    }

    [Fact]
    public void KillOutsideContinuingNeedsNoGraphRewrite()
    {
        var canonical = CanonicalShaderPipeline.Prepare(SpirvReader.Parse(NativeInvocationKillTests.ColorFixture().ToBytes()));
        Assert.Same(canonical, InvocationTerminationControlFlow.PrepareSpirv(canonical, DiagnosticStage.SpirvWrite));
    }
}
