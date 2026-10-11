using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class NativeUnreachableTests
{
    internal static SpirvBinary Fixture(bool nested = false, bool repeat = false)
        => ReplaceReturn(NativeCanonicalPointerReturnTests.Fixture(nested: nested, repeat: repeat), pointer: true);

    internal static SpirvBinary ScalarFixture()
        => ReplaceReturn(SpirvBinary.Parse(SpirvWriter.Write(WgslReader.Parse(
            "fn choose(condition:bool)->u32{if condition{return 7u;}return 9u;}"
            + "@group(0) @binding(0) var<storage,read> inputs:array<u32>;"
            + "@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;"
            + "@compute @workgroup_size(1) fn main(){outputs[0]=choose((inputs[0]&1u)!=0u);outputs[1]=99u;}"), SpirvCompilationTarget.Default)), pointer: false);

    internal static SpirvBinary ContinuingFixture()
    {
        var binary = SpirvBinary.Parse(SpirvWriter.Write(WgslReader.Parse(
            "@group(0) @binding(0) var<storage,read> inputs:array<u32>;"
            + "@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;"
            + "fn abort(){outputs[0]=100u;return;}"
            + "@compute @workgroup_size(1) fn main(){outputs[0]=inputs[0];outputs[1]=10u;var counter=0u;"
            + "loop{if counter>=1u{break;}counter++;continuing{if (inputs[0]&1u)!=0u{abort();}outputs[1]+=3u;}}outputs[0]+=7u;}"), SpirvCompilationTarget.Default));
        var code = binary.Instructions.ToList();
        uint hundred = code.First(i => (Op)i.Opcode == Op.Constant && i.Operands.Length == 3 && i.Operands[2] == 100).Operands[1];
        int store = code.FindIndex(i => (Op)i.Opcode == Op.Store && i.Operands[1] == hundred);
        // The call remains in continuing, while its callee does not return.
        // A direct terminating branch in a multi-block continue construct
        // would violate SPIR-V's structural post-dominance requirement.
        int ret = code.FindIndex(store + 1, i => (Op)i.Opcode == Op.Return);
        code[ret] = new((ushort)Op.Unreachable, []);
        return new() { Version = binary.Version, Generator = binary.Generator, Bound = binary.Bound, Instructions = code };
    }

    private static SpirvBinary ReplaceReturn(SpirvBinary binary, bool pointer)
    {
        var pointers = binary.Instructions.Where(i => (Op)i.Opcode == Op.TypePointer).Select(i => i.Operands[0]).ToHashSet();
        bool helper = false, replaced = false; int returns = 0;
        var code = new List<SpirvInstruction>();
        foreach (var instruction in binary.Instructions) {
            var op = (Op)instruction.Opcode;
            if (op == Op.Function) helper = pointer ? pointers.Contains(instruction.Operands[0])
                : binary.Instructions.Any(i => (Op)i.Opcode == Op.TypeInt && i.Operands[0] == instruction.Operands[0]);
            if (helper && op == Op.ReturnValue && !replaced && ++returns == (pointer ? 2 : 1)) {
                code.Add(new((ushort)Op.Unreachable, [])); replaced = true;
            }
            else code.Add(instruction);
            if (op == Op.FunctionEnd) helper = false;
        }
        if (!replaced) throw new InvalidOperationException("Fixture needs a helper return.");
        return new() { Version = binary.Version, Generator = binary.Generator, Bound = binary.Bound, Instructions = code };
    }

    [Theory]
    [InlineData(false, false, 5u, 14u, 26u)] [InlineData(false, false, 7u, 16u, 30u)]
    [InlineData(true, false, 5u, 14u, 26u)] [InlineData(true, false, 7u, 16u, 30u)]
    [InlineData(false, true, 5u, 22u, 42u)] [InlineData(false, true, 7u, 24u, 46u)]
    public void NativeUnreachableHelpersPreserveDefinedExecution(bool nested, bool repeat, uint input, uint first, uint second)
    {
        var binary = SpirvBinary.Parse(Fixture(nested, repeat).ToBytes()); byte[] original = binary.ToBytes();
        var traces = new List<CanonicalPassTrace>(); var deferrals = new List<CanonicalDeferral>();
        var module = SpirvReader.ReadBinary(binary, traces: traces, deferrals: deferrals);
        Assert.Contains(traces, t => t.Pass == "native-cfg-import" && t.Before.Contains("  unreachable span=", StringComparison.Ordinal));
        Assert.DoesNotContain(deferrals, d => d.Function == "<SPIR-V>");
        Assert.Equal(original, binary.ToBytes()); ModuleValidator.Validate(module);
        Assert.Equal(new[] { first, second }, new CanonicalExecution(module, [input]).Run().Output);
        var output = SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        Assert.Contains(output.Instructions, i => (Op)i.Opcode == Op.Unreachable);
        Assert.Equal(new[] { first, second }, new CanonicalExecution(SpirvReader.Parse(output.ToBytes()), [input]).Run().Output);
        Assert.Equal(new[] { first, second }, new CanonicalExecution(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)), [input]).Run().Output);
    }

    [Fact]
    public void LegacyScalarReaderRetainsNonReturningBranches()
    {
        var module = SpirvReader.Parse(ScalarFixture().ToBytes()); ModuleValidator.Validate(module);
        Assert.Equal(new[] { 9u, 99u }, new CanonicalExecution(module, [0u]).Run().Output);
        Assert.Contains(SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)).Instructions, i => (Op)i.Opcode == Op.Unreachable);
        Assert.Equal(new[] { 9u, 99u }, new CanonicalExecution(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)), [0u]).Run().Output);
    }

    [Theory] [InlineData(0u, 7u)] [InlineData(2u, 9u)]
    public void ContinuingUnreachableKeepsDefinedIterationsAndNativeTermination(uint input, uint first)
    {
        var module = SpirvReader.Parse(ContinuingFixture().ToBytes()); ModuleValidator.Validate(module);
        uint[] expected = [first, 13u];
        Assert.Equal(expected, new CanonicalExecution(module, [input]).Run().Output);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Unreachable);
        Assert.Equal(expected, new CanonicalExecution(SpirvReader.Parse(binary.ToBytes()), [input]).Run().Output);
        Assert.Equal(expected, new CanonicalExecution(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)), [input]).Run().Output);
    }

    [Fact]
    public void WgslLegalizationHandlesAnInternalNonReturningContinuingPath()
    {
        var module = WgslReader.Parse("@compute @workgroup_size(1) fn main(){var counter=0u;loop{if counter>=1u{break;}counter++;continuing{if counter==9u{counter=100u;}counter+=0u;}}}");
        var function = Assert.Single(module.Functions);
        var loop = Assert.IsType<Statement.Loop>(function.Body.Statements[1]);
        var branch = Assert.Single(loop.Continuing.Statements.OfType<Statement.If>());
        branch.Accept.Statements.Add(new Statement.Unreachable());
        ModuleValidator.Validate(module);
        var lowered = WgslTerminationLowering.Run(module); ModuleValidator.Validate(lowered);
        var loweredLoop = Assert.IsType<Statement.Loop>(Assert.Single(lowered.Functions).Body.Statements[1]);
        var loweredBranch = Assert.Single(loweredLoop.Continuing.Statements.OfType<Statement.If>());
        Assert.Single(loweredBranch.Accept.Statements);
        Assert.IsType<Statement.Store>(loweredBranch.Accept.Statements[0]);
        Assert.IsType<Statement.Unreachable>(branch.Accept.Statements.Last());
        _ = WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default));
    }

    [Fact]
    public void TheInterpreterDoesNotAssignAnExpectedResultToUndefinedExecution()
    {
        var module = SpirvReader.Parse(Fixture().ToBytes());
        var error = Assert.Throws<InvalidOperationException>(() => new CanonicalExecution(module, [0u]).Run());
        Assert.Contains("undefined execution", error.Message);
    }

    [Fact]
    public void NonReturningTerminatorHasNoReturnOperandOrSuccessorAndKeepsItsSpan()
    {
        var module = SpirvReader.ReadBinary(SpirvBinary.Parse(Fixture(nested: true).ToBytes()));
        int observed = 0;
        foreach (var function in module.Functions) {
            Assert.True(StructuredControlFlowReader.TryRead(function, module, out var graph, out var deferred), deferred);
            ControlFlowAnalysis.RemoveUnreachable(graph!); ControlFlowVerifier.Validate(graph!, module);
            foreach (var abort in graph!.Blocks.Select(b => b.Terminator).OfType<ControlFlowTerminator.Unreachable>()) {
                observed++;
                Assert.Empty(abort.Operands); Assert.Empty(abort.Edges); Assert.True(abort.Span.Start > 0); Assert.Equal(4, abort.Span.Length);
            }
        }
        Assert.True(observed > 0);
    }

    [Fact]
    public void WgslTerminationLegalizationBorrowsItsInputAndDoesNotAffectSpirvOrder()
    {
        var module = SpirvReader.Parse(ScalarFixture().ToBytes()); byte[] before = SpirvWriter.Write(module, SpirvCompilationTarget.Default);
        string wgsl = WgslWriter.Write(module, SpirvCompilationTarget.Default); Assert.Equal(before, SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        var lowered = WgslTerminationLowering.Run(module); ModuleValidator.Validate(lowered);
        Assert.NotSame(module, lowered);
        Assert.Equal(new[] { 9u, 99u }, new CanonicalExecution(WgslReader.Parse(wgsl), [0u]).Run().Output);
        Assert.Contains(SpirvBinary.Parse(before).Instructions, i => (Op)i.Opcode == Op.Unreachable);
    }

    [Fact]
    public void NativeUnreachableOperandsAreRejectedWithoutFallback()
    {
        var binary = Fixture(); var code = binary.Instructions.ToList();
        int index = code.FindIndex(i => (Op)i.Opcode == Op.Unreachable);
        code[index] = code[index] with { Operands = [binary.Bound] };
        var deferrals = new List<CanonicalDeferral>();
        var error = Assert.Throws<ShaderException>(() => SpirvReader.ReadBinary(new SpirvBinary {
            Version = binary.Version, Bound = binary.Bound, Instructions = code
        }, deferrals: deferrals));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains("operand count", error.Message);
        Assert.DoesNotContain(deferrals, d => d.Function == "<SPIR-V>");
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void UnreachableDoesNotFakeUniformReconvergence(bool canonical)
    {
        var module = WgslReader.Parse("@compute @workgroup_size(2) fn main(@builtin(local_invocation_index) id:u32){if true{return;}workgroupBarrier();}");
        var function = Assert.Single(module.Functions); var branch = Assert.IsType<Statement.If>(function.Body.Statements[0]);
        var accept = new Block(); accept.Statements.Add(new Statement.Unreachable());
        function.Body.Statements[0] = branch with { Condition = new Expression.Binary("!=", new Expression.Reference("id", ShaderType.U32),
            Expression.U32(0), ShaderType.Bool), Accept = accept };
        var graphs = new Dictionary<string, ControlFlowFunction>();
        if (canonical) {
            Assert.True(StructuredControlFlowReader.TryRead(function, module, out var graph, out var deferred), deferred);
            ControlFlowAnalysis.RemoveUnreachable(graph!); ControlFlowVerifier.Validate(graph!, module); graphs.Add(function.Name, graph!);
        }
        var error = Assert.Throws<ShaderException>(() => UniformityAnalysis.Validate(module, graphs));
        Assert.Contains("barrier", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
