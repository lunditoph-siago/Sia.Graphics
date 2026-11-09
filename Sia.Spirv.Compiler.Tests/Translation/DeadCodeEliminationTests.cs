using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Tests.Translation;

public sealed class DeadCodeEliminationTests
{
    [Fact]
    public void UnusedPureWorkDoesNotReachTheSpirvBackend()
    {
        const string source = """
            @group(0) @binding(0) var<storage, read> data: array<f32>;
            @compute @workgroup_size(1) fn main() { let unused = sin(data[0]) * 2f; }
            """;
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default));
        Assert.DoesNotContain(binary.Instructions, i => i.Opcode is (ushort)Op.ExtInst or (ushort)Op.FMul);
    }

    [Fact]
    public void DeadPureCalculationsAreRemoved()
    {
        string result = ShaderTranslation.NormalizeWgsl("""
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
    public void InvalidSourceIsRejectedBeforeDeadValueElimination()
    {
        var error = Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv(
            "@compute @workgroup_size(1) fn main() { let unused = vec2<u32>(1u)[3]; }", SpirvCompilationTarget.Default));
        Assert.Contains("out of bounds", error.Diagnostic.Message);
    }
}
