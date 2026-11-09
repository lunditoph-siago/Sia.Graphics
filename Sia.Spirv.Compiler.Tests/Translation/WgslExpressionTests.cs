using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Tests.Translation;

public sealed class WgslExpressionTests
{
    [Fact]
    public void DiscardedAbstractConstantsDoNotRequireI32Representability()
    {
        byte[] binary = ShaderTranslator.WgslToSpirv("""
            const a = 1 << 30u;
            const b = a << 10u;
            const c = 5000000000 << 3u;
            @compute @workgroup_size(1) fn main() { _ = b; _ = c; }
            """, SpirvCompilationTarget.Default);
        Assert.Single(SpirvBinary.Parse(binary).Instructions, i => i.Opcode == (ushort)Op.EntryPoint);
        WgslReader.Parse(ShaderTranslator.SpirvToWgsl(binary, SpirvCompilationTarget.Default));
    }

    [Fact]
    public void DiscardedInvalidAbstractExpressionsStillReportErrors()
    {
        var error = Assert.Throws<ShaderException>(() => WgslReader.Parse("@compute @workgroup_size(1) fn main() { _ = 1 / 0; }"));
        Assert.Equal(DiagnosticStage.Validation, error.Diagnostic.Stage);
        Assert.Contains("divides by zero", error.Diagnostic.Message);
    }

    [Theory]
    [InlineData("bitcast<u32>(4294967295)", uint.MaxValue)]
    [InlineData("bitcast<u32>(bitcast<i32>(1.0))", 1065353216u)]
    public void AbstractBitcastOverloadsChooseTheRequiredConcreteType(string expression, uint expected)
    {
        var module = WgslReader.Parse($$"""
            const bits = {{expression}};
            const_assert bits == {{expected}}u;
            @compute @workgroup_size(1) fn main() { }
            """);
        Assert.Equal(expected, Assert.IsType<Expression.Literal>(module.Constants.Single(c => c.Name == "bits").Value).Value);
    }

    [Fact]
    public void InvalidSourceIsRejectedBeforeDeadValueElimination()
    {
        var error = Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv(
            "@compute @workgroup_size(1) fn main() { let unused = vec2<u32>(1u)[3]; }", SpirvCompilationTarget.Default));
        Assert.Contains("out of bounds", error.Diagnostic.Message);
    }
}
