using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class NativeCanonicalPointerPhiTests
{
    internal static SpirvBinary Fixture(bool loop = false, uint iterations = 0, bool repeat = false, bool qualified = false)
    {
        var binary = NativeCanonicalPointerReturnTests.Fixture(nested: true, select: true, repeat: repeat, qualified: qualified);
        var code = binary.Instructions.ToList(); uint next = binary.Bound;
        var pointerTypes = code.Where(i => (Op)i.Opcode == Op.TypePointer).Select(i => i.Operands[0]).ToHashSet();
        int position = code.FindIndex(i => (Op)i.Opcode == Op.Select && pointerTypes.Contains(i.Operands[0]));
        var a = code[position].Operands;
        SpirvInstruction I(Op op, params uint[] operands) => new((ushort)op, operands);
        if (!loop) {
            uint yes = next++, no = next++, merge = next++;
            code.RemoveAt(position); code.InsertRange(position, [I(Op.SelectionMerge, merge, 0),
                I(Op.BranchConditional, a[2], yes, no), I(Op.Label, yes), I(Op.Branch, merge),
                I(Op.Label, no), I(Op.Branch, merge), I(Op.Label, merge), I(Op.Phi, a[0], a[1], a[3], yes, a[4], no)]);
        }
        else {
            uint u32 = code.First(i => (Op)i.Opcode == Op.TypeInt && i.Operands is [_, 32, 0]).Operands[0];
            uint boolean = code.First(i => (Op)i.Opcode == Op.TypeBool).Operands[0];
            uint zero = code.First(i => (Op)i.Opcode == Op.Constant && i.Operands is [_, _, 0] && i.Operands[0] == u32).Operands[1];
            uint one = code.First(i => (Op)i.Opcode == Op.Constant && i.Operands is [_, _, 1] && i.Operands[0] == u32).Operands[1];
            uint limit = next++; code.Insert(code.FindIndex(i => (Op)i.Opcode == Op.Function), I(Op.Constant, u32, limit, iterations)); position++;
            uint entry = code.Take(position).Last(i => (Op)i.Opcode == Op.Label).Operands[0];
            uint p = next++, q = next++, count = next++, nextCount = next++, condition = next++;
            uint header = next++, body = next++, continuing = next++, merge = next++;
            code.RemoveAt(position); code.InsertRange(position, [I(Op.Branch, header), I(Op.Label, header),
                I(Op.Phi, a[0], p, a[3], entry, q, continuing), I(Op.Phi, a[0], q, a[4], entry, p, continuing),
                I(Op.Phi, u32, count, zero, entry, nextCount, continuing), I(Op.ULessThan, boolean, condition, count, limit),
                I(Op.LoopMerge, merge, continuing, 0), I(Op.BranchConditional, condition, body, merge),
                I(Op.Label, body), I(Op.Branch, continuing), I(Op.Label, continuing),
                I(Op.IAdd, u32, nextCount, count, one), I(Op.Branch, header), I(Op.Label, merge), I(Op.Select, a[0], a[1], a[2], p, q)]);
        }
        return new() { Version = binary.Version, Bound = next, Instructions = code };
    }

    [Theory]
    [InlineData(false, 0u, false, 0u, 2u, 34u)] [InlineData(false, 0u, false, 5u, 14u, 26u)]
    [InlineData(false, 0u, true, 0u, 3u, 48u)] [InlineData(false, 0u, true, 5u, 22u, 42u)]
    [InlineData(true, 0u, false, 0u, 2u, 34u)] [InlineData(true, 0u, false, 5u, 14u, 26u)]
    [InlineData(true, 1u, false, 0u, 9u, 16u)] [InlineData(true, 1u, false, 5u, 7u, 34u)]
    [InlineData(true, 2u, false, 0u, 2u, 34u)] [InlineData(true, 3u, false, 5u, 7u, 34u)]
    [InlineData(true, 1u, true, 0u, 17u, 20u)] [InlineData(true, 1u, true, 5u, 8u, 14u)]
    public void OriginalNativePhiEdgesEnterCommonCfgAndRetainParallelSwaps(bool loop, uint iterations, bool repeat, uint input, uint first, uint second)
    {
        var binary = Fixture(loop, iterations, repeat); byte[] original = binary.ToBytes();
        var traces = new List<CanonicalPassTrace>(); var deferrals = new List<CanonicalDeferral>();
        var module = SpirvReader.ReadBinary(binary, traces: traces, deferrals: deferrals);
        Assert.Contains(traces, t => t.Pass == "native-cfg-import" && t.Before.Contains("ptr<", StringComparison.Ordinal));
        Assert.Contains(traces, t => t.Pass == "pointer-return-helper-expansion");
        Assert.DoesNotContain(deferrals, d => d.Function == "<SPIR-V>");
        Assert.Equal(original, binary.ToBytes()); uint[] expected = [first, second];
        Assert.Equal(expected, new CanonicalExecution(module, [input]).Run().Output);
        Assert.Equal(expected, new CanonicalExecution(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)), [input]).Run().Output);
        Assert.Equal(expected, new CanonicalExecution(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)), [input]).Run().Output);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void NativePhiQualifiersKeepExactLoadAndStoreCounts(bool loop)
    {
        var module = SpirvReader.ReadBinary(Fixture(loop, 1, qualified: true));
        var output = SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        Assert.Equal(2, output.Instructions.Count(i => (Op)i.Opcode == Op.Store && i.Operands.Length > 2 && (i.Operands[2] & 1) != 0));
        Assert.Equal(2, output.Instructions.Count(i => (Op)i.Opcode == Op.Load && i.Operands.Length > 3 && (i.Operands[3] & 1) != 0));
    }

    [Theory] [InlineData("extra")] [InlineData("missing")] [InlineData("type")]
    public void NativePhiInputsMustMatchEveryActualPredecessorAndType(string defect)
    {
        var binary = Fixture(); var code = binary.Instructions.ToList();
        int index = code.FindIndex(i => (Op)i.Opcode == Op.Phi); var a = code[index].Operands.ToArray();
        if (defect == "extra") a = [.. a, a[2], code.First(i => (Op)i.Opcode == Op.Label).Operands[0]];
        else if (defect == "missing") a = a[..4];
        else a[2] = code.First(i => (Op)i.Opcode == Op.Constant).Operands[1];
        code[index] = code[index] with { Operands = a };
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(new SpirvBinary { Version = binary.Version, Bound = binary.Bound, Instructions = code }.ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage);
        Assert.Contains(defect == "type" ? "phi value" : "predecessors", error.Message);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void NativePhiStorageBufferRestrictionIsCheckedBeforeTagsEraseItsRoots(bool returned)
    {
        var binary = returned ? PointerReturnTests.Fixture("cross", "branch", false, true, true)
            : PointerPhiTests.Fixture("cross", "branch", false);
        var code = binary.Instructions.Where(i => (Op)i.Opcode != Op.Capability || i.Operands[0] is not (4441 or 4442)).ToList();
        code.Insert(1, new((ushort)Op.Capability, [4441]));
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(new SpirvBinary { Version = binary.Version, Bound = binary.Bound, Instructions = code }.ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage);
        Assert.Contains("one storage buffer structure", error.Message);
    }

    [Fact]
    public void InternalNativeSelectedPlaceDoesNotRelaxPublicWgslValidation()
    {
        var module = WgslReader.Parse("@group(0) @binding(0) var<storage,read_write> data:array<u32>; @compute @workgroup_size(1) fn main(){}");
        var global = module.Globals.Single(); var pointer = new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage);
        Expression Cell(uint index) => new Expression.Access(new Expression.Reference(global.Name,
            new ShaderType.Pointer(global.Type, AddressSpace.Storage)), Expression.U32(index), pointer);
        module.Functions.Single().Body.Statements.Add(new Statement.Store(new Expression.Select(Expression.Bool(true), Cell(0), Cell(1)), Expression.U32(7)));
        ModuleValidator.ValidateNative(module);
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(module));
    }
}
