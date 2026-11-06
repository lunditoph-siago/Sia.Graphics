using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;
using Sia.Spirv;

namespace Sia.Spirv.Compiler.Tests;

public sealed class WgslValueQualityTests
{
    [Theory]
    [InlineData("vec2<u32>(data[0], data[1])", "copy.x + copy.y")]
    [InlineData("array<u32, 2>(data[0], data[1])", "copy[0] + copy[1]")]
    [InlineData("Pair(data[0], data[1])", "copy.first + copy.second")]
    [InlineData("mat2x2<f32>(1f, 2f, 3f, 4f)", "u32(copy[0].x + copy[1].y)")]
    public void ReadOnlyAggregateLocalsDoNotRequireSpirvFunctionStorage(string initializer, string expression)
    {
        string source = $$"""
            struct Pair { first: u32, second: u32, }
            @group(0) @binding(0) var<storage, read_write> data: array<u32>;
            @compute @workgroup_size(1) fn main() {
                var copy = {{initializer}};
                data[1] = {{expression}};
            }
            """;
        var canonical = CanonicalShaderPipeline.Prepare(WgslReader.Parse(source));
        Assert.DoesNotContain(canonical.Functions.Values.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions),
            i => i.Operation is ValueOperation.Local);
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default));
        Assert.DoesNotContain(binary.Instructions, i => i.Opcode == (ushort)Op.Variable && i.Operands[2] == 7); // Function.
    }

    [Fact]
    public void DynamicArrayReadsShareTheirRequiredSpirvStorage()
    {
        const string source = """
            @group(0) @binding(0) var<storage, read_write> data: array<u32>;
            @compute @workgroup_size(1) fn main() {
                var copy = array<u32, 2>(data[0], data[1]);
                data[1] = copy[data[0] & 1u] + copy[data[1] & 1u];
            }
            """;
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default));
        Assert.Single(binary.Instructions, i => i.Opcode == (ushort)Op.Variable && i.Operands[2] == 7);
        Assert.DoesNotContain("var sia_cfg_", Translate(source));
    }

    [Theory]
    [InlineData("f32", "let low = 2f; data[1] = clamp(data[0], low, 1f);")]
    [InlineData("f32", "let low = 2f; data[1] = clamp(0f, low, 1f);")]
    [InlineData("u32", "let low = 2u; data[1] = clamp(data[0], low, 1u);")]
    [InlineData("f32", "let edge = 1f; data[1] = smoothstep(edge, 1f, data[0]);")]
    [InlineData("f32", "let exponent = 129i; data[1] = ldexp(data[0], exponent);")]
    [InlineData("u32", "let offset = 31u; data[1] = extractBits(data[0], offset, 2u);")]
    [InlineData("u32", "let offset = 31u; data[1] = insertBits(data[0], 0u, offset, 2u);")]
    public void RuntimeBuiltinBoundsAreNotTurnedIntoConstantStageErrors(string type, string body)
    {
        string source = $$"""
            @group(0) @binding(0) var<storage, read_write> data: array<{{type}}>;
            @compute @workgroup_size(1) fn main() { {{body}} }
            """;
        string result = Translate(source);
        Assert.Contains("let sia_runtime_", result);
        WgslReader.Parse(result);
        WgslReader.Parse(ShaderTranslator.SpirvToWgsl(ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default), SpirvCompilationTarget.Default));
    }

    [Theory]
    [InlineData("u32", "clamp(data[0], 2u, 1u)")]
    [InlineData("f32", "clamp(data[0], 2f, 1f)")]
    [InlineData("f32", "smoothstep(1f, 1f, data[0])")]
    [InlineData("f32", "ldexp(data[0], 129i)")]
    [InlineData("u32", "extractBits(data[0], 31u, 2u)")]
    [InlineData("u32", "insertBits(data[0], 0u, 31u, 2u)")]
    public void InvalidConstantBuiltinBoundsAreRejectedBeforeOptimization(string type, string expression)
        => Assert.Throws<ShaderException>(() => WgslReader.Parse($$"""
            @group(0) @binding(0) var<storage, read_write> data: array<{{type}}>;
            @compute @workgroup_size(1) fn main() { data[1] = {{expression}}; }
            """));

    [Fact]
    public void StraightLineArithmeticDoesNotMaterializeEverySsaValue()
    {
        string result = Translate("""
            @group(0) @binding(0) var<storage, read_write> data: array<u32>;
            @compute @workgroup_size(1) fn main() {
                let x = data[0];
                let a = x + 1u;
                let b = a * 2u;
                data[1] = b;
            }
            """);
        Assert.True(result.Split('\n').Count(l => l.TrimStart().StartsWith("let ", StringComparison.Ordinal)) <= 2, result);
        Assert.DoesNotContain("var sia_cfg_", result);
    }

    [Fact]
    public void DeadPureCalculationsAreRemoved()
    {
        string result = Translate("""
            @compute @workgroup_size(1) fn main() {
                let unused = vec4<f32>(2f, 3f, 4f, 5f);
                let unused2 = sin(unused) * unused;
            }
            """);
        Assert.DoesNotContain("sin(", result);
        Assert.DoesNotContain("let ", result);
        Assert.DoesNotContain("var ", result);
    }

    [Fact]
    public void ReadOnlyAggregateLocalIsPromotedToValues()
    {
        string result = Translate("""
            @group(0) @binding(0) var<storage, read_write> data: array<u32>;
            @compute @workgroup_size(1) fn main() {
                var copy = vec2<u32>(data[0], data[1]);
                data[1] = copy[0] + copy[1];
            }
            """);
        Assert.DoesNotContain("var sia_cfg_", result);
        WgslReader.Parse(result);
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
        string result = Translate("""
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
    [InlineData("4294967295u", "+ 1u")]
    [InlineData("0u", "/ x")]
    [InlineData("32u", "<< x")]
    public void RuntimeConstantOperationsRemainValidWgsl(string value, string operation)
    {
        string expression = operation == "/ x" ? "data[0] / x" : operation == "<< x" ? "data[0] << x" : "x " + operation;
        string result = Translate($$"""
            @group(0) @binding(0) var<storage, read_write> data: array<u32>;
            @compute @workgroup_size(1) fn main() { let x = {{value}}; data[1] = {{expression}}; }
            """);
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
        Assert.Equal(expected, WgslExecutionTests.Execute(result).Memory[1]);
    }

    internal static string Translate(string source)
        => WgslWriter.Write(WgslReader.Parse(source), SpirvCompilationTarget.Default);
}
