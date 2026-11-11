using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Tests.IL;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Tests.Interoperability;

[Trait("Category", "Native")]
public sealed class NativeCompilerTests
{
    [NativeCompilerTheory(NativeCompiler.Tint)]
    [MemberData(nameof(IlSemanticCases.StorageResults), MemberType = typeof(IlSemanticCases))]
    public void TintPreservesIlStorageResults(string name, uint first, uint second)
        => AssertIlStorageResults(NativeCompiler.Tint, name, first, second);

    [NativeCompilerTheory(NativeCompiler.Naga)]
    [MemberData(nameof(IlSemanticCases.StorageResults), MemberType = typeof(IlSemanticCases))]
    public void NagaPreservesIlStorageResults(string name, uint first, uint second)
        => AssertIlStorageResults(NativeCompiler.Naga, name, first, second);

    [NativeCompilerTheory(NativeCompiler.Tint)]
    [InlineData(NativeCompiler.Tint)]
    public void TintPreservesRefLocalResults(string compiler)
        => AssertRefLocalResults(compiler);

    [NativeCompilerTheory(NativeCompiler.Naga)]
    [InlineData(NativeCompiler.Naga)]
    public void NagaPreservesRefLocalResults(string compiler)
        => AssertRefLocalResults(compiler);

    private static void AssertRefLocalResults(string compiler)
    {
        byte[] binary = RuntimeRefLocalTests.Compile(nameof(RefLocalShaders.Loop), new() { Version = 0x00010300 });
        string output = NativeCompiler.ReadSpirv(compiler, binary);
        Assert.False(string.IsNullOrWhiteSpace(output));
        Assert.Equal(new[] { 8u, 5u }, WgslFixtureInterpreter.Execute(output).Memory);
    }

    public static TheoryData<string, string> UniformInputs => new() {
        { "workgroup input copied through helper", """
            var<workgroup> data: array<i32, 128>;
            fn read(index: u32) { if workgroupUniformLoad(&data[index]) > 10i { workgroupBarrier(); } }
            @compute @workgroup_size(4) fn main(@builtin(workgroup_id) id: vec3<u32>) { read(id.x); }
            """ },

    };

    [NativeCompilerTheory(NativeCompiler.Tint)]
    [MemberData(nameof(UniformInputs))]
    public void TintAcceptsUniformBuiltinProxies(string scenario, string source)
    {
        byte[] binary = ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default);
        string wgsl = ShaderTranslator.SpirvToWgsl(binary, SpirvCompilationTarget.Default);
        string output = NativeCompiler.ReprintWithTint(wgsl);
        Assert.False(string.IsNullOrWhiteSpace(output), scenario);
        Assert.Equal(ShaderStage.Compute, Assert.Single(WgslReader.Parse(output).Functions, f => f.Stage is not null).Stage);
    }

    [NativeCompilerTheory(NativeCompiler.Tint)]
    [InlineData(NativeCompiler.Tint)]
    public void TintPreservesOptimizedProduct(string compiler) => AssertProduct(compiler);

    [NativeCompilerTheory(NativeCompiler.Naga)]
    [InlineData(NativeCompiler.Naga)]
    public void NagaPreservesOptimizedProduct(string compiler) => AssertProduct(compiler);

    private static void AssertProduct(string compiler)
    {
        byte[] binary = ShaderTranslator.WgslToSpirv(Translation.SpirvValueOptimizationTests.ConstantVectorProduct,
            new() { Version = 0x00010300 });
        string output = NativeCompiler.ReadSpirv(compiler, binary);
        Assert.Equal(new[] { 7u, 98u }, WgslFixtureInterpreter.Execute(output).Memory);
    }

    private static void AssertIlStorageResults(string compiler, string name, uint first, uint second)
    {
        // Compile SPIR-V 1.3 directly for Tint's reader; never rewrite a header.
        byte[] binary = SpirvTestAssembly.CompileRuntime(typeof(MemorySemanticsShaders), name, new() { Version = 0x00010300 });
        string output = NativeCompiler.ReadSpirv(compiler, binary);
        Assert.False(string.IsNullOrWhiteSpace(output));
        Assert.Equal(new[] { first, second }, WgslFixtureInterpreter.Execute(output).Memory);
    }
}
