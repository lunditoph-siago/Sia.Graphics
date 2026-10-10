using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class NativeCanonicalSlotTests
{
    internal static SpirvBinary Fixture(bool loop = false, uint iterations = 0, bool repeat = false, bool select = false)
        => PointerMemoryTests.StorePointers(select
            ? NativeCanonicalPointerReturnTests.Fixture(nested: true, select: true, repeat: repeat)
            : NativeCanonicalPointerPhiTests.Fixture(loop, iterations, repeat), select ? "select" : "native");

    internal static SpirvBinary StandaloneFixture()
    {
        var binary = NativeCanonicalPointerReturnTests.Fixture(select: true);
        var pointers = binary.Instructions.Where(i => (Op)i.Opcode == Op.TypePointer).Select(i => i.Operands[0]).ToHashSet();
        var removed = new HashSet<uint>(); var code = new List<SpirvInstruction>(); bool skip = false;
        foreach (var instruction in binary.Instructions) {
            var op = (Op)instruction.Opcode; var a = instruction.Operands;
            if (op == Op.Function) skip = pointers.Contains(a[0]);
            if (skip) {
                if (op == Op.Label) removed.Add(a[0]);
                else if (op is Op.Function or Op.FunctionParameter or Op.Load or Op.IAdd or Op.Select) removed.Add(a[1]);
                if (op == Op.FunctionEnd) skip = false;
                continue;
            }
            // Replace the two native calls with already evaluated pointer selects;
            // their helper increments are deliberately absent from this fixture.
            code.Add(op == Op.FunctionCall && pointers.Contains(a[0]) ? new((ushort)Op.Select, [a[0], a[1], a[5], a[3], a[4]]) : instruction);
        }
        code.RemoveAll(i => (Op)i.Opcode == Op.Name && removed.Contains(i.Operands[0]));
        return PointerMemoryTests.StorePointers(new SpirvBinary { Version = binary.Version, Bound = binary.Bound, Instructions = code }, "select");
    }

    [Theory]
    [InlineData(false, 0u, false, false, 0u, 2u, 34u)] [InlineData(false, 0u, false, false, 5u, 14u, 26u)]
    [InlineData(false, 0u, true, false, 0u, 3u, 48u)] [InlineData(false, 0u, true, false, 5u, 22u, 42u)]
    [InlineData(true, 0u, false, false, 0u, 2u, 34u)] [InlineData(true, 1u, false, false, 5u, 7u, 34u)]
    [InlineData(true, 2u, false, false, 0u, 2u, 34u)] [InlineData(true, 3u, false, false, 5u, 7u, 34u)]
    [InlineData(true, 1u, true, false, 0u, 17u, 20u)] [InlineData(true, 1u, true, false, 5u, 8u, 14u)]
    [InlineData(false, 0u, false, true, 0u, 2u, 34u)] [InlineData(false, 0u, false, true, 5u, 14u, 26u)]
    public void NativeSlotMemoryUsesSharedPromotionAndCapturedAddresses(bool loop, uint iterations, bool repeat,
        bool select, uint input, uint first, uint second)
    {
        var binary = Fixture(loop, iterations, repeat, select); byte[] original = binary.ToBytes();
        var traces = new List<CanonicalPassTrace>(); var deferrals = new List<CanonicalDeferral>();
        var module = SpirvReader.ReadBinary(binary, traces: traces, deferrals: deferrals);
        Assert.Contains(traces, t => t.Pass == "native-slot-promotion" && t.Before.Contains("allocation-only", StringComparison.Ordinal));
        Assert.Contains(traces, t => t.Pass == "native-cfg-import");
        Assert.DoesNotContain(deferrals, d => d.Function == "<SPIR-V>");
        Assert.Equal(original, binary.ToBytes()); uint[] expected = [first, second];
        Assert.Equal(expected, new CanonicalExecution(module, [input]).Run().Output);
        Assert.Equal(expected, new CanonicalExecution(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)), [input]).Run().Output);
        Assert.Equal(expected, new CanonicalExecution(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)), [input]).Run().Output);
    }

    [Theory] [InlineData(0u, 0u, 34u)] [InlineData(5u, 12u, 24u)]
    public void SlotLoadsWithoutPointerReturnsOrPhisStillUseNativeCfg(uint input, uint first, uint second)
    {
        var binary = StandaloneFixture(); var pointers = binary.Instructions.Where(i => (Op)i.Opcode == Op.TypePointer).Select(i => i.Operands[0]).ToHashSet();
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.Function && pointers.Contains(i.Operands[0]) || (Op)i.Opcode == Op.Phi);
        var traces = new List<CanonicalPassTrace>(); var deferrals = new List<CanonicalDeferral>();
        var module = SpirvReader.ReadBinary(binary, traces: traces, deferrals: deferrals);
        Assert.Contains(traces, t => t.Pass == "native-slot-promotion"); Assert.Contains(traces, t => t.Pass == "native-cfg-import");
        Assert.DoesNotContain(deferrals, d => d.Function == "<SPIR-V>");
        foreach (var candidate in new[] { module, WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)), SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)) })
            Assert.Equal(new uint[] { first, second }, new CanonicalExecution(candidate, [input]).Run().Output);
    }

    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)]
    public void QualifiedAndPrivateSlotsRetainTheirExplicitMigrationRoute(bool privateSlot, bool volatileSlot)
    {
        var binary = PointerMemoryTests.StorePointers(NativeCanonicalPointerPhiTests.Fixture(), "native",
            qualified: !privateSlot, privateSlot: privateSlot, slotVolatile: volatileSlot);
        var traces = new List<CanonicalPassTrace>(); var deferrals = new List<CanonicalDeferral>();
        var module = SpirvReader.ReadBinary(binary, traces: traces, deferrals: deferrals);
        Assert.Contains(deferrals, d => d.Function == "<SPIR-V>" && d.Feature.Contains(privateSlot ? "Private/global" : "qualified", StringComparison.Ordinal));
        Assert.DoesNotContain(traces, t => t.Pass == "native-cfg-import"); ModuleValidator.Validate(module);
        var native = SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        if (volatileSlot) {
            Assert.Contains(native.Instructions, i => (Op)i.Opcode == Op.Load && i.Operands.Length > 3 && (i.Operands[3] & 1) != 0);
            Assert.Contains(native.Instructions, i => (Op)i.Opcode == Op.Store && i.Operands.Length > 2 && (i.Operands[2] & 1) != 0);
            Assert.Contains("volatile memory access", Assert.Throws<ShaderException>(() => WgslWriter.Write(module, SpirvCompilationTarget.Default)).Message);
        }
        else Assert.NotEmpty(WgslWriter.Write(module, SpirvCompilationTarget.Default));
    }

    [Theory] [InlineData("undefined")] [InlineData("type")] [InlineData("operand-count")]
    public void NativeSlotParseErrorsDoNotFallBackToNormalization(string defect)
    {
        var binary = StandaloneFixture(); var code = binary.Instructions.ToList();
        var slotTypes = code.Where(i => (Op)i.Opcode == Op.TypePointer && i.Operands[1] == 7).Select(i => i.Operands[0]).ToHashSet();
        var slots = code.Where(i => (Op)i.Opcode == Op.Variable && slotTypes.Contains(i.Operands[0])).Select(i => i.Operands[1]).ToHashSet();
        // CopyObject gives the first store a slot alias; mutate a direct slot store.
        int position = code.FindIndex(i => (Op)i.Opcode == Op.Store && slots.Contains(i.Operands[0]));
        var a = code[position].Operands;
        code[position] = code[position] with { Operands = defect switch {
            "undefined" => [a[0], binary.Bound],
            "type" => [a[0], code.First(i => (Op)i.Opcode == Op.Constant).Operands[1]],
            _ => [a[0]]
        } };
        var deferrals = new List<CanonicalDeferral>();
        var error = Assert.Throws<ShaderException>(() => SpirvReader.ReadBinary(new SpirvBinary { Version = binary.Version, Bound = binary.Bound, Instructions = code }, deferrals: deferrals));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage);
        Assert.Contains(defect == "undefined" ? "undefined pointer" : defect == "type" ? "pointer type mismatch" : "operand count", error.Message);
        Assert.DoesNotContain(deferrals, d => d.Function == "<SPIR-V>");
    }

    [Fact]
    public void UninitializedPointerLoadsRetainTheLegacyDiagnosticAndExplicitDeferral()
    {
        var binary = StandaloneFixture(); var code = binary.Instructions.ToList();
        var pointers = code.Where(i => (Op)i.Opcode == Op.TypePointer).Select(i => i.Operands[0]).ToHashSet();
        var slotType = code.First(i => (Op)i.Opcode == Op.TypePointer && i.Operands[1] == 7 && pointers.Contains(i.Operands[2])).Operands;
        uint slot = binary.Bound, loaded = slot + 1; int label = code.FindIndex(i => (Op)i.Opcode == Op.Label);
        code.Insert(label + 1, new((ushort)Op.Variable, [slotType[0], slot, 7]));
        int position = label + 2;
        while ((Op)code[position].Opcode == Op.Variable) position++;
        code.Insert(position, new((ushort)Op.Load, [slotType[2], loaded, slot]));
        var input = new SpirvBinary { Version = binary.Version, Bound = loaded + 1, Instructions = code }; byte[] original = input.ToBytes();
        var traces = new List<CanonicalPassTrace>(); var deferrals = new List<CanonicalDeferral>();
        var normalize = typeof(SpirvReader).GetNestedType("PointerPhiLowering", System.Reflection.BindingFlags.NonPublic)!
            .GetMethod("RunWithHelpers")!;
        var legacy = Assert.IsType<ShaderException>(Assert.Throws<System.Reflection.TargetInvocationException>(
            () => normalize.Invoke(null, [input, null, null])).InnerException);
        var error = Assert.Throws<ShaderException>(() => SpirvReader.ReadBinary(input, traces: traces, deferrals: deferrals));
        Assert.Contains(deferrals, d => d.Function == "<SPIR-V>" && d.Feature.Contains("uninitialized", StringComparison.Ordinal));
        Assert.DoesNotContain(traces, t => t.Pass == "native-cfg-import"); Assert.Equal(original, input.ToBytes());
        Assert.Equal(legacy.Diagnostic.Stage, error.Diagnostic.Stage); Assert.Equal(legacy.Message, error.Message);
        Assert.Contains("no initialized finite provenance", error.Message);
    }

    [Fact]
    public void SlotLoadsCannotEraseStorageBufferRootRestrictions()
    {
        var binary = PointerMemoryTests.Fixture("cross", "select", false);
        var code = binary.Instructions.Where(i => (Op)i.Opcode != Op.Capability || i.Operands[0] != 4442).ToList();
        code.Insert(1, new((ushort)Op.Capability, [4441]));
        var deferrals = new List<CanonicalDeferral>();
        var error = Assert.Throws<ShaderException>(() => SpirvReader.ReadBinary(new SpirvBinary { Version = binary.Version, Bound = binary.Bound, Instructions = code }, deferrals: deferrals));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains("one storage buffer structure", error.Message);
        Assert.DoesNotContain(deferrals, d => d.Function == "<SPIR-V>");
    }
}
