using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class SpirvUniformLegalizationTests
{
    [Fact]
    public void DynamicColumnSelectionAndQualifiedReadsExistBeforeSerialization()
    {
        var input = UniformMemoryTests.Fixture(1, dynamic: true);
        var prepared = ShaderTargetLowering.ForSpirv(input, null, true, true, true);
        var physical = Assert.IsType<ShaderType.Structure>(prepared.Module.Globals.Single(g => g.Name == "data").Type);
        Assert.Equal(4, physical.Members.Count);
        var helpers = prepared.Module.Functions.Where(f => f.Name.StartsWith("sia_spv_uniform_read_", StringComparison.Ordinal)).ToArray();
        var selected = Assert.Single(helpers, f => f.Body.Statements.OfType<Statement.Switch>().Any());
        Assert.True(StructuredControlFlowReader.TryRead(selected, prepared.Module, out var graph, out var reason), reason);
        ControlFlowVerifier.Validate(graph!, prepared.Module);
        Assert.Contains(graph!.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Load { MemoryAccess.Flags: 1 });
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()));
    }

    [Fact]
    public void UniformAliasIndexCapturesAreExplicitAndBorrowedInputIsPreserved()
    {
        var input = WgslReader.Parse(UniformMemoryTests.AliasSource); string before = WgslWriter.Write(input);
        var prepared = ShaderTargetLowering.ForSpirv(input, null, true, true, true);
        var main = prepared.Module.Functions.Single(f => f.Name == "main");
        Assert.DoesNotContain(main.Body.Statements, s => s is Statement.Declare { Type: ShaderType.Pointer { Space: AddressSpace.Uniform } });
        Assert.Contains(main.Body.Statements, s => s is Statement.Declare d && d.Name.StartsWith("sia_spv_uniform_index_", StringComparison.Ordinal));
        Assert.Equal(before, WgslWriter.Write(input));
        Assert.Equal(3, Assert.IsType<ShaderType.Structure>(input.Globals[0].Type).Members.Count);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()));
    }

    [Theory]
    [InlineData(MemoryDecorations.Volatile, 1u)]
    [InlineData(MemoryDecorations.Coherent, 48u)]
    public void WholeRootMemoryRequirementsAreExplicitOnSelectedLeavesInCanonicalHelpers(MemoryDecorations decoration, uint mask)
    {
        var input = UniformMemoryTests.AggregateFixture("root", 0, true, decoration);
        var prepared = ShaderTargetLowering.ForSpirv(input, null, true, true, true);
        var helper = Assert.Single(prepared.Module.Functions, f => f.Name.StartsWith("sia_spv_uniform_read_", StringComparison.Ordinal));
        Assert.True(StructuredControlFlowReader.TryRead(helper, prepared.Module, out var graph, out var reason), reason);
        ControlFlowVerifier.Validate(graph!, prepared.Module);
        var reads = graph!.Blocks.SelectMany(b => b.Instructions).Select(i => i.Operation).OfType<ValueOperation.Load>().ToArray();
        Assert.Equal(4, reads.Length);
        Assert.Equal(2, reads.Count(r => r.MemoryAccess?.Flags == mask));
        if (decoration == MemoryDecorations.Coherent) Assert.All(reads.Where(r => r.MemoryAccess is not null), r => Assert.Equal(5u, r.MemoryAccess!.VisibleScope));
    }

    [Fact]
    public void InnerScalarDeclarationCanShadowAnOuterConvertedUniformAlias()
    {
        string source = UniformMemoryTests.AliasSource.Replace("cursor=1u;", "{let column=8u;output[1]=f32(column);} cursor=1u;");
        var input = WgslReader.Parse(source);
        var prepared = SpirvPhysicalLayoutLowering.Prepare(input);
        ModuleValidator.Validate(prepared.Module);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()));
    }

    internal const string ContinuingSource = """
            struct Data{index:u32,matrix:mat2x2f,tail:f32,}
            @group(0) @binding(0) var<uniform> data:Data;
            @group(0) @binding(1) var<storage,read_write> output:array<f32>;
            @compute @workgroup_size(1) fn main(){loop{
                let column=&data.matrix[data.index];output[0]=(*column)[1];
                continuing{
                    output[1]=(*column)[1];
                    let column=&data.matrix[0u];output[2]=(*column)[1];
                    let stop=true;break if stop;
                }
            }}
            """;

    [Fact]
    public void ContinuingAliasCanShadowAnAliasInTheLoopBody()
    {
        var prepared = SpirvPhysicalLayoutLowering.Prepare(WgslReader.Parse(ContinuingSource));
        ModuleValidator.Validate(prepared.Module);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()));
    }

    [Theory]
    [InlineData("let stop=true;let stop=false;break if stop;")]
    [InlineData("break if stop;")]
    public void ContinuingScopeStillRejectsDuplicateDeclarationsAndUseBeforeDeclaration(string continuing)
    {
        string source = "@compute @workgroup_size(1) fn main(){loop{continuing{" + continuing + "}}}";
        Assert.Throws<ShaderException>(() => WgslReader.Parse(source));
    }
}
