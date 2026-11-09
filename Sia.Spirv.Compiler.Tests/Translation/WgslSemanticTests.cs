using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;

namespace Sia.Spirv.Compiler.Tests.Translation;

public sealed class WgslSemanticTests
{
    [Theory]
    [InlineData("let x = data[0]; let a = x + 1u; data[1] = a * 2u;", 7u, 16u)]
    [InlineData("let x = data[0]; data[0] = 40u; data[1] = x + 2u;", 40u, 9u)]
    [InlineData("let x = data[0]; workgroupBarrier(); data[0] = 9u; data[1] = x;", 9u, 7u)]
    [InlineData("let x = next(); data[1] = x + 1u;", 10u, 8u)]
    [InlineData("data[1] = select(next(), next(), true);", 10u, 10u)]
    [InlineData("let x = 4294967295u; data[1] = x + 1u;", 7u, 0u)]
    [InlineData("let x = 0u; data[1] = data[0] / x;", 7u, 7u)]
    [InlineData("let x = 0u; data[1] = data[0] % x;", 7u, 0u)]
    [InlineData("let x = 32u; data[1] = data[0] << x;", 7u, 7u)]
    [InlineData("var a = 1u; var b = 2u; for (var i = 0u; i < 3u; i++) { let old = a; a = b; b = old; } data[0] = a; data[1] = b;", 2u, 1u)]
    [InlineData("var x = 1u; if (data[0] > 4u) { x = 5u; } else { x = 6u; } data[1] = x;", 7u, 5u)]
    [InlineData("var v = vec2<u32>(data[0], 2u); data[1] = v[0] + v[1];", 7u, 9u)]
    [InlineData("var v = vec2<u32>(5u, 6u); let old = v; v[0] = 9u; data[0] = old[0]; data[1] = v[0];", 5u, 9u)]
    [InlineData("var v = vec2<u32>(1u); if (data[0] > 4u) { v = vec2<u32>(3u, 4u); } data[1] = v[0] + v[1];", 7u, 7u)]
    [InlineData("var v = vec2<u32>(data[0], 2u); data[1] = first(&v);", 7u, 7u)]
    public void GeneratedWgslPreservesMemoryAndArithmetic(string body, uint first, uint second)
    {
        string source = $$"""
            @group(0) @binding(0) var<storage, read_write> data: array<u32>;
            fn next() -> u32 { let old = data[0]; data[0] = 10u; return old; }
            fn first(p: ptr<function, vec2<u32>>) -> u32 { return (*p)[0]; }
            @compute @workgroup_size(1) fn main() { {{body}} }
            """;
        var before = WgslFixtureInterpreter.Execute(source);
        foreach (string output in Outputs(source)) {
            var after = WgslFixtureInterpreter.Execute(output);
            Assert.Equal(new[] { first, second }, after.Memory);
            Assert.Equal(before.Memory, after.Memory);
            Assert.Equal(before.Effects, after.Effects);
        }
    }

    [Fact]
    public void AtomicResultsAreCapturedOnceAndInOrder()
    {
        const string source = """
            @group(0) @binding(0) var<storage, read_write> data: array<atomic<u32>>;
            @compute @workgroup_size(1) fn main() {
                let x = atomicAdd(&data[0], 1u);
                atomicStore(&data[1], x + 1u);
            }
            """;
        foreach (string output in Outputs(source)) {
            var result = WgslFixtureInterpreter.Execute(output);
            Assert.Equal(new[] { 8u, 8u }, result.Memory);
            Assert.Equal(new[] { "atomicAdd", "atomicStore" }, result.Effects);
        }
    }

    private static IEnumerable<string> Outputs(string source)
    {
        yield return ShaderTranslation.NormalizeWgsl(source);
        byte[] binary = ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default);
        yield return ShaderTranslator.SpirvToWgsl(binary, SpirvCompilationTarget.Default);
    }
}
