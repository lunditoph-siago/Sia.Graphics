using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class PointerReturnTests
{
    internal static SpirvBinary Fixture(string kind, string mode, bool slot, bool nested, bool early, bool privateSlot = false, bool qualified = false, uint iterations = 3, uint version = 0)
    {
        var binary = slot ? PointerMemoryTests.Fixture(kind, mode, qualified, privateSlot, iterations, false, version)
            : mode == "select" ? PointerSelectionTests.Fixture(kind, false, qualified) : PointerPhiTests.Fixture(kind, mode, qualified, iterations);
        var code = binary.Instructions.ToList(); uint next = binary.Bound;
        SpirvInstruction I(Op op, params uint[] a) => new((ushort)op, a);
        var pointers = code.Where(i => (Op)i.Opcode == Op.TypePointer).Select(i => i.Operands[0]).ToHashSet();
        uint boolean = code.First(i => (Op)i.Opcode == Op.TypeBool).Operands[0];
        var added = new List<SpirvInstruction>(); var helpers = new List<SpirvInstruction>();
        var functions = new Dictionary<(uint Pointer, uint Slot), uint>();
        uint Root(uint id) => code.FirstOrDefault(i => (Op)i.Opcode == Op.CopyObject && i.Operands[1] == id) is { } copy ? Root(copy.Operands[2]) : id;
        foreach (var original in code.Where(i => (Op)i.Opcode == (slot ? Op.Load : Op.Select) && pointers.Contains(i.Operands[0])).ToArray())
        {
            var a = original.Operands; uint slotType = slot ? code.Single(i => (Op)i.Opcode == Op.Variable && i.Operands[1] == Root(a[2])).Operands[0] : 0;
            if (!functions.TryGetValue((a[0], slotType), out uint function))
            {
                uint signature = next++, p = next++, q = next++, condition = next++, entry = next++, value = next++;
                function = next++; added.Add(I(Op.TypeFunction, slot ? [signature, a[0], slotType] : [signature, a[0], a[0], a[0], boolean]));
                helpers.AddRange([I(Op.Function, a[0], function, 0, signature), I(Op.FunctionParameter, slot ? slotType : a[0], p)]);
                if (!slot) helpers.AddRange([I(Op.FunctionParameter, a[0], q), I(Op.FunctionParameter, boolean, condition)]);
                helpers.Add(I(Op.Label, entry));
                if (slot) helpers.AddRange([I(Op.Load, [a[0], value, p, .. a[3..]]), I(Op.ReturnValue, value)]);
                else if (early)
                {
                    uint left = next++, right = next++, merge = next++;
                    helpers.AddRange([I(Op.SelectionMerge, merge, 0), I(Op.BranchConditional, condition, left, right),
                        I(Op.Label, left), I(Op.ReturnValue, p), I(Op.Label, right), I(Op.ReturnValue, q), I(Op.Label, merge), I(Op.Unreachable)]);
                }
                else helpers.AddRange([I(Op.Select, a[0], value, condition, p, q), I(Op.ReturnValue, value)]);
                helpers.Add(I(Op.FunctionEnd));
                if (nested)
                {
                    uint wrapper = next++, wp = next++, wq = next++, wc = next++, label = next++, result = next++;
                    helpers.AddRange([I(Op.Function, a[0], wrapper, 0, signature), I(Op.FunctionParameter, slot ? slotType : a[0], wp)]);
                    if (!slot) helpers.AddRange([I(Op.FunctionParameter, a[0], wq), I(Op.FunctionParameter, boolean, wc)]);
                    helpers.AddRange([I(Op.Label, label), I(Op.FunctionCall, slot ? [a[0], result, function, wp] : [a[0], result, function, wp, wq, wc]),
                        I(Op.ReturnValue, result), I(Op.FunctionEnd)]); function = wrapper;
                }
                functions.Add((a[0], slotType), function);
            }
            code[code.IndexOf(original)] = I(Op.FunctionCall, slot ? [a[0], a[1], function, Root(a[2])] : [a[0], a[1], function, a[3], a[4], a[2]]);
        }
        code.InsertRange(code.FindIndex(i => (Op)i.Opcode == Op.Function), added.Concat(helpers));
        return new() { Version = version == 0 ? binary.Version : version, Generator = binary.Generator, Bound = next, Instructions = code };
    }

    [Theory]
    [InlineData("scalar", "select", false, false, false, false)] [InlineData("array", "select", false, true, true, false)]
    [InlineData("atomic", "select", false, true, false, false)] [InlineData("workgroup", "select", false, false, true, false)]
    [InlineData("cross", "select", false, true, true, false)] [InlineData("mixed-struct", "swap", false, true, true, false)]
    [InlineData("scalar", "local", true, false, false, false)] [InlineData("array", "local", true, true, false, true)]
    [InlineData("atomic", "swap", true, true, false, false)] [InlineData("workgroup", "swap", true, false, false, true)]
    [InlineData("mixed-struct", "local", true, true, false, true)] [InlineData("scalar", "swap", true, true, false, true)]
    public void ReturnedAddressesRetainBranchLocalIndicesAndCallSnapshots(string kind, string mode, bool slot, bool nested, bool early, bool privateSlot)
    {
        var module = SpirvReader.Parse(Fixture(kind, mode, slot, nested, early, privateSlot).ToBytes()); ModuleValidator.Validate(module);
        Assert.DoesNotContain(module.Functions, f => f.ReturnType is ShaderType.Pointer);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
    }

    [Theory] [InlineData("scalar", 0u, "variable-pointer capability")] [InlineData("workgroup", 4441u, "full VariablePointers")]
    public void ReturnedPointersStillRequireTheirDeclaredCapability(string kind, uint capability, string message)
    {
        var binary = Fixture(kind, "select", false, true, true); var code = binary.Instructions.ToList();
        code.RemoveAll(i => (Op)i.Opcode == Op.Capability && i.Operands[0] is 4441 or 4442);
        if (capability != 0) code.Insert(1, new((ushort)Op.Capability, [capability]));
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(new SpirvBinary { Version = binary.Version, Bound = binary.Bound, Instructions = code }.ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains(message, error.Message);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void MalformedPointerReturnsRetainParseDiagnostics(bool wrongType)
    {
        var binary = Fixture("scalar", "select", false, false, true); var code = binary.Instructions.ToList();
        int index = code.FindIndex(i => (Op)i.Opcode == Op.ReturnValue);
        uint scalar = code.First(i => (Op)i.Opcode == Op.Constant && i.Operands.Length == 3).Operands[1];
        code[index] = code[index] with { Operands = wrongType ? [scalar] : [] };
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(new SpirvBinary { Version = binary.Version, Bound = binary.Bound, Instructions = code }.ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains(wrongType ? "undefined pointer" : "return operands", error.Message);
    }
}
