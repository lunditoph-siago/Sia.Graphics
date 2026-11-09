using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Front;

namespace Sia.Spirv.Compiler.Tests.Translation;

public sealed class WgslBuiltinValidationTests
{
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
        string result = ShaderTranslation.NormalizeWgsl(source);
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
    {
        var error = Assert.Throws<ShaderException>(() => WgslReader.Parse($$"""
            @group(0) @binding(0) var<storage, read_write> data: array<{{type}}>;
            @compute @workgroup_size(1) fn main() { data[1] = {{expression}}; }
            """));
        Assert.Equal(DiagnosticStage.Validation, error.Diagnostic.Stage);
        Assert.Contains("builtin bounds", error.Diagnostic.Message);
    }
}
