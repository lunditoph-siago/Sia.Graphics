using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class ValidationTests
{
    [Theory]
    [InlineData("fn f() -> i32 { }")]
    [InlineData("fn f() -> i32 { if true { return 1; } }")]
    [InlineData("fn f() -> bool { return !1; }")]
    [InlineData("fn f() { let x = vec2(1,2)[2]; }")]
    [InlineData("fn f() { f(); }")]
    [InlineData("fn f() { g(); } fn g() { f(); }")]
    [InlineData("fn f() { let x = dot(1.0, 2.0); }")]
    [InlineData("fn f() { let x = cross(vec2f(1), vec2f(2)); }")]
    [InlineData("fn f() { let x = all(1u); }")]
    [InlineData("@compute @workgroup_size(1) fn f() { discard; }")]
    [InlineData("fn helper() { let x = dpdx(1.0f); } @compute @workgroup_size(1) fn f() { helper(); }")]
    [InlineData("@vertex fn f() -> @location(0) f32 { return 1; }")]
    [InlineData("@compute @workgroup_size(0) fn f() { }")]
    public void InvalidShaderIsRejectedBeforeWriting(string source)
    {
        var exception = Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default));
        Assert.Equal(DiagnosticStage.Validation, exception.Diagnostic.Stage);
    }

    [Fact]
    public void ValidatorChecksIrMutabilityIndependentlyOfFrontend()
    {
        var module = WgslReader.Parse("fn f() { let value = 1i; }");
        module.Functions[0].Body.Statements.Add(new Statement.Store(new Expression.Reference("value", ShaderType.I32), Expression.I32(2)));
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(module));
    }

    [Fact]
    public void ReturnInBothBranchesAndInfiniteLoopHaveNoMissingReturn()
    {
        ModuleValidator.Validate(WgslReader.Parse("fn f(a: bool) -> i32 { if a { return 1; } else { return 2; } } fn g() -> i32 { loop { } }"));
    }

    [Fact]
    public void WriterDeduplicatesFunctionTypesAndBlockDecorations()
    {
        var module = WgslReader.Parse("struct S { values: array<u32>, } @group(0) @binding(0) var<storage, read> a: S; @group(0) @binding(1) var<storage, read> b: S; fn one() {} fn two() {} @compute @workgroup_size(1) fn main() { one(); two(); }");
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default));
        Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.TypeFunction);
        Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Decorate && i.Operands[1] == 2);
    }

    [Fact]
    public void PublicTranslatorRoundtripKeepsComputeEntryAndResourceBinding()
    {
        const string source = "@group(1) @binding(3) var<storage, read_write> data: array<u32>; @compute @workgroup_size(8) fn main(@builtin(global_invocation_id) id: vec3u) { data[id.x] = id.x * 2u; }";
        string translated = ShaderTranslator.SpirvToWgsl(ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default), SpirvCompilationTarget.Default);
        var module = WgslReader.Parse(translated); ModuleValidator.Validate(module);
        Assert.Equal(new ResourceBinding(1, 3), Assert.Single(module.Globals, g => g.Binding is not null).Binding);
        Assert.Equal(ShaderStage.Compute, Assert.Single(module.Functions, f => f.Stage is not null).Stage);
    }
}
