using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.LLVM;

// LLVM integers are signless. Make opcode-implied signedness explicit for
// consumers that select arithmetic from the declared SPIR-V operand type.
internal static class SpirvSignedConversionLowering
{
    public static void Rewrite(string path)
    {
        var binary = SpirvBinary.Parse(File.ReadAllBytes(path));
        var integers = new Dictionary<uint, (uint Width, bool Signed)>();
        var vectors = new Dictionary<uint, (uint Component, uint Size)>();
        foreach (var instruction in binary.Instructions) {
            var a = instruction.Operands;
            if ((Op)instruction.Opcode == Op.TypeInt) integers[a[0]] = (a[1], a[2] != 0);
            if ((Op)instruction.Opcode == Op.TypeVector) vectors[a[0]] = (a[1], a[2]);
        }
        var types = integers.Keys.Concat(vectors.Where(pair => integers.ContainsKey(pair.Value.Component)).Select(pair => pair.Key)).ToHashSet();
        var values = binary.Instructions.Where(i => i.Operands.Length >= 2 && types.Contains(i.Operands[0])
                && (i.Opcode < 19 || i.Opcode > 39))
            .ToDictionary(i => i.Operands[1], i => i.Operands[0]);
        uint nextId = binary.Bound;
        var addedTypes = new List<SpirvInstruction>();
        var output = new List<SpirvInstruction>();
        uint IntegerType(uint type, bool signed) {
            if (vectors.TryGetValue(type, out var vector)) {
                uint component = IntegerType(vector.Component, signed);
                if (component == vector.Component) return type;
                foreach (var pair in vectors) if (pair.Value == (component, vector.Size)) return pair.Key;
                uint id = nextId++;
                vectors.Add(id, (component, vector.Size));
                addedTypes.Add(new((ushort)Op.TypeVector, [id, component, vector.Size]));
                return id;
            }
            var integer = integers[type];
            if (integer.Signed == signed) return type;
            foreach (var pair in integers) if (pair.Value == (integer.Width, signed)) return pair.Key;
            uint result = nextId++;
            integers.Add(result, (integer.Width, signed));
            addedTypes.Add(new((ushort)Op.TypeInt, [result, integer.Width, signed ? 1u : 0u]));
            return result;
        }
        uint Cast(uint value, bool signed) {
            uint type = values[value], target = IntegerType(type, signed);
            if (type == target) return value;
            uint id = nextId++;
            output.Add(new((ushort)Op.Bitcast, [target, id, value]));
            return id;
        }
        foreach (var instruction in binary.Instructions) {
            var op = (Op)instruction.Opcode;
            bool? signed = op switch {
                Op.SNegate or Op.SDiv or Op.SRem or Op.SMod or Op.ShiftRightArithmetic or Op.ConvertSToF
                    or Op.SLessThan or Op.SLessThanEqual or Op.SGreaterThan or Op.SGreaterThanEqual => true,
                Op.UDiv or Op.UMod or Op.ShiftRightLogical or Op.ConvertUToF
                    or Op.ULessThan or Op.ULessThanEqual or Op.UGreaterThan or Op.UGreaterThanEqual => false,
                _ => null
            };
            if (signed is null) { output.Add(instruction); continue; }
            var a = (uint[])instruction.Operands.Clone();
            a[2] = Cast(a[2], signed.Value);
            if (a.Length == 4 && op is not (Op.ShiftRightArithmetic or Op.ShiftRightLogical)) a[3] = Cast(a[3], signed.Value);
            uint originalType = a[0], originalId = a[1];
            if (types.Contains(a[0])) {
                a[0] = IntegerType(a[0], signed.Value);
                if (a[0] != originalType) a[1] = nextId++;
            }
            output.Add(new(instruction.Opcode, a));
            if (a[0] != originalType) output.Add(new((ushort)Op.Bitcast, [originalType, originalId, a[1]]));
        }
        if (nextId == binary.Bound) return;
        int firstFunction = output.FindIndex(i => (Op)i.Opcode == Op.Function);
        output.InsertRange(firstFunction, addedTypes);
        File.WriteAllBytes(path, new SpirvBinary {
            Version = binary.Version, Generator = binary.Generator, Bound = nextId, Instructions = output
        }.ToBytes());
    }
}
