using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class SpirvWriter
{
    private sealed partial class Writer
    {
        private readonly Dictionary<SpirvSpecializationValue, uint> specializationValues = new(ReferenceEqualityComparer.Instance);
        private uint SpecializationLength(string name) => EmitSpecialization(EntryAbi.ArrayLengths[name]);
        private uint SpecializationLengthU32(string name) => EmitSpecialization(EntryAbi.ArrayLengthsU32[name]);
        private uint Specialization(Expression expression) => EmitSpecialization(EntryAbi.Specializations[expression]);
        private uint EmitSpecialization(SpirvSpecializationValue value)
        {
            if (specializationValues.TryGetValue(value, out uint known)) return known;
            uint result;
            switch (value) {
                case SpirvSpecializationValue.Constant constant: result = Constant(constant.Value); break;
                case SpirvSpecializationValue.Reference reference:
                    if (!globals.TryGetValue(reference.Name, out var symbol) || symbol.Place) throw Error("Specialization reference must denote a preceding constant.");
                    result = symbol.Id; break;
                case SpirvSpecializationValue.Instruction instruction:
                    var operands = instruction.Values.Select(EmitSpecialization).Concat(instruction.Literals).ToArray();
                    result = SpecializationInstruction(instruction.Operation is null ? Op.SpecConstantComposite : Op.SpecConstantOp, instruction.Type,
                        instruction.Operation is { } op ? new[] { (uint)op }.Concat(operands).ToArray() : operands); break;
                default: throw Error("Unknown prepared specialization value.");
            }
            specializationValues.Add(value, result); return result;
        }
        private uint SpecializationInstruction(Op instruction, ShaderType type, uint[] operands)
        {
            uint typeId = Type(type); string key = $"{(uint)instruction}:{typeId}:" + string.Join(',', operands);
            if (constantIds.TryGetValue(key, out uint known)) return known;
            uint id = Id(); declarations.Add(I(instruction, new[] { typeId, id }.Concat(operands).ToArray()));
            constantIds.Add(key, id); return id;
        }
    }
}
