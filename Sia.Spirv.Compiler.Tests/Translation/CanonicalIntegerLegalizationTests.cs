using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalIntegerLegalizationTests
{
    private const string Loop = "@group(0) @binding(1) var<storage,read_write> output:array<u32>; @compute @workgroup_size(1) fn main(){var sum=0u; for(var i=0u;i<4u;i++){sum+=(i+2u)/2u;} output[0]=sum;}";

    [Theory]
    [InlineData("signed", 0xfffffff9u, 3u, 0xfffffffeu, 0xffffffffu)]
    [InlineData("signed", 0x80000000u, 0xffffffffu, 0x80000000u, 0u)]
    [InlineData("signed", 7u, 0u, 7u, 0u)]
    [InlineData("unsigned", 0xffffffffu, 2u, 0x7fffffffu, 1u)]
    [InlineData("unsigned", 17u, 0u, 17u, 0u)]
    [InlineData("ordered", 17u, 0u, 17u, 12u)]
    [InlineData("loop", 0u, 0u, 6u, 0u)]
    public void SharedGraphsReachTheTargetWithoutReconstructingBorrowedBodies(string family, uint numerator, uint divisor, uint quotient, uint remainder)
    {
        string source = family switch { "signed" => SpirvIntegerLegalizationTests.Signed, "unsigned" => SpirvIntegerLegalizationTests.Unsigned,
            "ordered" => SpirvIntegerLegalizationTests.Ordered, _ => Loop };
        var input = WgslReader.Parse(source); var canonical = CanonicalShaderPipeline.Prepare(input);
        Assert.Empty(canonical.DeferredFunctions);
        var before = canonical.Functions.ToDictionary(p => p.Key, p => ControlFlowPrinter.Write(p.Value));
        var bodies = input.Functions.ToDictionary(f => f.Name, f => f.Body);
        try {
            foreach (var function in input.Functions) { function.Body = new(); function.Body.Statements.Add(new Statement.Return(Expression.U32(999))); }
            var lowered = ShaderTargetLowering.PrepareSpirv(canonical, null);
            ModuleValidator.Validate(lowered);
            foreach (var pair in canonical.Functions) {
                Assert.NotSame(pair.Value, lowered.Functions[pair.Key]);
                Assert.Equal(before[pair.Key], ControlFlowPrinter.Write(pair.Value));
                Assert.Equal(pair.Value.Loops, lowered.Functions[pair.Key].Loops);
                Assert.Equal(pair.Value.Blocks.Select(b => b.Id), lowered.Functions[pair.Key].Blocks.Select(b => b.Id));
                foreach (var original in pair.Value.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null))
                    Assert.Contains(lowered.Functions[pair.Key].Blocks.SelectMany(b => b.Instructions), i => i.Result == original.Result);
            }
            foreach (var helper in lowered.Declarations.Functions.Where(f => !canonical.Functions.ContainsKey(f.Name))) {
                Assert.Empty(helper.Body.Statements);
                Assert.All(lowered.Functions[helper.Name].Blocks.SelectMany(b => b.Instructions), i => Assert.Equal(ShaderEffects.None, i.Effects));
            }
            var prepared = SpirvEntryPointLowering.Run(lowered, true, true);
            var bytes = SpirvWriter.Emit(prepared).ToBytes();
            ModuleValidator.Validate(SpirvReader.Parse(bytes));
            var result = new CanonicalExecution(SpirvReader.Parse(bytes), [numerator, divisor]).Run();
            Assert.Equal(new uint[] { quotient, remainder }, result.Output);
            if (family == "ordered") Assert.Equal(new[] { 0, 1 }, result.Reads);
        }
        finally { foreach (var function in input.Functions) function.Body = bodies[function.Name]; }
    }

    [Theory]
    [InlineData("i16", 1, false)] [InlineData("u16", 1, false)]
    [InlineData("i32", 1, false)] [InlineData("u32", 1, false)]
    [InlineData("i64", 1, false)] [InlineData("u64", 1, false)]
    [InlineData("i16", 2, false)] [InlineData("u16", 3, false)]
    [InlineData("i64", 4, false)] [InlineData("u64", 2, false)]
    [InlineData("i32", 2, true)] [InlineData("u32", 4, true)]
    public void WidthsBroadcastAndSourceOriginsStayInTheActualPreparedGraphs(string scalar, int size, bool scalarLeft)
    {
        var input = WgslReader.Parse(SpirvIntegerLegalizationTests.WidthFixture(scalar, size, scalarLeft));
        var canonical = CanonicalShaderPipeline.Prepare(input);
        var graph = canonical.Functions["main"];
        var filter = new DiagnosticFilter(DiagnosticSeverity.Warning, "derivative_uniformity");
        foreach (var block in graph.Blocks)
            for (int i = 0; i < block.Instructions.Count; i++)
                if (block.Instructions[i].Operation is ValueOperation.Binary { Operator: "/" or "%" })
                    block.Instructions[i] = block.Instructions[i] with { Span = new(99, 3), DiagnosticFilters = [filter] };
        string before = ControlFlowPrinter.Write(graph);
        var lowered = ShaderTargetLowering.PrepareSpirv(canonical, null);
        Assert.Equal(before, ControlFlowPrinter.Write(graph));
        var calls = lowered.Functions["main"].Blocks.SelectMany(b => b.Instructions)
            .Where(i => i.Operation is ValueOperation.Call c && c.Function.StartsWith("sia_spv_integer_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, i => { Assert.Equal(new SourceSpan(99, 3), i.Span); Assert.Equal(new[] { filter }, i.DiagnosticFilters); });
        foreach (var helper in lowered.Functions.Where(p => p.Key.StartsWith("sia_spv_integer_", StringComparison.Ordinal))) {
            Assert.Empty(helper.Value.Signature.Body.Statements);
            var block = Assert.Single(helper.Value.Blocks);
            Assert.All(block.Instructions, i => Assert.Equal(ShaderEffects.None, i.Effects));
            if (scalar.StartsWith('i')) {
                long minimum = scalar switch { "i16" => short.MinValue, "i64" => long.MinValue, _ => int.MinValue };
                Assert.Contains(block.Instructions, i => i.Operation is ValueOperation.Literal l && Convert.ToInt64(l.Value) == minimum);
            }
        }
        var prepared = SpirvEntryPointLowering.Run(lowered, true, true);
        ModuleValidator.Validate(prepared.PhysicalLayout.Canonical);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OptOutPreservesRawUnsignedOperationsAndSignedRemainderPolicy(bool signed)
    {
        var canonical = CanonicalShaderPipeline.Prepare(WgslReader.Parse(signed ? SpirvIntegerLegalizationTests.Signed : SpirvIntegerLegalizationTests.Unsigned));
        var lowered = ShaderTargetLowering.PrepareSpirv(canonical, null, false);
        var helpers = lowered.Functions.Where(p => p.Key.StartsWith("sia_spv_integer_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(signed ? 1 : 0, helpers.Length);
        if (signed) Assert.DoesNotContain(Assert.Single(helpers).Value.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Select);
        Assert.Contains(lowered.Functions["main"].Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Binary { Operator: "/" });
        ModuleValidator.Validate(lowered);
    }
}
