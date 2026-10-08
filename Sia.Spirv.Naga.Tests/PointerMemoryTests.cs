using Sia.Spirv.Naga.Back;
using Sia.Spirv.Naga.Front;
using Sia.Spirv.Naga.Spirv;
using Sia.Spirv.Naga.Valid;

namespace Sia.Spirv.Naga.Tests;

public class PointerMemoryTests
{
    internal static SpirvBinary Fixture(string kind, string mode, bool qualified, bool privateSlot = false, uint iterations = 3, bool slotVolatile = false, uint version = 0)
    {
        var binary = mode == "select" ? PointerSelectionTests.Fixture(kind, false, qualified)
            : PointerPhiTests.Fixture(kind, mode == "hybrid" ? "nested" : mode, qualified, iterations);
        var code = binary.Instructions.ToList(); uint next = binary.Bound;
        SpirvInstruction I(Op op, params uint[] operands) => new((ushort)op, operands);
        var pointerTypes = code.Where(i => (Op)i.Opcode == Op.TypePointer).Select(i => i.Operands[0]).ToHashSet();
        var choices = code.Where(i => (Op)i.Opcode == (mode == "select" ? Op.Select : Op.Phi) && pointerTypes.Contains(i.Operands[0])).ToArray();
        if (mode == "hybrid") choices = choices.Take(1).ToArray();
        var variables = new List<SpirvInstruction>(); var types = new List<SpirvInstruction>();
        var pending = new Dictionary<uint, List<SpirvInstruction>>();
        var loads = new Dictionary<SpirvInstruction, SpirvInstruction>();
        foreach (var choice in choices)
        {
            var a = choice.Operands; uint type = next++, slot = next++, alias = next++;
            types.Add(I(Op.TypePointer, type, privateSlot ? 6u : 7u, a[0]));
            variables.Add(I(Op.Variable, type, slot, privateSlot ? 6u : 7u));
            if (mode == "select")
            {
                uint selected = next++; int index = code.IndexOf(choice);
                // Overwrite the slot after loading: the old loaded address must
                // retain its root and index, even after subsequent slot writes.
                code.RemoveAt(index); code.InsertRange(index, [I(Op.Select, a[0], selected, a[2], a[3], a[4]),
                    I(Op.CopyObject, type, alias, slot), I(Op.Store, alias, selected), I(Op.Load, a[0], a[1], slot), I(Op.Store, slot, a[4])]);
            }
            else
            {
                for (int p = 2; p < a.Length; p += 2)
                {
                    if (!pending.TryGetValue(a[p + 1], out var stores)) pending.Add(a[p + 1], stores = []);
                    stores.Add(I(Op.Store, slot, a[p]));
                }
                loads.Add(choice, I(Op.Load, a[0], a[1], slot));
            }
        }
        if (mode != "select")
        {
            var output = new List<SpirvInstruction>(); uint label = 0; var phiLoads = new List<SpirvInstruction>();
            foreach (var instruction in code)
            {
                Op op = (Op)instruction.Opcode;
                if (op is not (Op.Phi or Op.Line or Op.NoLine or Op.Nop)) { output.AddRange(phiLoads); phiLoads.Clear(); }
                if (op == Op.Label) label = instruction.Operands[0];
                if (op is Op.Branch or Op.BranchConditional)
                    if (pending.TryGetValue(label, out var stores))
                    {
                        int insert = output.Count;
                        if (insert != 0 && (Op)output[^1].Opcode is Op.LoopMerge or Op.SelectionMerge) insert--;
                        output.InsertRange(insert, stores);
                    }
                if (loads.TryGetValue(instruction, out var load)) phiLoads.Add(load); else output.Add(instruction);
            }
            code = output;
        }
        code.InsertRange(code.FindIndex(i => (Op)i.Opcode == Op.Function), types);
        int variablePosition = privateSlot ? code.FindIndex(i => (Op)i.Opcode == Op.Function)
            : code.FindIndex(i => (Op)i.Opcode == Op.Label) + 1;
        code.InsertRange(variablePosition, variables);
        var slotIds = variables.Select(i => i.Operands[1]).ToHashSet();
        if (qualified || slotVolatile)
            code = code.Select(i => ((Op)i.Opcode == Op.Store && slotIds.Contains(i.Operands[0])
                || (Op)i.Opcode == Op.Load && pointerTypes.Contains(i.Operands[0]))
                ? i with { Operands = [.. i.Operands, slotVolatile ? 7u : 6u, 4] } : i).ToList();
        if (version >= 0x10400)
        {
            var globals = code.Where(i => (Op)i.Opcode == Op.Variable && i.Operands[2] != 7).Select(i => i.Operands[1]).ToArray();
            code = code.Select(i =>
            {
                if ((Op)i.Opcode != Op.EntryPoint) return i;
                _ = SpirvBinary.ReadString(i.Operands.AsSpan(2), out int words);
                var listed = i.Operands[(2 + words)..].ToHashSet();
                return i with { Operands = [.. i.Operands, .. globals.Where(listed.Add)] };
            }).ToList();
        }
        return new() { Version = version == 0 ? binary.Version : version, Generator = binary.Generator, Bound = next, Instructions = code };
    }

