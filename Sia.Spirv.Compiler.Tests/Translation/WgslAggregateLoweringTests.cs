using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Tests.Translation;

public sealed class WgslAggregateLoweringTests
{
    [Theory]
    [InlineData("vec2<u32>(data[0], data[1])", "copy.x + copy.y")]
    [InlineData("vec2<u32>(data[0], data[1])", "copy[0] + copy[1]")]
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
        string wgsl = ShaderTranslation.NormalizeWgsl(source);
        WgslAssertions.NoMutableLocals(wgsl);
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
        WgslAssertions.NoMutableLocals(ShaderTranslation.NormalizeWgsl(source));
    }
}
