using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class NativeInvocationKillTests
{
    internal static SpirvBinary ContinuingFixture(bool repeat = false)
    {
        string loop = "var count=0u;loop{let previous=count;if inputs[0]==2u{break;}count++;"
            + "switch count{case 1u:{continue;}default:{outputs[0]+=100u;}}"
            + "continuing{outputs[1]+=3u;let value=choose(&count,(inputs[0]&1u)!=0u);outputs[0]+=value;break if previous>=2u;}}";
        string source = "fn choose(p:ptr<function,u32>,condition:bool)->u32{*p+=1u;if condition{return 7u;}return 9u;}"
            + "@group(0) @binding(0) var<storage,read> inputs:array<u32>;"
            + "@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;"
            + "@fragment fn main()->@location(0) u32{outputs[0]=40u;outputs[1]=20u;"
            + (repeat ? "var outer=0u;loop{if outer>=2u{break;}" + loop + "outer++;}" : loop)
            + "outputs[1]+=100u;return outputs[0]+1000u;}";
        // Retain the actual pointer helper call in the native continuing region.
        var binary = SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(WgslReader.Parse(source)));
        var pointers = binary.Instructions.Where(i => (Op)i.Opcode == Op.TypePointer).Select(i => i.Operands[0]).ToHashSet();
        var code = binary.Instructions.ToList(); bool helper = false, replaced = false;
        for (int index = 0; index < code.Count; index++) {
            var instruction = code[index]; var op = (Op)instruction.Opcode;
            if (op == Op.Function) helper = false;
            if (op == Op.FunctionParameter && pointers.Contains(instruction.Operands[0])) helper = true;
            if (helper && op == Op.ReturnValue && !replaced) { code[index] = new((ushort)Op.Kill, []); replaced = true; }
        }
        if (!replaced) throw new InvalidOperationException("Fixture needs a pointer helper return.");
        return new() { Version = binary.Version, Generator = binary.Generator, Bound = binary.Bound, Instructions = code };
    }

    [Theory]
    [InlineData(false, 0u, 158u, 126u, false)] [InlineData(true, 0u, 276u, 132u, false)]
    [InlineData(false, 1u, 40u, 23u, true)] [InlineData(true, 1u, 40u, 23u, true)]
    [InlineData(false, 2u, 40u, 120u, false)] [InlineData(true, 2u, 40u, 120u, false)]
    public void ContinuingKillKeepsContinueBreakScopeAndRepeatedEntry(bool repeat, uint input, uint first, uint second, bool killed)
    {
        var binary = ContinuingFixture(repeat); byte[] original = binary.ToBytes(); var module = SpirvReader.Parse(original);
        byte[] native = SpirvWriter.Write(module, SpirvCompilationTarget.Default); string wgsl = WgslWriter.Write(module, SpirvCompilationTarget.Default);
        Assert.Equal(native, SpirvWriter.Write(module, SpirvCompilationTarget.Default)); Assert.Equal(original, binary.ToBytes());
        Assert.Contains(SpirvBinary.Parse(native).Instructions, i => (Op)i.Opcode == Op.Kill);
        foreach (var candidate in new[] { module, SpirvReader.Parse(native), WgslReader.Parse(wgsl) }) {
            var machine = new CanonicalExecution(candidate, [input]);
            Assert.Equal(new[] { first, second }, machine.Run().Output); Assert.Equal(killed, machine.InvocationKilled);
        }
    }

    internal static SpirvBinary ColorFixture()
    {
        var binary = SpirvBinary.Parse(SpirvWriter.Write(WgslReader.Parse(
            "fn choose(condition:bool)->u32{if condition{return 7u;}return 9u;}"
            + "@group(0) @binding(0) var<storage,read> inputs:array<u32>;"
            + "@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;"
            + "@fragment fn main()->@location(0) u32{outputs[0]=43u;outputs[1]=23u;"
            + "let value=choose((inputs[0]&1u)!=0u);outputs[0]=value;outputs[1]=99u;return value+100u;}"), SpirvCompilationTarget.Default));
        uint entry = binary.Instructions.Single(i => (Op)i.Opcode == Op.EntryPoint).Operands[1];
        var code = binary.Instructions.ToList(); bool helper = false, replaced = false;
        for (int index = 0; index < code.Count; index++) {
            var instruction = code[index]; var op = (Op)instruction.Opcode;
            if (op == Op.Function) helper = instruction.Operands[1] != entry;
            if (helper && op == Op.ReturnValue && !replaced) {
                code[index] = new((ushort)Op.Kill, []); replaced = true;
            }
            if (op == Op.FunctionEnd) helper = false;
        }
        if (!replaced) throw new InvalidOperationException("Fixture needs a helper return.");
        return new() { Version = binary.Version, Generator = binary.Generator, Bound = binary.Bound, Instructions = code };
    }

    [Theory] [InlineData(0u, 9u, 99u, false)] [InlineData(1u, 43u, 23u, true)]
    public void FragmentKillPreservesEarlierStoresAndStopsCallerStores(uint input, uint first, uint second, bool killed)
    {
        var binary = ColorFixture(); byte[] before = binary.ToBytes(); var module = SpirvReader.Parse(before);
        foreach (var candidate in new[] { module, SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)), WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)) }) {
            var machine = new CanonicalExecution(candidate, [input]);
            Assert.Equal(new[] { first, second }, machine.Run().Output); Assert.Equal(killed, machine.InvocationKilled);
        }
        Assert.Equal(before, binary.ToBytes());
    }

    internal static SpirvBinary Fixture(bool nested = false, bool repeat = false, bool scalar = false)
    {
        var source = scalar ? NativeUnreachableTests.ScalarFixture() : NativeUnreachableTests.Fixture(nested, repeat);
        uint entry = source.Instructions.First(i => (Op)i.Opcode == Op.EntryPoint).Operands[1];
        var code = source.Instructions.Where(i => (Op)i.Opcode != Op.ExecutionMode).Select(i => (Op)i.Opcode == Op.EntryPoint
            ? i with { Operands = new uint[] { 4 }.Concat(i.Operands.Skip(1)).ToArray() }
            : (Op)i.Opcode == Op.Unreachable ? new SpirvInstruction((ushort)Op.Kill, []) : i).ToList();
        code.Insert(code.FindIndex(i => (Op)i.Opcode == Op.EntryPoint) + 1, new((ushort)Op.ExecutionMode, [entry, 7]));
        return new() { Version = source.Version, Generator = source.Generator, Bound = source.Bound, Instructions = code };
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public void NativeKillHelpersEnterCanonicalControlFlow(bool nested, bool repeat)
    {
        var binary = SpirvBinary.Parse(Fixture(nested, repeat).ToBytes()); byte[] original = binary.ToBytes();
        var traces = new List<CanonicalPassTrace>(); var deferrals = new List<CanonicalDeferral>();
        var module = SpirvReader.ReadBinary(binary, traces: traces, deferrals: deferrals);
        Assert.Contains(traces, t => t.Pass == "native-cfg-import" && t.Before.Contains("invocation-kill span="));
        Assert.DoesNotContain(deferrals, d => d.Function == "<SPIR-V>"); Assert.Equal(original, binary.ToBytes());
        ModuleValidator.Validate(module);
        Assert.Contains(SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)).Instructions, i => (Op)i.Opcode == Op.Kill);
        _ = WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void ScalarKillHelpersHaveLegalWgslReturnsAndPreserveNativeTermination(bool repeatedOrder)
    {
        var module = SpirvReader.Parse(Fixture(scalar: true).ToBytes());
        byte[] before = SpirvWriter.Write(module, SpirvCompilationTarget.Default); string wgsl = WgslWriter.Write(module, SpirvCompilationTarget.Default);
        Assert.Equal(before, SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        Assert.Contains(SpirvBinary.Parse(before).Instructions, i => (Op)i.Opcode == Op.Kill);
        var parsed = WgslReader.Parse(wgsl); ModuleValidator.Validate(parsed);
        Assert.Contains("discard;", wgsl);
        if (repeatedOrder) Assert.Equal(wgsl, WgslWriter.Write(module, SpirvCompilationTarget.Default));
    }

    [Fact]
    public void NativeKillOperandsAreRejectedWithoutFallback()
    {
        var source = Fixture(); var code = source.Instructions.ToList(); int kill = code.FindIndex(i => (Op)i.Opcode == Op.Kill);
        code[kill] = code[kill] with { Operands = [source.Bound] }; var deferrals = new List<CanonicalDeferral>();
        var error = Assert.Throws<ShaderException>(() => SpirvReader.ReadBinary(new() { Version = source.Version, Bound = source.Bound, Instructions = code }, deferrals: deferrals));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains("operand count", error.Message);
        Assert.DoesNotContain(deferrals, d => d.Function == "<SPIR-V>");
    }

    [Theory]
    [InlineData(false, false, 0u, 1u, 10u, true)] [InlineData(true, false, 2u, 3u, 10u, true)]
    [InlineData(false, true, 0u, 1u, 10u, true)] [InlineData(false, false, 5u, 14u, 26u, false)]
    [InlineData(true, false, 5u, 14u, 26u, false)] [InlineData(false, true, 5u, 22u, 42u, false)]
    public void KillingAHigherLevelInvocationNeverReturnsToCallerStores(bool nested, bool repeat, uint input, uint first, uint second, bool killed)
    {
        var module = SpirvReader.Parse(Fixture(nested, repeat).ToBytes());
        foreach (var candidate in new[] { module, SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)), WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)) }) {
            var machine = new CanonicalExecution(candidate, [input]); var result = machine.Run();
            Assert.Equal(new[] { first, second }, result.Output); Assert.Equal(killed, machine.InvocationKilled);
        }
    }

    [Fact]
    public void KillTerminatorRetainsOriginAndOrderedInvocationEffects()
    {
        var module = SpirvReader.Parse(Fixture().ToBytes()); int observed = 0;
        foreach (var function in module.Functions) {
            Assert.True(StructuredControlFlowReader.TryRead(function, module, out var graph, out var reason), reason);
            ControlFlowAnalysis.RemoveUnreachable(graph!); ControlFlowVerifier.Validate(graph!, module);
            foreach (var kill in graph!.Blocks.Select(b => b.Terminator).OfType<ControlFlowTerminator.InvocationKill>()) {
                observed++; Assert.Empty(kill.Operands); Assert.Empty(kill.Edges);
                Assert.True(kill.Span.Start > 0); Assert.Equal(4, kill.Span.Length);
                Assert.Equal(ShaderEffects.Convergent | ShaderEffects.InvocationTermination, kill.Effects);
            }
        }
        Assert.True(observed > 0);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void AnAlwaysNonReturningCallDoesNotReachAFollowingBarrier(bool canonical)
    {
        var module = WgslReader.Parse("fn stop(){}@compute @workgroup_size(2) fn main(@builtin(local_invocation_index) id:u32){if true{stop();workgroupBarrier();}}");
        module.Functions.Single(f => f.Name == "stop").Body.Statements.Add(new Statement.Unreachable());
        var main = module.Functions.Single(f => f.Stage == ShaderStage.Compute);
        var branch = Assert.IsType<Statement.If>(main.Body.Statements[0]);
        main.Body.Statements[0] = branch with { Condition = new Expression.Binary("!=", new Expression.Reference("id", ShaderType.U32), Expression.U32(0), ShaderType.Bool) };
        ModuleValidator.Validate(module); var graphs = new Dictionary<string, ControlFlowFunction>();
        if (canonical) foreach (var function in module.Functions) {
            Assert.True(StructuredControlFlowReader.TryRead(function, module, out var graph, out var reason), reason);
            ControlFlowAnalysis.RemoveUnreachable(graph!); graphs.Add(function.Name, graph!);
        }
        _ = UniformityAnalysis.Validate(module, graphs);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void OrdinaryConditionalHelperReturnsStillReconverge(bool canonical)
    {
        var module = WgslReader.Parse("fn choose(condition:bool)->u32{if condition{return 7u;}return 9u;}@compute @workgroup_size(2) fn main(@builtin(local_invocation_index) id:u32){let result=choose(id!=0u);workgroupBarrier();}");
        var graphs = new Dictionary<string, ControlFlowFunction>();
        if (canonical) foreach (var function in module.Functions) {
            Assert.True(StructuredControlFlowReader.TryRead(function, module, out var graph, out var reason), reason);
            ControlFlowAnalysis.RemoveUnreachable(graph!); graphs.Add(function.Name, graph!);
        }
        _ = UniformityAnalysis.Validate(module, graphs);
    }

    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void NonReturningCalleeDoesNotFakeReconvergenceAtCallerBarrier(bool canonical, bool nested)
    {
        var module = WgslReader.Parse("fn stop(condition:bool){if condition{return;}}fn forward(condition:bool){stop(condition);}"
            + "@compute @workgroup_size(2) fn main(@builtin(local_invocation_index) id:u32){stop(id!=0u);workgroupBarrier();}");
        var stop = module.Functions.Single(f => f.Name == "stop");
        var branch = Assert.IsType<Statement.If>(stop.Body.Statements[0]); branch.Accept.Statements[0] = new Statement.Unreachable();
        if (nested) {
            var main = module.Functions.Single(f => f.Stage == ShaderStage.Compute);
            var evaluate = Assert.IsType<Statement.Evaluate>(main.Body.Statements[0]);
            main.Body.Statements[0] = evaluate with { Value = Assert.IsType<Expression.Call>(evaluate.Value) with { Function = "forward" } };
        }
        ModuleValidator.Validate(module); var graphs = new Dictionary<string, ControlFlowFunction>();
        if (canonical) foreach (var function in module.Functions) {
            Assert.True(StructuredControlFlowReader.TryRead(function, module, out var graph, out var reason), reason);
            ControlFlowAnalysis.RemoveUnreachable(graph!); graphs.Add(function.Name, graph!);
        }
        var error = Assert.Throws<ShaderException>(() => UniformityAnalysis.Validate(module, graphs));
        Assert.Contains("barrier", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
