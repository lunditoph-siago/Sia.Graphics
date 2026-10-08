using Sia.Spirv.Compiler.LLVM;
using Sia.Spirv.Compiler.Translation.Spirv;
using static Sia.Spirv.Compiler.Translation.Tests.SpirvTests;

namespace Sia.Spirv.Compiler.Tests;

public class SpirvNormalizationTests
{
    [Fact]
    public void SignedVectorOperationsKeepBooleanAndFloatResultTypes()
    {
        var binary = Binary(I(Op.TypeInt, 1, 32, 0), I(Op.TypeBool, 2), I(Op.TypeFloat, 3, 32),
            I(Op.TypeVector, 4, 1, 2), I(Op.TypeVector, 5, 2, 2), I(Op.TypeVector, 6, 3, 2),
            I(Op.TypeVoid, 7), I(Op.TypeFunction, 8, 7, 4), I(Op.Function, 7, 10, 0, 8),
            I(Op.FunctionParameter, 4, 11), I(Op.Label, 12),
            I(Op.SGreaterThan, 5, 13, 11, 11), I(Op.ConvertSToF, 6, 14, 11),
            I(Op.ShiftRightArithmetic, 4, 15, 11, 11), I(Op.Return), I(Op.FunctionEnd));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".spv");
        try {
            File.WriteAllBytes(path, binary.ToBytes());
            SpirvSignedConversionLowering.Rewrite(path);
            var result = SpirvBinary.Parse(File.ReadAllBytes(path));
            Assert.Equal(5u, Assert.Single(result.Instructions, i => (Op)i.Opcode == Op.SGreaterThan).Operands[0]);
            Assert.Equal(6u, Assert.Single(result.Instructions, i => (Op)i.Opcode == Op.ConvertSToF).Operands[0]);
            var signedInt = Assert.Single(result.Instructions, i => (Op)i.Opcode == Op.TypeInt && i.Operands[2] == 1).Operands[0];
            var signedVector = Assert.Single(result.Instructions, i => (Op)i.Opcode == Op.TypeVector && i.Operands[1] == signedInt).Operands[0];
            Assert.Equal(signedVector, Assert.Single(result.Instructions, i => (Op)i.Opcode == Op.ShiftRightArithmetic).Operands[0]);
            Assert.Contains(result.Instructions, i => (Op)i.Opcode == Op.Bitcast && i.Operands[0] == 4 && i.Operands[1] == 15);
            var once = File.ReadAllBytes(path);
            SpirvSignedConversionLowering.Rewrite(path);
            Assert.Equal(once, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }
}
