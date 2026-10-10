using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class NativeCanonicalControlFlowTests
{
    internal static SpirvBinary EnclosingSelectionExitFixture(bool reverse)
    {
        var module = WgslReader.Parse("const zero:u32=0u;const one:u32=1u;const two:u32=2u;const three:u32=3u;const nine:u32=9u;const flag:bool=false;"
            + "@group(0) @binding(0) var<storage,read> inputs:array<u32>;"
            + "@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;"
            + "@compute @workgroup_size(1) fn main(){outputs[0]=zero+one+two+three+nine+inputs[0];}");
        var binary = SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(module));
        var code = binary.Instructions.ToList(); uint next = binary.Bound;
        uint integer = code.First(i => (Op)i.Opcode == Op.TypeInt && i.Operands is [_, 32, 0]).Operands[0];
        uint boolean = code.First(i => (Op)i.Opcode == Op.TypeBool).Operands[0];
        uint Constant(uint value) => code.First(i => (Op)i.Opcode == Op.Constant && i.Operands is [_, _, var v]
            && i.Operands[0] == integer && v == value).Operands[1];
        uint Buffer(uint binding) => code.First(i => (Op)i.Opcode == Op.Decorate && i.Operands is [_, 33, var b] && b == binding).Operands[0];
        uint pointer = code.First(i => (Op)i.Opcode == Op.TypePointer && i.Operands is [_, 12, var type] && type == integer).Operands[0];
        uint zero = Constant(0), one = Constant(1), two = Constant(2), three = Constant(3), nine = Constant(9);
        int start = code.FindIndex(i => (Op)i.Opcode == Op.Function);
        var definition = code[start]; int end = code.FindIndex(start, i => (Op)i.Opcode == Op.FunctionEnd);
        code.RemoveRange(start, end - start + 1);
        uint entry = next++, body = next++, tail = next++, merge = next++;
        uint firstAddress = next++, first = next++, outerCondition = next++;
        uint secondAddress = next++, second = next++, innerCondition = next++;
        uint effectAddress = next++, result = next++, outputAddress = next++;
        SpirvInstruction I(Op op, params uint[] operands) => new((ushort)op, operands);
        code.InsertRange(start, [definition, I(Op.Label, entry),
            I(Op.AccessChain, pointer, firstAddress, Buffer(0), zero, zero), I(Op.Load, integer, first, firstAddress),
            I(Op.INotEqual, boolean, outerCondition, first, zero), I(Op.SelectionMerge, merge, 0),
            I(Op.BranchConditional, outerCondition, body, merge), I(Op.Label, body),
            I(Op.AccessChain, pointer, secondAddress, Buffer(0), zero, one), I(Op.Load, integer, second, secondAddress),
            I(Op.INotEqual, boolean, innerCondition, second, zero),
            // This exits an enclosing selection; it is not a new selection header.
            I(Op.BranchConditional, innerCondition, reverse ? merge : tail, reverse ? tail : merge),
            I(Op.Label, tail), I(Op.AccessChain, pointer, effectAddress, Buffer(1), zero, one),
            I(Op.Store, effectAddress, nine), I(Op.Branch, merge), I(Op.Label, merge),
            I(Op.Phi, integer, result, one, entry, two, body, three, tail),
            I(Op.AccessChain, pointer, outputAddress, Buffer(1), zero, zero), I(Op.Store, outputAddress, result),
            I(Op.Return), I(Op.FunctionEnd)]);
        return SpirvBinary.Parse(new SpirvBinary { Version = binary.Version, Generator = binary.Generator, Bound = next, Instructions = code }.ToBytes());
    }

    [Theory]
    [InlineData(false, 0u, 1u, 1u, 0u)] [InlineData(false, 1u, 0u, 2u, 0u)] [InlineData(false, 1u, 1u, 3u, 9u)]
    [InlineData(true, 0u, 0u, 1u, 0u)] [InlineData(true, 1u, 0u, 3u, 9u)] [InlineData(true, 1u, 1u, 2u, 0u)]
    public void NativeConditionalExitUsesEnclosingSelectionAndPreservesPhiAndEffects(bool reverse, uint first, uint second, uint result, uint effect)
        => CheckRoutes(ReadNative(EnclosingSelectionExitFixture(reverse)), [first, second], [result, effect], first == 0 ? [0] : [0, 1]);

    internal static SpirvBinary PhiLoopFixture()
    {
        var module = WgslReader.Parse("const zero:u32=0u;const one:u32=1u;const two:u32=2u;const ten:u32=10u;const flag:bool=false;"
            + "@group(0) @binding(0) var<storage,read> inputs:array<u32>;"
            + "@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;"
            + "@compute @workgroup_size(1) fn main(){outputs[0]=zero+one+two+ten+inputs[0];}");
        var binary = SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(module)); var code = binary.Instructions.ToList(); uint next = binary.Bound;
        uint integer = code.First(i => (Op)i.Opcode == Op.TypeInt && i.Operands is [_, 32, 0]).Operands[0];
        uint boolean = code.First(i => (Op)i.Opcode == Op.TypeBool).Operands[0];
        uint Constant(uint value) => code.First(i => (Op)i.Opcode == Op.Constant && i.Operands is [_, _, var v]
            && i.Operands[0] == integer && v == value).Operands[1];
        uint Buffer(uint binding) => code.First(i => (Op)i.Opcode == Op.Decorate && i.Operands is [_, 33, var b] && b == binding).Operands[0];
        uint pointer = code.First(i => (Op)i.Opcode == Op.TypePointer && i.Operands is [_, 12, var type] && type == integer).Operands[0];
        uint zero = Constant(0), one = Constant(1), two = Constant(2), ten = Constant(10), input = Buffer(0), output = Buffer(1);
        int start = code.FindIndex(i => (Op)i.Opcode == Op.Function);
        var definition = code[start]; int end = code.FindIndex(start, i => (Op)i.Opcode == Op.FunctionEnd);
        code.RemoveRange(start, end - start + 1);
        uint entry = next++, header = next++, body = next++, continuing = next++, merge = next++;
        uint inputAddress = next++, limit = next++, a = next++, b = next++, count = next++, increment = next++;
        uint done = next++, product = next++, result = next++, outputAddress = next++;
        SpirvInstruction I(Op op, params uint[] operands) => new((ushort)op, operands);
        code.InsertRange(start, [definition, I(Op.Label, entry), I(Op.AccessChain, pointer, inputAddress, input, zero, zero),
            I(Op.Load, integer, limit, inputAddress), I(Op.Branch, header), I(Op.Label, header),
            I(Op.Phi, integer, a, one, entry, b, continuing), I(Op.Phi, integer, b, two, entry, a, continuing),
            I(Op.Phi, integer, count, zero, entry, increment, continuing), I(Op.UGreaterThanEqual, boolean, done, count, limit),
            I(Op.LoopMerge, merge, continuing, 0), I(Op.BranchConditional, done, merge, body),
            I(Op.Label, body), I(Op.Branch, continuing), I(Op.Label, continuing),
            I(Op.IAdd, integer, increment, count, one), I(Op.Branch, header), I(Op.Label, merge),
            I(Op.IMul, integer, product, a, ten), I(Op.IAdd, integer, result, product, b),
            I(Op.AccessChain, pointer, outputAddress, output, zero, zero), I(Op.Store, outputAddress, result),
            I(Op.Return), I(Op.FunctionEnd)]);
        return SpirvBinary.Parse(new SpirvBinary { Version = binary.Version, Generator = binary.Generator, Bound = next, Instructions = code }.ToBytes());
    }

    private static Module ReadNative(SpirvBinary binary)
    {
        byte[] borrowed = binary.ToBytes(); var traces = new List<CanonicalPassTrace>(); var deferrals = new List<CanonicalDeferral>();
        var module = SpirvReader.ReadBinary(binary, traces: traces, deferrals: deferrals);
        Assert.Contains(traces, t => t.Pass == "native-cfg-import");
        Assert.Empty(deferrals); Assert.Equal(borrowed, binary.ToBytes());
        ModuleValidator.Validate(module); return module;
    }

    private static void CheckRoutes(Module module, uint[] input, uint[] expected, int[]? reads = null)
    {
        foreach (var candidate in new[] { module, WgslReader.Parse(WgslWriter.Write(module)), SpirvReader.Parse(SpirvWriter.Write(module)) }) {
            var actual = new CanonicalExecution(candidate, input).Run(); Assert.Equal(expected, actual.Output);
            if (reads is not null) Assert.Equal(reads, actual.Reads);
        }
    }

    [Theory]
    [InlineData(0u, 12u)] [InlineData(1u, 21u)] [InlineData(2u, 12u)] [InlineData(5u, 21u)]
    public void NativeScalarPhiLoopKeepsParallelArguments(uint input, uint expected)
        => CheckRoutes(ReadNative(PhiLoopFixture()), [input], [expected, 0], [0]);

    [Theory]
    [InlineData("swap", 1u, 21u, 0u)] [InlineData("switch", 0u, 10u, 0u)]
    [InlineData("switch", 1u, 20u, 99u)] [InlineData("switch", 4u, 6u, 99u)]
    [InlineData("or", 0u, 7u, 0u)] [InlineData("and", 0u, 9u, 0u)]
    [InlineData("helpers", 0u, 14u, 2u)] [InlineData("helpers", 2u, 6u, 2u)]
    [InlineData("nested", 2u, 28u, 1u)] [InlineData("nested", 10u, 136u, 3u)]
    [InlineData("continuing", 0u, 3u, 0u)] [InlineData("scope", 0u, 3u, 0u)]
    [InlineData("signed", 0xfffffff8u, 0xfffffffcu, 0u)]
    public void OrdinaryNativeControlFlowUsesSharedImportWithSpecifiedResults(string family, uint input, uint first, uint second)
    {
        string source = family switch {
            "swap" => CanonicalControlFlowTests.SwapLoop, "switch" => CanonicalControlFlowTests.EarlyExit,
            "or" => CanonicalControlFlowTests.ShortCircuitOr, "and" => CanonicalControlFlowTests.ShortCircuitAnd,
            "helpers" => CanonicalControlFlowTests.ScalarHelpers, "nested" => CanonicalControlFlowTests.NestedLoops,
            "continuing" => CanonicalControlFlowTests.ContinuingBreakIf, "scope" => CanonicalControlFlowTests.BodyContinuingScope,
            _ => CanonicalControlFlowTests.SignedShift
        };
        var binary = SpirvBinary.Parse(SpirvWriter.Write(WgslReader.Parse(source)));
        CheckRoutes(ReadNative(binary), [input], [first, second], family is "or" or "and" ? [0] : null);
    }

    [Fact]
    public void CapturedIndexRemainsAReadAtItsOriginalPosition()
    {
        var binary = SpirvBinary.Parse(SpirvWriter.Write(WgslReader.Parse(CanonicalControlFlowTests.CapturedIndex)));
        CheckRoutes(ReadNative(binary), [1, 5], [6, 0], [0, 1, 0]);
    }

    [Theory]
    [InlineData("missing")] [InlineData("duplicate")] [InlineData("type")]
    public void MalformedNativePhiIsDiagnosedAtItsOriginalInstruction(string defect)
    {
        var binary = PhiLoopFixture(); var code = binary.Instructions.ToList();
        int index = code.FindIndex(i => (Op)i.Opcode == Op.Phi); var phi = code[index]; var operands = phi.Operands.ToArray();
        if (defect == "missing") operands = operands[..4];
        else if (defect == "duplicate") operands[5] = operands[3];
        else operands[2] = code.First(i => (Op)i.Opcode == Op.ConstantFalse).Operands[1];
        code[index] = phi with { Operands = operands };
        var malformed = SpirvBinary.Parse(new SpirvBinary { Version = binary.Version, Bound = binary.Bound, Instructions = code }.ToBytes());
        var original = malformed.Instructions[index]; var traces = new List<CanonicalPassTrace>();
        var error = Assert.Throws<ShaderException>(() => SpirvReader.ReadBinary(malformed, traces: traces));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage);
        Assert.Equal(new SourceSpan(original.WordOffset * 4, original.WordCount * 4), error.Diagnostic.Span);
        Assert.Empty(traces);
    }

    [Fact]
    public void PendingSpecializationArrayRecordsItsMigrationDeferralWithoutResolvingDefaults()
    {
        var binary = SpirvBinary.Parse(SpirvWriter.Write(WgslReader.Parse(
            "@id(7) override n=3u;var<workgroup> data:array<u32,n>;@compute @workgroup_size(1) fn main(){data[0]=1u;}")));
        var traces = new List<CanonicalPassTrace>(); var deferrals = new List<CanonicalDeferral>();
        var module = SpirvReader.ReadBinary(binary, traces: traces, deferrals: deferrals);
        Assert.Contains(deferrals, d => d.Function == "<SPIR-V>" && d.Feature.Contains("specialization-sized arrays", StringComparison.Ordinal));
        Assert.Empty(traces);
        var array = Assert.IsType<ShaderType.Array>(module.Globals.Single(g => g.Space == AddressSpace.Workgroup).Type);
        Assert.Null(array.Length); Assert.NotNull(array.OverrideLength);
    }
}