    [Theory]
    [InlineData("scalar", "select", false, false)] [InlineData("scalar", "local", true, false)]
    [InlineData("array", "nested", true, false)] [InlineData("vector", "branch", false, false)]
    [InlineData("mixed-struct", "local", true, true)] [InlineData("atomic", "swap", true, false)]
    [InlineData("workgroup", "swap", false, false)] [InlineData("same", "swap", true, true)]
    [InlineData("scalar", "hybrid", true, true)] [InlineData("array", "hybrid", false, false)]
    public void StoredPointersRetainLoadedAddressesAcrossSlotWrites(string kind, string mode, bool qualified, bool privateSlot)
    {
        var module = SpirvReader.Parse(Fixture(kind, mode, qualified, privateSlot).ToBytes());
        ModuleValidator.Validate(module);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Theory] [InlineData("scalar", 0u)] [InlineData("workgroup", 4441u)]
    public void PointerLoadsEnforceTheirDeclaredCapability(string kind, uint capability)
    {
        var binary = Fixture(kind, "select", false);
        var code = binary.Instructions.Where(i => (Op)i.Opcode != Op.Capability || i.Operands[0] is not (4441 or 4442)).ToList();
        if (capability != 0) code.Insert(1, new((ushort)Op.Capability, [capability]));
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(new SpirvBinary { Version = binary.Version, Bound = binary.Bound, Instructions = code }.ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage);
        Assert.Contains(capability == 0 ? "variable-pointer capability" : "full VariablePointers", error.Message);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void VolatilePointerSlotMemoryRetainsNativeFlagsAndExplicitWgslDiagnostic(bool privateSlot)
    {
        var module = SpirvReader.Parse(Fixture("scalar", "local", false, privateSlot, 1, true).ToBytes());
        ModuleValidator.Validate(module);
        var native = SpirvBinary.Parse(SpirvWriter.Write(module));
        Assert.Contains(native.Instructions, i => (Op)i.Opcode == Op.Load && i.Operands.Length == 5 && i.Operands[3] == 7);
        Assert.Contains(native.Instructions, i => (Op)i.Opcode == Op.Store && i.Operands.Length == 4 && i.Operands[2] == 7);
        ModuleValidator.Validate(SpirvReader.Parse(native.ToBytes()));
        Assert.Contains("volatile memory access", Assert.Throws<ShaderException>(() => WgslWriter.Write(module)).Message);
    }

    [Fact]
    public void UndefinedStoredPointerRetainsAParseDiagnosticWithoutAnyPhi()
    {
        var binary = MatrixHelperTests.PointerProducerFixture("load"); var code = binary.Instructions.ToList();
        int store = code.FindIndex(i => (Op)i.Opcode == Op.Store);
        code[store] = code[store] with { Operands = [code[store].Operands[0], binary.Bound - 1] };
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(new SpirvBinary { Version = binary.Version, Bound = binary.Bound, Instructions = code }.ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains("undefined pointer", error.Message);
    }

    [Theory] [InlineData(0x10400u)] [InlineData(0x10500u)]
    public void NewPrivateSlotStateIsListedInModernNativeEntryInterfaces(uint version)
    {
        var binary = Fixture("scalar", "local", true, true, 1, false, version);
        var normalized = (SpirvBinary)typeof(SpirvReader).GetNestedType("PointerPhiLowering", System.Reflection.BindingFlags.NonPublic)!
            .GetMethod("Run")!.Invoke(null, [binary])!;
        var globals = normalized.Instructions.Where(i => (Op)i.Opcode == Op.Variable && i.Operands[2] == 6).Select(i => i.Operands[1]).ToArray();
        Assert.NotEmpty(globals);
        foreach (var entry in normalized.Instructions.Where(i => (Op)i.Opcode == Op.EntryPoint))
        {
            _ = SpirvBinary.ReadString(entry.Operands.AsSpan(2), out int words);
            foreach (uint global in globals) Assert.Contains(global, entry.Operands[(2 + words)..]);
        }
        var module = SpirvReader.Parse(binary.ToBytes()); ModuleValidator.Validate(module);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }
}
