using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class WgslTests
{
    [Fact]
    public void NestedTemplatesAndShiftOperatorsAreDistinguished()
    {
        var tokens = WgslLexer.Tokenize("var x: array<vec4<u32>, 2>; fn f() { let y = 8u >> 2u; }");
        Assert.Equal(2, tokens.Count(t => t.Kind == TokenKind.TemplateStart));
        Assert.Equal(2, tokens.Count(t => t.Kind == TokenKind.TemplateEnd));
        Assert.Contains(tokens, t => t.Text == ">>" && t.Kind == TokenKind.Symbol);
        WgslReader.Parse("alias T = array<array<vec4<u32>, 2>, 3>; var<private> x: T;");
    }

    [Fact]
    public void NestedCommentsAndUnicodeIdentifiersParse()
    {
        var module = WgslReader.Parse("/* outside /* inside */ outside */ fn θ2() -> i32 { return 42; }");
        Assert.Equal("θ2", Assert.Single(module.Functions).Name);
        Assert.Throws<ShaderException>(() => WgslReader.Parse("/* outside /* inside */"));
    }

    [Theory]
    [InlineData("0x1.8p+1f", 3)] [InlineData(".5f", 0.5)] [InlineData("1e-2f", 0.01)]
    public void FloatingLiteralsParse(string text, double expected)
    {
        var literal = WgslNumbers.Parse(text, default);
        Assert.Equal((float)expected, Assert.IsType<float>(literal.Value));
    }

    [Theory]
    [InlineData("01u")] [InlineData("4294967296u")] [InlineData("2147483648i")] [InlineData("1e999f")]
    public void MalformedOrOutOfRangeLiteralsAreRejected(string text) => Assert.Throws<ShaderException>(() => WgslNumbers.Parse(text, default));

    [Fact]
    public void LoadPrecedesCallThatCanMutateTheLoadedVariable()
    {
        var module = WgslReader.Parse("var<private> value: i32; fn mutate() -> i32 { value = 3; return 2; } fn sample() -> i32 { return value + mutate(); }");
        var body = module.Functions.Single(f => f.Name == "sample").Body.Statements;
        Assert.IsType<Expression.Load>(Assert.IsType<Statement.Declare>(body[0]).Initializer);
        Assert.IsType<Expression.Call>(Assert.IsType<Statement.Declare>(body[1]).Initializer);
        Assert.IsType<Statement.Return>(body[2]);
    }

    [Fact]
    public void RuntimeShortCircuitKeepsRightHandCallInItsBranch()
    {
        var module = WgslReader.Parse("fn side_effect() -> bool { return true; } fn sample(a: bool) -> bool { return a && side_effect(); }");
        var body = module.Functions.Single(f => f.Name == "sample").Body.Statements;
        var conditional = Assert.Single(body.OfType<Statement.If>());
        Assert.Contains(conditional.Accept.Statements, s => s is Statement.Declare { Initializer: Expression.Call { Function: "side_effect" } });
        Assert.DoesNotContain(body, s => s is Statement.Declare { Initializer: Expression.Call });
    }

    [Fact]
    public void ConstantShortCircuitDoesNotEvaluateUndefinedRightHandValue()
    {
        var module = WgslReader.Parse("const ok = false && sqrt(-1) != 0; const_assert !ok;");
        Assert.Equal(false, Assert.IsType<Expression.Literal>(Assert.Single(module.Constants).Value).Value);
    }

    [Fact]
    public void ForwardAliasesAndDeclarationsResolve()
    {
        var module = WgslReader.Parse("alias T = S; struct S { value: array<u32, N>, } const N = 2; var<private> data: T;");
        var structure = Assert.Single(module.Structures);
        Assert.Equal((uint)2, Assert.IsType<ShaderType.Array>(Assert.Single(structure.Members).Type).Length);
        Assert.Equal(structure, Assert.Single(module.Globals).Type);
    }

    [Fact]
    public void PointerIndexUsesAutomaticDereference()
    {
        string output = WgslWriter.Write(WgslReader.Parse("fn f() -> i32 { var a = array<i32, 1>(42); let p = &a; return p[0]; }"));
        Assert.Contains("(*p)[0i]", output);
    }

    [Fact]
    public void SwitchSelectorAndCasesInferUnsignedTypeTogether()
    {
        var module = WgslReader.Parse("fn f() { switch 0 { case 0u: {} default: {} } }");
        var statement = Assert.IsType<Statement.Switch>(Assert.Single(Assert.Single(module.Functions).Body.Statements));
        Assert.Equal(ShaderType.U32, statement.Selector.Type);
    }

    [Theory]
    [InlineData("alias A = B; alias B = A;")]
    [InlineData("const A = B; const B = A;")]
    [InlineData("const A = 4294967295u + 1u;")]
    [InlineData("@group(0) @binding(0) var<storage, read> a: u32; fn f() { a = 1u; }")]
    [InlineData("fn f() { let x = 1; x = 2; }")]
    [InlineData("fn f() { break; }")]
    [InlineData("fn f() { unknown(); }")]
    public void RejectsInvalidPrograms(string source) => Assert.Throws<ShaderException>(() => WgslReader.Parse(source));

    [Fact]
    public void ExplicitStrideBecomesSizedElementWrapperAndAccess()
    {
        var array = new ShaderType.Array(ShaderType.U32, 4, 16);
        var module = new Module(); module.Globals.Add(new("data", array, AddressSpace.Private));
        var function = new ShaderFunction("f");
        var place = new Expression.Reference("data", new ShaderType.Pointer(array, AddressSpace.Private));
        function.Body.Statements.Add(new Statement.Store(new Expression.Access(place, Expression.U32(1), new ShaderType.Pointer(ShaderType.U32, AddressSpace.Private)), Expression.U32(7)));
        module.Functions.Add(function);
        string output = WgslWriter.Write(module);
        Assert.Contains("@size(16) value: u32", output);
        Assert.Contains("data[1u].value = 7u", output);
        Assert.Equal((uint)64, TypeLayout.Of(array).Size);
    }
}
