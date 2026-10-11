using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalUncalledFunctionTests
{
    // Adapter regression tests construct the remaining migration boundary explicitly.
    // Entry reachability no longer creates this state in the production pipeline.
    internal static CanonicalModule Defer(CanonicalModule module, string name)
        => module with {
            Functions = module.Functions.Where(p => p.Key != name).ToDictionary(p => p.Key, p => p.Value),
            DeferredFunctions = module.DeferredFunctions.Append(new KeyValuePair<string, string>(name, "test migration boundary"))
                .ToDictionary(p => p.Key, p => p.Value)
        };

    [Fact]
    public void DiagnosticRangesRemainOwnedWhenBorrowedBodiesAreReplaced()
    {
        var input = WgslReader.Parse("fn helper() @diagnostic(info,vendor.root){@diagnostic(off,vendor.empty) {}}");
        var canonical = CanonicalShaderPipeline.Prepare(input); var graph = canonical.Functions["helper"];
        Assert.Single(graph.BodyDiagnosticFilters); Assert.Single(graph.DiagnosticRanges);
        input.Functions.Single().Body = new();
        input.Functions.Single().Body.DiagnosticFilters.Add(new(DiagnosticSeverity.Error, "poison", "vendor"));
        var copy = graph.Copy();
        Assert.NotSame(graph.BodyDiagnosticFilters, copy.BodyDiagnosticFilters);
        Assert.NotSame(graph.DiagnosticRanges.Single(), copy.DiagnosticRanges.Single());
        var text = WgslWriter.Emit(ShaderTargetLowering.ForWgsl(canonical));
        Assert.Contains("vendor.root", text); Assert.Contains("vendor.empty", text); Assert.DoesNotContain("vendor.poison", text);
        ModuleValidator.Validate(WgslReader.Parse(text));
    }

    [Theory] [InlineData(0ul, 0u)] [InlineData(4294967296ul, 0u)] [InlineData(ulong.MaxValue, uint.MaxValue)]
    public void ConstantConversionsFoldOnTheOwnedGraphBeforeEitherTarget(ulong input, uint expected)
    {
        var module = new Module(); var function = new ShaderFunction("convert") { ReturnType = ShaderType.U32 };
        var expression = new Expression.Convert(ShaderType.U32, new Expression.Literal(input, new ShaderType.Scalar(ScalarKind.Uint, 8))) { Span = new(34, 7) };
        function.Body.Statements.Add(new Statement.Return(expression)); module.Functions.Add(function);
        var traces = new List<CanonicalPassTrace>(); var canonical = CanonicalShaderPipeline.Prepare(module, traces);
        var graph = canonical.Functions["convert"];
        Assert.DoesNotContain(graph.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Convert);
        Assert.Contains(graph.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Literal { Value: uint n } && n == expected && i.Span == expression.Span);
        Assert.Same(expression, Assert.IsType<Statement.Return>(Assert.Single(function.Body.Statements)).Value);
        Assert.Equal(ControlFlowAnalyses.All, Assert.Single(traces, t => t.Pass == "constant-conversion-folding").Preserved);
        Assert.Contains($"return {expected}u;", WgslWriter.Emit(ShaderTargetLowering.ForWgsl(canonical)));
        ModuleValidator.Validate(ShaderTargetLowering.PrepareSpirv(canonical, null), native: true);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void UncalledAndLibraryFunctionsOwnTheirExecutableGraphs(bool entry)
    {
        const string helpers = "fn leaf(n:u32)->u32{return n+3u;}fn edit(p:ptr<function,u32>){*p=leaf(*p);}fn library(n:u32)->u32{var x=n;edit(&x);return x;}";
        var input = WgslReader.Parse(helpers + (entry ? "@compute @workgroup_size(1) fn main(){}" : ""));
        var traces = new List<CanonicalPassTrace>();
        var canonical = CanonicalShaderPipeline.Prepare(input, traces);
        Assert.Empty(canonical.DeferredFunctions);
        Assert.Equal(input.Functions.Count, canonical.Functions.Count);
        Assert.Equal(entry ? new[] { "main" } : [], canonical.EntryFunctions.Order().ToArray());
        Assert.Contains(traces, t => t.Function == "library" && t.Pass == "local-value-promotion");
        var before = canonical.Functions.ToDictionary(p => p.Key, p => ControlFlowPrinter.Write(p.Value));
        var fresh = CanonicalShaderPipeline.Prepare(input);
        Assert.All(canonical.Functions, p => Assert.NotSame(p.Value, fresh.Functions[p.Key]));
        foreach (var function in input.Functions) {
            function.Body = new();
            function.Body.Statements.Add(new Statement.Evaluate(new Expression.Reference("obsolete", ShaderType.U32)));
        }
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(input));
        ModuleValidator.Validate(canonical);
        var effects = ShaderEffectAnalysis.Compute(canonical);
        Assert.True((effects["library"] & ShaderEffects.UnknownCall) != 0);
        var target = ShaderTargetLowering.PrepareSpirv(canonical, null);
        Assert.Contains("library", target.Functions.Keys);
        Assert.Contains("leaf", target.Functions.Keys);
        Assert.Empty(target.DeferredFunctions);
        var wgsl = WgslWriter.Emit(ShaderTargetLowering.ForWgsl(canonical));
        var parsed = WgslReader.Parse(wgsl);
        Assert.Contains(parsed.Functions, f => f.Name == "library");
        Assert.DoesNotContain("obsolete", wgsl);
        if (entry) {
            var native = SpirvWriter.Emit(SpirvEntryPointLowering.Run(target, true, true)).ToBytes();
            ModuleValidator.Validate(SpirvReader.Parse(native));
        }
        Assert.All(canonical.Functions, p => Assert.Equal(before[p.Key], ControlFlowPrinter.Write(p.Value)));
    }
}
