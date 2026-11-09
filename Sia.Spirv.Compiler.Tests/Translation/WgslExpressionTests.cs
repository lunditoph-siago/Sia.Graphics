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
    [InlineData("bitcast<vec2<u32>>(vec2(4294967295, 1)).x", uint.MaxValue)]
    [InlineData("bitcast<u32>(bitcast<f32>(1))", 1u)]
    [InlineData("bitcast<u32>(bitcast<i32>(1.0))", 1065353216u)]
    [InlineData("bitcast<u32>(bitcast<vec2<f16>>(vec2(1, 2)))", 1073757184u)]
    [InlineData("bitcast<u32>(vec2<f16>(bitcast<f16>(1), 0h))", 15360u)]
    public void AbstractBitcastOverloadsChooseTheRequiredConcreteType(string expression, uint expected)
    {
        var module = WgslReader.Parse($$"""
            enable f16;
            const bits = {{expression}};
            const_assert bits == {{expected}}u;
            @compute @workgroup_size(1) fn main() { }
            """);
        Assert.Equal(expected, Assert.IsType<Expression.Literal>(module.Constants.Single(c => c.Name == "bits").Value).Value);
    }
}
