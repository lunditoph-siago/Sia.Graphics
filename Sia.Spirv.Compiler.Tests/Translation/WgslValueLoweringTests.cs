using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Tests.Translation;

public sealed class WgslValueLoweringTests
{
    [Fact]
    public void StraightLineArithmeticDoesNotMaterializeEverySsaValue()
    {
        string result = ShaderTranslation.NormalizeWgsl("""
            @group(0) @binding(0) var<storage, read_write> data: array<u32>;
            @compute @workgroup_size(1) fn main() {
                let x = data[0];
                let a = x + 1u;
                let b = a * 2u;
                data[1] = b;
            }
            """);
        Assert.True(WgslLexer.Tokenize(result).Count(t => t.Text == "let") <= 2, result);
        WgslAssertions.NoMutableLocals(result);
    }

    [Theory]
    [InlineData("root", false)]
    [InlineData("branch", true)]
    [InlineData("before", true)]
    [InlineData("loop", true)]
    public void SingleAssignmentPromotionRespectsLexicalScopeAndInitialization(string kind, bool retainsStorage)
    {
        var module = new Module();
        var function = new ShaderFunction("calculate") { ReturnType = ShaderType.U32 };
        function.Arguments.Add(new("condition", ShaderType.Bool)); module.Functions.Add(function);
        var place = new Expression.Reference("value", new ShaderType.Pointer(ShaderType.U32, AddressSpace.Function));
        function.Body.Statements.Add(new Statement.Declare("value", ShaderType.U32, null));
        var assignment = new Statement.Store(place, Expression.U32(9));
        Expression returned = new Expression.Load(place);
        if (kind == "root") function.Body.Statements.Add(assignment);
        else if (kind == "branch") {
            var accept = new Block(); accept.Statements.Add(assignment);
            function.Body.Statements.Add(new Statement.If(new Expression.Reference("condition", ShaderType.Bool), accept, new()));
        }
        else if (kind == "before") {
            function.Body.Statements.Add(new Statement.Declare("old", ShaderType.U32, returned, false));
            returned = new Expression.Reference("old", ShaderType.U32); function.Body.Statements.Add(assignment);
        }
        else {
            var body = new Block(); var exit = new Block(); exit.Statements.Add(new Statement.Break());
            body.Statements.Add(new Statement.If(new Expression.Reference("condition", ShaderType.Bool), exit, new()));
            body.Statements.Add(assignment); body.Statements.Add(new Statement.Break());
            function.Body.Statements.Add(new Statement.Loop(body, new()));
        }
        function.Body.Statements.Add(new Statement.Return(returned));
        var result = WgslLetPromotion.Run(module);
        ModuleValidator.Validate(result);
        Assert.Equal(retainsStorage, result.Functions[0].Body.Statements.OfType<Statement.Declare>().Any(d => d.Name == "value" && d.Mutable));
        Assert.True(((Statement.Declare)module.Functions[0].Body.Statements[0]).Mutable);
    }

    [Fact]
    public void DerivativeEvaluationStaysOutsideDivergentControlFlow()
    {
        string result = ShaderTranslation.NormalizeWgsl("""
            @fragment fn main(@location(0) value: f32) -> @location(0) f32 {
                let derivative = dpdx(value);
                if (value > 0f) { return derivative; }
                return 0f;
            }
            """);
        Assert.True(result.IndexOf("dpdx(", StringComparison.Ordinal) < result.IndexOf("if (", StringComparison.Ordinal), result);
        WgslReader.Parse(result);
    }

    [Theory]
    [InlineData("let x = 4294967295u; data[1] = x + 1u;", 0u)]
    [InlineData("let x = 32u; data[1] = data[0] << x;", 7u)]
    [InlineData("let x = 0u; data[1] = data[0] / x;", 7u)]
    [InlineData("let x = 0u; data[1] = data[0] % x;", 0u)]
    public void SpirvRoundTripKeepsRuntimeEvaluationAndResults(string body, uint expected)
    {
        string source = $$"""
            @group(0) @binding(0) var<storage, read_write> data: array<u32>;
            @compute @workgroup_size(1) fn main() { {{body}} }
            """;
        byte[] binary = ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default);
        string result = ShaderTranslator.SpirvToWgsl(binary, SpirvCompilationTarget.Default);
        Assert.Equal(expected, WgslFixtureInterpreter.Execute(result).Memory[1]);
    }
}
