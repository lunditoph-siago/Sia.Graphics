using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class NativeCanonicalSlotHelperTests
{
    internal static SpirvBinary Fixture(bool nested = false, bool repeat = false, bool qualified = false)
    {
        var binary = NativeCanonicalSlotTests.Fixture(repeat: repeat);
        var code = binary.Instructions.ToList(); uint next = binary.Bound;
        var pointers = code.Where(i => (Op)i.Opcode == Op.TypePointer).ToDictionary(i => i.Operands[0], i => i.Operands);
        var slots = code.Where(i => (Op)i.Opcode == Op.Variable && pointers.ContainsKey(pointers[i.Operands[0]][2]))
            .ToDictionary(i => i.Operands[1], i => i.Operands[0]);
        uint voidType = code.First(i => (Op)i.Opcode == Op.TypeVoid).Operands[0];
        var types = new List<SpirvInstruction>(); var helpers = new List<SpirvInstruction>();
        var ids = new Dictionary<uint, uint>();
        SpirvInstruction I(Op op, params uint[] a) => new((ushort)op, a);
        uint Helper(uint slotType)
        {
            if (ids.TryGetValue(slotType, out uint id)) return id;
            uint signature = next++, function = next++, slot = next++, value = next++, label = next++;
            uint pointer = pointers[slotType][2];
            types.Add(I(Op.TypeFunction, signature, voidType, slotType, pointer));
            helpers.AddRange([I(Op.Function, voidType, function, 0, signature), I(Op.FunctionParameter, slotType, slot),
                I(Op.FunctionParameter, pointer, value), I(Op.Label, label),
                qualified ? I(Op.Store, slot, value, 1) : I(Op.Store, slot, value), I(Op.Return), I(Op.FunctionEnd)]);
            if (nested) {
                uint wrapper = next++, p = next++, q = next++, entry = next++, call = next++;
                helpers.AddRange([I(Op.Function, voidType, wrapper, 0, signature), I(Op.FunctionParameter, slotType, p),
                    I(Op.FunctionParameter, pointer, q), I(Op.Label, entry), I(Op.FunctionCall, voidType, call, function, p, q),
                    I(Op.Return), I(Op.FunctionEnd)]);
                function = wrapper;
            }
            ids.Add(slotType, function); return function;
        }
        var output = new List<SpirvInstruction>();
        foreach (var instruction in code) {
            var a = instruction.Operands;
            if ((Op)instruction.Opcode == Op.Store && slots.TryGetValue(a[0], out uint slotType))
                output.Add(I(Op.FunctionCall, voidType, next++, Helper(slotType), a[0], a[1]));
            else output.Add(instruction);
        }
        output.InsertRange(output.FindIndex(i => (Op)i.Opcode == Op.Function), types.Concat(helpers));
        return new() { Version = binary.Version, Generator = binary.Generator, Bound = next, Instructions = output };
    }

    [Theory]
    [InlineData(false, false, 0u, 2u, 34u)] [InlineData(false, false, 5u, 14u, 26u)]
    [InlineData(true, false, 0u, 2u, 34u)] [InlineData(true, false, 5u, 14u, 26u)]
    [InlineData(false, true, 0u, 3u, 48u)] [InlineData(false, true, 5u, 22u, 42u)]
    [InlineData(true, true, 0u, 3u, 48u)] [InlineData(true, true, 5u, 22u, 42u)]
    public void HelperSlotWritesEnterTheSharedCfgBeforePromotion(bool nested, bool repeat, uint input, uint first, uint second)
    {
        var binary = Fixture(nested, repeat); byte[] original = binary.ToBytes();
        var traces = new List<CanonicalPassTrace>(); var deferrals = new List<CanonicalDeferral>();
        var module = SpirvReader.ReadBinary(binary, traces: traces, deferrals: deferrals);
        Assert.Contains(traces, t => t.Pass == "native-slot-helper-expansion");
        Assert.Contains(traces, t => t.Pass == "native-cfg-import");
        Assert.DoesNotContain(deferrals, d => d.Function == "<SPIR-V>");
        Assert.Equal(original, binary.ToBytes()); ModuleValidator.ValidateNative(module);
        Assert.Equal(new[] { first, second }, new CanonicalExecution(module, [input]).Run().Output);
        Assert.Equal(new[] { first, second }, new CanonicalExecution(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)), [input]).Run().Output);
        Assert.Equal(new[] { first, second }, new CanonicalExecution(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)), [input]).Run().Output);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void QualifiedHelperSlotWritesUseNativeControlFlow(bool nested)
    {
        var deferrals = new List<CanonicalDeferral>(); var traces = new List<CanonicalPassTrace>();
        var module = SpirvReader.ReadBinary(Fixture(nested, qualified: true), traces: traces, deferrals: deferrals);
        Assert.DoesNotContain(deferrals, d => d.Function == "<SPIR-V>");
        Assert.Contains(traces, t => t.Pass == "native-cfg-import");
        var output = SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        Assert.Contains(output.Instructions, i => (Op)i.Opcode == Op.Store && i.Operands.Length > 2 && (i.Operands[2] & 1) != 0);
    }
}
