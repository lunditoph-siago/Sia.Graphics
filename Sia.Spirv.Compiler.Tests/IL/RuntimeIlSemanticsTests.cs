using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Tests.IL;

public sealed class RuntimeIlSemanticsTests
{
    [Theory]
    [MemberData(nameof(IlSemanticCases.StorageResults), MemberType = typeof(IlSemanticCases))]
    public void RuntimeIlRoundtripPreservesExpectedMemory(string name, uint first, uint second)
    {
        byte[] binary = SpirvTestAssembly.CompileRuntime(typeof(MemorySemanticsShaders), name);
        string wgsl = ShaderTranslator.SpirvToWgsl(binary, SpirvCompilationTarget.Default);
        Assert.Equal(new[] { first, second }, WgslFixtureInterpreter.Execute(wgsl).Memory);
    }

    [Theory]
    [InlineData(nameof(IntegerShaders.DivideByEight), (int)Op.ShiftRightLogical)]
    [InlineData(nameof(IntegerShaders.RemainderByThree), (int)Op.SDiv)]
    [InlineData(nameof(IntegerShaders.Shift), (int)Op.ShiftLeftLogical)]
    public void RuntimeIlUsesTheSameIntegerLowering(string method, int operation)
    {
        var binary = SpirvBinary.Parse(SpirvTestAssembly.CompileRuntime(typeof(IntegerShaders), method));
        Assert.Single(binary.Instructions, i => i.Opcode == (ushort)operation);
        Assert.Single(binary.Instructions, i => i.Opcode == (ushort)Op.FunctionCall);
        if (method == nameof(IntegerShaders.Shift))
            Assert.Single(binary.Instructions, i => i.Opcode == (ushort)Op.BitwiseAnd);
    }

    [SpirvToolchainTheory]
    [Trait("Category", "Toolchain")]
    [InlineData(nameof(IntegerShaders.DivideByEight))]
    [InlineData(nameof(IntegerShaders.RemainderByThree))]
    [InlineData(nameof(IntegerShaders.Shift))]
    public void RuntimeIntegerOutputsPassSpirvValidation(string method)
        => SpirvTestToolchain.Validate(SpirvTestAssembly.CompileRuntime(typeof(IntegerShaders), method));
}
