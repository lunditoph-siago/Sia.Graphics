using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class PointerDescriptorIdentityTests
{
    internal static SpirvBinary StorageClassCollisionFixture()
    {
        // %12 is a valid result ID and also the StorageBuffer operand of
        // OpTypePointer. A type declaration must never shadow that variable.
        SpirvInstruction I(Op op, params uint[] operands) => new((ushort)op, operands);
        return new SpirvBinary { Version = 0x10300, Bound = 27, Instructions = [
            I(Op.Capability, 1), I(Op.Capability, 4441), I(Op.Capability, 30),
            I(Op.MemoryModel, 0, 1), I(Op.EntryPoint, 5, 16, 1852399981, 0), I(Op.ExecutionMode, 16, 17, 1, 1, 1),
            I(Op.Decorate, 9, 6, 4), I(Op.Decorate, 10, 2), I(Op.MemberDecorate, 10, 0, 35, 0),
            I(Op.Decorate, 12, 33, 0), I(Op.Decorate, 12, 34, 0),
            I(Op.TypeInt, 1, 32, 0), I(Op.TypeBool, 2), I(Op.TypeVoid, 3), I(Op.TypeFunction, 4, 3),
            I(Op.Constant, 1, 5, 0), I(Op.Constant, 1, 6, 1), I(Op.Constant, 1, 7, 2), I(Op.ConstantTrue, 2, 8),
            I(Op.TypeRuntimeArray, 9, 1), I(Op.TypeStruct, 10, 9), I(Op.TypeArray, 11, 10, 7),
            I(Op.TypePointer, 13, 12, 11), I(Op.TypePointer, 14, 12, 10), I(Op.TypePointer, 15, 12, 1), I(Op.Variable, 13, 12, 12),
            I(Op.Function, 3, 16, 0, 4), I(Op.Label, 17),
            I(Op.AccessChain, 15, 18, 12, 5, 5, 5), I(Op.Load, 1, 19, 18), I(Op.CopyObject, 1, 20, 19),
            I(Op.AccessChain, 14, 21, 12, 19), I(Op.AccessChain, 14, 22, 12, 20),
            I(Op.AccessChain, 15, 23, 21, 5, 5), I(Op.AccessChain, 15, 24, 22, 5, 5),
            I(Op.Select, 15, 25, 8, 23, 24), I(Op.Load, 1, 26, 25), I(Op.Store, 23, 26), I(Op.Return), I(Op.FunctionEnd)
        ] };
    }

    [Fact]
    public void StorageBufferVariableIdMayEqualStorageClassNumber()
    {
        var module = SpirvReader.Parse(StorageClassCollisionFixture().ToBytes());
        ModuleValidator.Validate(module);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
    }

    internal static SpirvBinary Fixture(string kind, string mode, string index = "shared", bool full = false, bool comparison = true, bool privateSlot = false)
    {
        var original = comparison ? PointerComparisonTests.Fixture(kind, mode, "other", 32, privateSlot)
            : PointerDescriptorArrayTests.Fixture(kind, mode, privateSlot);
        var source = PointerSelectionTests.Fixture(kind, false, false);
        var choice = source.Instructions.First(i => (Op)i.Opcode == Op.Select).Operands;
        var code = original.Instructions.ToList(); uint next = original.Bound;
        SpirvInstruction I(Op op, params uint[] a) => new((ushort)op, a);
        uint uintType = code.First(i => (Op)i.Opcode == Op.TypeInt && i.Operands is [_, 32, 0]).Operands[0];
        uint Constant(uint value) => code.First(i => (Op)i.Opcode == Op.Constant && i.Operands is [var t, _, var v] && t == uintType && v == value).Operands[1];
        uint data = code.First(i => (Op)i.Opcode == Op.Decorate && i.Operands is [_, 33, 0]).Operands[0];
        // Comparison fixtures may redirect non-'descriptor' right roots. Retain
        // the independently created descriptor address from the source fixture.
        int right = code.FindIndex(i => (Op)i.Opcode == Op.AccessChain && i.Operands[1] == choice[4]);
        var rightOperands = code[right].Operands.ToArray();
        rightOperands[2] = source.Instructions.First(i => (Op)i.Opcode == Op.AccessChain && i.Operands[1] == choice[4]).Operands[2];
        code[right] = code[right] with { Operands = rightOperands };
        uint Descriptor(uint id)
        {
            var instruction = code.First(i => (Op)i.Opcode == Op.AccessChain && i.Operands[1] == id);
            return instruction.Operands[2] == data ? id : Descriptor(instruction.Operands[2]);
        }
        uint leftDescriptor = Descriptor(choice[3]), rightDescriptor = Descriptor(choice[4]);
        int first = code.FindIndex(i => i.Operands.Length > 1 && i.Operands[1] == leftDescriptor && (Op)i.Opcode == Op.AccessChain);
        uint flag = next++, shared = next++, other = shared;
        var prelude = new List<SpirvInstruction> { I(Op.AccessChain, choice[0], flag, data, Constant(0), Constant(0), Constant(4)) };
        if (index.StartsWith("spec", StringComparison.Ordinal))
        {
            code.Insert(code.FindIndex(i => ((Op)i.Opcode).ToString().StartsWith("Type", StringComparison.Ordinal)), I(Op.Decorate, shared, 1, 191));
            var constants = new List<SpirvInstruction> { I(Op.SpecConstant, uintType, shared, 0) };
            if (index == "spec-derived") { other = next++; constants.Add(I(Op.SpecConstantOp, uintType, other, (uint)Op.IAdd, shared, Constant(0))); }
            code.InsertRange(code.FindIndex(i => (Op)i.Opcode == Op.Function), constants);
        }
        else
        {
            prelude.Add(I(Op.Load, uintType, shared, flag));
            if (index == "copy") { other = next++; prelude.Add(I(Op.CopyObject, uintType, other, shared)); }
            if (index == "independent")
            {
                other = next++; uint changed = next++;
                // Distinct reads of one location can capture different descriptors.
                // The first pointer address is formed before this write.
                int at = code.FindIndex(i => (Op)i.Opcode == Op.AccessChain && i.Operands[1] == rightDescriptor);
                code.InsertRange(at, [I(Op.ISub, uintType, changed, Constant(1), shared), I(Op.Store, flag, changed), I(Op.Load, uintType, other, flag)]);
            }
        }
        first = code.FindIndex(i => (Op)i.Opcode == Op.AccessChain && i.Operands[1] == leftDescriptor);
        code.InsertRange(first, prelude);
        foreach ((uint pointer, uint value) in new[] { (leftDescriptor, shared), (rightDescriptor, other) })
        {
            int at = code.FindIndex(i => (Op)i.Opcode == Op.AccessChain && i.Operands[1] == pointer);
            var a = code[at].Operands.ToArray(); a[^1] = value; code[at] = code[at] with { Operands = a };
        }
        int memory = comparison ? code.FindIndex(i => (Op)i.Opcode == Op.PtrEqual)
            : code.FindIndex(i => (Op)i.Opcode is Op.Load or Op.AtomicIAdd && i.Operands.Length >= 3 && i.Operands[2] == choice[1]);
        uint flipped = next++;
        code.InsertRange(memory, [I(Op.ISub, uintType, flipped, Constant(1), shared), I(Op.Store, flag, flipped)]);
        code.RemoveAll(i => (Op)i.Opcode == Op.Capability && i.Operands[0] is 4441 or 4442);
        code.Insert(1, I(Op.Capability, full ? 4442u : 4441u));
        return new() { Version = comparison ? 0x10400u : original.Version, Bound = next, Instructions = code };
    }

    [Theory]
    [InlineData("descriptor", "select", "shared", false)] [InlineData("descriptor", "phi", "shared", false)]
    [InlineData("descriptor", "slot", "copy", false)] [InlineData("descriptor", "loop", "shared", false)]
    [InlineData("descriptor", "return", "shared", false)] [InlineData("descriptor", "nested", "shared", false)]
    [InlineData("descriptor-atomic", "loop", "copy", false)] [InlineData("descriptor-atomic", "nested", "shared", true)]
    [InlineData("descriptor", "select", "spec", false)] [InlineData("descriptor", "phi", "spec-derived", false)]
    [InlineData("descriptor", "loop", "spec-derived", false)] [InlineData("descriptor", "nested", "spec-derived", false)]
    public void OneDescriptorSnapshotAllowsComparisonAndLimitedSelection(string kind, string mode, string index, bool full)
    {
        var module = SpirvReader.Parse(Fixture(kind, mode, index, full).ToBytes());
        ModuleValidator.Validate(module); ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
    }

    [Theory] [InlineData("select")] [InlineData("phi")] [InlineData("loop")] [InlineData("nested")]
    public void IndependentReadsDoNotProveDescriptorIdentity(string mode)
    {
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(Fixture("descriptor", mode, "independent", true).ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains("potentially aliased", error.Message);
    }

    [Theory] [InlineData("select")] [InlineData("phi")] [InlineData("slot")] [InlineData("loop")] [InlineData("return")] [InlineData("nested")]
    public void DynamicSingleBufferSelectionDoesNotRequirePointerComparisons(string mode)
    {
        var module = SpirvReader.Parse(Fixture("descriptor", mode, "copy", false, false).ToBytes()); ModuleValidator.Validate(module);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
    }

    [Theory] [InlineData("slot")] [InlineData("loop")]
    public void PrivateSlotsRetainInvariantSpecializationDescriptorIdentity(string mode)
    {
        var module = SpirvReader.Parse(Fixture("descriptor", mode, "spec-derived", false, true, true).ToBytes());
        ModuleValidator.Validate(module); ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
    }

    [Theory]
    [InlineData("select", 0u)] [InlineData("select", 1u)]
    [InlineData("loop", 0u)] [InlineData("loop", 1u)]
    [InlineData("return", 0u)] [InlineData("return", 1u)]
    [InlineData("nested", 0u)] [InlineData("nested", 1u)]
    public void DerivedDescriptorDependenciesResolveAfterWgslRoundtrip(string mode, uint value)
    {
        var native = SpirvReader.Parse(Fixture("descriptor", mode, "spec-derived").ToBytes());
        var wgsl = WgslReader.Parse(WgslWriter.Write(native, SpirvCompilationTarget.Default));
        var values = new Dictionary<string, double> { ["191"] = value };
        var resolved = Proc.PipelineConstantResolver.Resolve(wgsl, values);
        Assert.All(resolved.Constants, c => { Assert.False(c.IsOverride); Assert.False(c.IsSpecialization); });
        // Both source indices depend on the same pipeline value, including the
        // derived IAdd-zero operand; neither may retain the default after override.
        string root = native.Constants.Single(c => c.OverrideId == 191).Name;
        string derived = native.Constants.Single(c => c.IsSpecialization).Name;
        Assert.Equal(value, Assert.IsType<IR.Expression.Literal>(resolved.Constants.Single(c => c.Name == root).Value).Value);
        Assert.Equal(value, Assert.IsType<IR.Expression.Literal>(resolved.Constants.Single(c => c.Name == derived).Value).Value);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(wgsl, SpirvCompilationTarget.Default, new() { PipelineConstants = values })));
    }

    [Theory] [InlineData("descriptor", "slot")] [InlineData("descriptor", "loop")] [InlineData("descriptor-atomic", "loop")]
    public void DominatingPrivateSlotWritesRetainThisActivationDescriptorSnapshot(string kind, string mode)
    {
        var module = SpirvReader.Parse(Fixture(kind, mode, "shared", false, true, true).ToBytes());
        ModuleValidator.Validate(module); ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
    }

    internal static SpirvBinary TemporalFixture(bool privateSlot)
    {
        var original = PointerComparisonTests.Fixture("descriptor", "select"); var code = original.Instructions.ToList(); uint next = original.Bound;
        SpirvInstruction I(Op op, params uint[] a) => new((ushort)op, a);
        uint uintType = code.First(i => (Op)i.Opcode == Op.TypeInt && i.Operands is [_, 32, 0]).Operands[0];
        uint signed = code.First(i => (Op)i.Opcode == Op.TypeInt && i.Operands is [_, 32, 1]).Operands[0];
        uint boolean = code.First(i => (Op)i.Opcode == Op.TypeBool).Operands[0];
        uint Constant(uint value) => code.First(i => (Op)i.Opcode == Op.Constant && i.Operands is [var t, _, var v] && t == uintType && v == value).Operands[1];
        uint data = code.First(i => (Op)i.Opcode == Op.Decorate && i.Operands is [_, 33, 0]).Operands[0];
        uint output = code.First(i => (Op)i.Opcode == Op.Decorate && i.Operands is [_, 33, 1]).Operands[0];
        uint pointer = code.First(i => (Op)i.Opcode == Op.Select).Operands[0];
        uint slotType = next++, slot = next++, entry = next++, header = next++, body = next++, init = next++, join = next++, continuing = next++, exit = next++;
        uint flag = next++, iteration = next++, incremented = next++, active = next++, fresh = next++, current = next++, first = next++, previous = next++;
        uint equal = next++, unequal = next++, delta = next++, encoded = next++, equalValue = next++, unequalValue = next++, old = next++, toggled = next++;
        var added = new List<SpirvInstruction> { I(Op.TypePointer, slotType, privateSlot ? 6u : 7u, pointer) };
        if (privateSlot) added.Add(I(Op.Variable, slotType, slot, 6));
        code.InsertRange(code.FindIndex(i => (Op)i.Opcode == Op.Function), added);
        int function = code.FindIndex(i => (Op)i.Opcode == Op.Function), end = code.FindIndex(function, i => (Op)i.Opcode == Op.FunctionEnd);
        var instructions = new List<SpirvInstruction> { I(Op.Label, entry) };
        if (!privateSlot) instructions.Add(I(Op.Variable, slotType, slot, 7));
        instructions.AddRange([I(Op.AccessChain, pointer, flag, data, Constant(0), Constant(0), Constant(4)), I(Op.Branch, header), I(Op.Label, header),
            I(Op.Phi, uintType, iteration, Constant(0), entry, incremented, continuing), I(Op.ULessThan, boolean, active, iteration, Constant(3)),
            I(Op.LoopMerge, exit, continuing, 0), I(Op.BranchConditional, active, body, exit), I(Op.NoLine), I(Op.Label, body),
            I(Op.Load, uintType, fresh, flag), I(Op.AccessChain, pointer, current, data, fresh, Constant(1), Constant(0)),
            I(Op.IEqual, boolean, first, iteration, Constant(0)), I(Op.SelectionMerge, join, 0), I(Op.BranchConditional, first, init, join),
            I(Op.Label, init), I(Op.Store, slot, current), I(Op.Branch, join), I(Op.Label, join), I(Op.Load, pointer, previous, slot),
            I(Op.PtrEqual, boolean, equal, previous, current), I(Op.PtrNotEqual, boolean, unequal, previous, current), I(Op.Bitcast, signed, delta, Constant(0)),
            I(Op.Bitcast, uintType, encoded, delta), I(Op.Select, uintType, equalValue, equal, Constant(1), Constant(0)),
            I(Op.Select, uintType, unequalValue, unequal, Constant(1), Constant(0)), I(Op.Load, uintType, old, current)]);
        var values = new[] { equalValue, unequalValue, encoded, old };
        for (uint p = 0; p < 4; p++)
        {
            uint address = next++; instructions.AddRange([I(Op.AccessChain, pointer, address, output, Constant(0), Constant(p)), I(Op.Store, address, values[p])]);
        }
        instructions.AddRange([I(Op.ISub, uintType, toggled, Constant(1), fresh), I(Op.Store, flag, toggled), I(Op.Branch, continuing), I(Op.Label, continuing),
            I(Op.Store, slot, current), I(Op.IAdd, uintType, incremented, iteration, Constant(1)), I(Op.Branch, header), I(Op.Label, exit), I(Op.Return)]);
        code.RemoveRange(function + 1, end - function - 1); code.InsertRange(function + 1, instructions);
        code.RemoveAll(i => (Op)i.Opcode == Op.Capability && i.Operands[0] is 4441 or 4442); code.Insert(1, I(Op.Capability, 4442));
        if (privateSlot)
        {
            int at = code.FindIndex(i => (Op)i.Opcode == Op.EntryPoint); code[at] = code[at] with { Operands = [.. code[at].Operands, slot] };
        }
        return new() { Version = original.Version, Bound = next, Instructions = code };
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void ReexecutingOneIndexInstructionDoesNotUnifyOldAndCurrentSnapshots(bool privateSlot)
    {
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(TemporalFixture(privateSlot).ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains("potentially aliased", error.Message);
    }

    internal static SpirvBinary RepeatedCallFixture()
    {
        var original = PointerComparisonTests.Fixture("descriptor", "select"); var code = original.Instructions.ToList(); uint next = original.Bound;
        SpirvInstruction I(Op op, params uint[] a) => new((ushort)op, a);
        uint uintType = code.First(i => (Op)i.Opcode == Op.TypeInt && i.Operands is [_, 32, 0]).Operands[0];
        uint boolean = code.First(i => (Op)i.Opcode == Op.TypeBool).Operands[0];
        uint Constant(uint value) => code.First(i => (Op)i.Opcode == Op.Constant && i.Operands is [var t, _, var v] && t == uintType && v == value).Operands[1];
        uint data = code.First(i => (Op)i.Opcode == Op.Decorate && i.Operands is [_, 33, 0]).Operands[0];
        uint output = code.First(i => (Op)i.Opcode == Op.Decorate && i.Operands is [_, 33, 1]).Operands[0];
        uint pointer = code.First(i => (Op)i.Opcode == Op.Select).Operands[0];
        uint signature = next++, helper = next++, helperLabel = next++, helperFlag = next++, descriptor = next++, address = next++;
        code.Insert(code.FindIndex(i => (Op)i.Opcode == Op.Function), I(Op.TypeFunction, signature, pointer));
        int function = code.FindIndex(i => (Op)i.Opcode == Op.Function), end = code.FindIndex(function, i => (Op)i.Opcode == Op.FunctionEnd);
        uint entry = next++, flag = next++, first = next++, previous = next++, changed = next++, second = next++, equal = next++, unequal = next++, equalValue = next++, unequalValue = next++, old = next++;
        var instructions = new List<SpirvInstruction>
        {
            I(Op.Label, entry), I(Op.AccessChain, pointer, flag, data, Constant(0), Constant(0), Constant(4)), I(Op.FunctionCall, pointer, first, helper),
            I(Op.Load, uintType, previous, flag), I(Op.ISub, uintType, changed, Constant(1), previous), I(Op.Store, flag, changed),
            I(Op.FunctionCall, pointer, second, helper), I(Op.PtrEqual, boolean, equal, first, second), I(Op.PtrNotEqual, boolean, unequal, first, second),
            I(Op.Select, uintType, equalValue, equal, Constant(1), Constant(0)), I(Op.Select, uintType, unequalValue, unequal, Constant(1), Constant(0)), I(Op.Load, uintType, old, second)
        };
        var values = new[] { equalValue, unequalValue, Constant(0), old };
        for (uint p = 0; p < 4; p++)
        {
            uint target = next++; instructions.AddRange([I(Op.AccessChain, pointer, target, output, Constant(0), Constant(p)), I(Op.Store, target, values[p])]);
        }
        instructions.Add(I(Op.Return)); code.RemoveRange(function + 1, end - function - 1); code.InsertRange(function + 1, instructions);
        code.AddRange([I(Op.Function, pointer, helper, 0, signature), I(Op.Label, helperLabel),
            I(Op.AccessChain, pointer, helperFlag, data, Constant(0), Constant(0), Constant(4)), I(Op.Load, uintType, descriptor, helperFlag),
            I(Op.AccessChain, pointer, address, data, descriptor, Constant(1), Constant(0)), I(Op.ReturnValue, address), I(Op.FunctionEnd)]);
        return new() { Version = original.Version, Bound = next, Instructions = code };
    }

    [Fact]
    public void RepeatedCallsDoNotReuseCalleeActivationDescriptorSnapshots()
    {
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(RepeatedCallFixture().ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains("potentially aliased", error.Message);
    }
}
