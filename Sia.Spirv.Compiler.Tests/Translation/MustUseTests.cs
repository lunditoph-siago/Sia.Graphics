using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class MustUseTests
{
    [Theory]
    [InlineData("return required();", "i32")]
    [InlineData("var value = required(); return value;", "i32")]
    [InlineData("let value = required(); return value;", "i32")]
    [InlineData("_ = required();", "")]
    [InlineData("let value = required() + 1;", "")]
    public void RequiredResultsCanBeConsumedOrExplicitlyDiscarded(string body, string result)
    {
        string source = $"fn caller() {(result == "" ? "" : "-> " + result)} {{ {body} }} @must_use fn required() -> i32 {{ return 10; }} @compute @workgroup_size(1) fn main() {{ _ = caller(); }}";
        // Void calls cannot be phony-assigned.
        if (result == "") source = source.Replace("_ = caller();", "caller();", StringComparison.Ordinal);
        byte[] bytes = ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default);
        ModuleValidator.Validate(WgslReader.Parse(ShaderTranslator.SpirvToWgsl(bytes, SpirvCompilationTarget.Default)));
    }

    [Theory]
    [InlineData("fn caller() { required(); } @must_use fn required() -> i32 { return 1; }")]
    [InlineData("@must_use fn required() {}")]
    [InlineData("@must_use(1) fn required() -> i32 { return 1; }")]
    [InlineData("@must_use @must_use fn required() -> i32 { return 1; }")]
    [InlineData("@must_use const value = 1;")]
    [InlineData("fn main() { abs(-1); }")]
    [InlineData("fn main() { vec2f(1.0); }")]
    [InlineData("struct Value { x: i32, } fn main() { Value(1); }")]
    [InlineData("fn main() { bitcast<f32>(1u); }")]
    [InlineData("var<workgroup> value: u32; @compute @workgroup_size(1) fn main() { workgroupUniformLoad(&value); }")]
    [InlineData("@compute @workgroup_size(1) fn main() { subgroupAdd(1u); }")]
    public void UnusedRequiredResultsAndInvalidAttributesAreRejected(string source)
    {
        Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default));
    }

    [Fact]
    public void UnannotatedCallsAndAtomicResultsCanBeDiscardedImplicitly()
    {
        const string source = "var<workgroup> value: atomic<u32>; fn optional() -> i32 { return 1; } @compute @workgroup_size(1) fn main() { optional(); atomicLoad(&value); atomicAdd(&value, 1u); atomicCompareExchangeWeak(&value, 1u, 2u); }";
        ModuleValidator.Validate(WgslReader.Parse(ShaderTranslator.SpirvToWgsl(ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default), SpirvCompilationTarget.Default)));
    }

    [Fact]
    public void IrEvaluationUsesAnExplicitPhonyAssignmentInWgsl()
    {
        var module = new Module(); var function = new ShaderFunction("main") { Stage = ShaderStage.Compute };
        function.Body.Statements.Add(new Statement.Evaluate(new Expression.Call("abs", [Expression.I32(-1)], ShaderType.I32)));
        module.Functions.Add(function);
        string output = WgslWriter.Write(module, SpirvCompilationTarget.Default);
        Assert.Contains("_ = abs(-1i);", output);
        ModuleValidator.Validate(WgslReader.Parse(output));
    }
}
