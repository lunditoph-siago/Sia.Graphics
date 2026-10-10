using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalWgslTargetTests
{
    private const string Output = "@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;";
    internal const string PrivateZero = "var<private> slot:u32;" + Output
        + "@compute @workgroup_size(2) fn main(){slot=0u;outputs[0]=slot;}";
    internal const string Collective = "var<workgroup> value:u32;" + Output
        + "fn read()->u32{return workgroupUniformLoad(&value);}@compute @workgroup_size(1) fn main(){value=17u;outputs[0]=read();}";

    private static void Poison(CanonicalModule canonical)
    {
        foreach (var function in canonical.Declarations.Functions.Where(f => canonical.Functions.ContainsKey(f.Name))) {
            function.Body = new();
            function.Body.Statements.Add(new Statement.Declare("obsolete", new ShaderType.Array(ShaderType.U32, null) { OverrideLength = "missing" }, null));
            function.Body.Statements.Add(new Statement.Evaluate(new Expression.HelperInvocation()));
        }
    }

    [Theory] [InlineData("1", true)] [InlineData("2", false)] [InlineData("size", false)]
    public void EntryBuiltinFactsUseOwnedScalarVectorAndStructureValues(string size, bool folded)
    {
        var input = WgslReader.Parse("override size=1u;struct In{@builtin(local_invocation_index) index:u32,@builtin(local_invocation_id) local:vec3u,@builtin(global_invocation_id) world:vec3u}"
            + Output + "@compute @workgroup_size(" + size + ") fn main(arg:In){outputs[0]=arg.index+arg.local.x;outputs[1]=arg.world.x;}");
        var canonical = CanonicalShaderPipeline.Prepare(input); Poison(canonical);
        var before = canonical.Functions.ToDictionary(p => p.Key, p => ControlFlowPrinter.Write(p.Value));
        var old = canonical.Functions["main"];
        var results = old.Blocks.SelectMany(b => b.Instructions).Where(i => i.Operation is ValueOperation.Member { Name: "index" or "local" }).Select(i => i.Result!.Value).ToArray();
        Assert.Equal(2, results.Length);
        var lowered = WgslEntryPointLowering.Run(canonical); var graph = lowered.Functions["main"];
        Assert.Equal(folded, !ReferenceEquals(old, graph));
        foreach (var result in results) {
            var instruction = Assert.Single(graph.Blocks.SelectMany(b => b.Instructions), i => i.Result == result);
            Assert.Equal(folded, instruction.Operation is ValueOperation.Literal or ValueOperation.Construct);
        }
        Assert.Contains(graph.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Member { Name: "world" });
        Assert.All(canonical.Functions, p => Assert.Equal(before[p.Key], ControlFlowPrinter.Write(p.Value)));
        ModuleValidator.Validate(lowered, native: true);
        WgslReader.Parse(WgslWriter.Emit(ShaderTargetLowering.ForWgsl(canonical)));
    }

    [Theory]
    [InlineData("slot=0u;outputs[0]=slot;", true)]
    [InlineData("slot=1u;outputs[0]=slot;", false)]
    [InlineData("let p=&slot;*p=0u;outputs[0]=slot;", false)]
    [InlineData("{let slot=3u;outputs[0]=slot;}outputs[1]=slot;", false)]
    public void PrivateSlotFactsInspectGraphWritesEscapesAndShadowing(string body, bool folded)
    {
        var input = WgslReader.Parse("var<private> slot:u32;" + Output + "@compute @workgroup_size(2) fn main(){" + body + "}");
        var canonical = CanonicalShaderPipeline.Prepare(input); Poison(canonical);
        var original = canonical.Functions["main"]; string before = ControlFlowPrinter.Write(original);
        var lowered = WgslEntryPointLowering.Run(canonical);
        Assert.Equal(folded, !ReferenceEquals(original, lowered.Functions["main"]));
        Assert.Equal(before, ControlFlowPrinter.Write(original)); ModuleValidator.Validate(lowered, native: true);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void QualifiedPrivateLoadsAndStoresPreventFolding(bool store)
    {
        var canonical = CanonicalShaderPipeline.Prepare(WgslReader.Parse(PrivateZero)); var graph = canonical.Functions["main"];
        var definitions = graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null).ToDictionary(i => i.Result!.Value.Id);
        foreach (var block in graph.Blocks) for (int i = 0; i < block.Instructions.Count; i++) {
            var instruction = block.Instructions[i];
            var pointer = instruction.Operation switch { ValueOperation.Load l => l.Pointer, ValueOperation.Store s => s.Pointer, _ => default };
            if (pointer.Type is ShaderType.Pointer && definitions.GetValueOrDefault(pointer.Id)?.Operation is ValueOperation.Symbol { Name: "slot" }) {
                if (store && instruction.Operation is ValueOperation.Store s) block.Instructions[i] = instruction with { Operation = s with { MemoryAccess = new(1) } };
                if (!store && instruction.Operation is ValueOperation.Load l) block.Instructions[i] = instruction with { Operation = l with { MemoryAccess = new(1) } };
            }
        }
        Poison(canonical); var before = ControlFlowPrinter.Write(graph);
        Assert.Same(graph, WgslEntryPointLowering.Run(canonical).Functions["main"]);
        Assert.Equal(before, ControlFlowPrinter.Write(graph));
    }

    [Fact]
    public void WgslTargetUsesOwnedPointerHelpersAndPreservesBorrowedGraphs()
    {
        var canonical = CanonicalShaderPipeline.Prepare(WgslReader.Parse(CanonicalDeferredHelperTests.Source)); Poison(canonical);
        var before = canonical.Functions.ToDictionary(p => p.Key, p => ControlFlowPrinter.Write(p.Value));
        string text = WgslWriter.Emit(ShaderTargetLowering.ForWgsl(canonical)); var parsed = WgslReader.Parse(text);
        Assert.DoesNotContain(parsed.Functions, f => f.Name == "edit");
        Assert.Equal(new uint[] { 13, 13 }, new CanonicalExecution(parsed, [5]).Run().Output);
        Assert.All(canonical.Functions, p => Assert.Equal(before[p.Key], ControlFlowPrinter.Write(p.Value)));
    }

    [Fact]
    public void CollectiveReadRecoveryUsesOwnedHelperEffectsAndPreservesSsa()
    {
        var input = SpirvReader.Parse(SpirvWriter.Write(WgslReader.Parse(Collective), SpirvCompilationTarget.Default));
        var canonical = CanonicalShaderPipeline.Prepare(input); Poison(canonical);
        var before = canonical.Functions.ToDictionary(p => p.Key, p => ControlFlowPrinter.Write(p.Value));
        var helper = Assert.Single(canonical.Functions.Values, g => g.Signature.Stage is null && g.Signature.ReturnType == ShaderType.U32);
        var readResults = helper.Blocks.SelectMany(b => b.Instructions)
            .Where(i => i.Operation is ValueOperation.Load { Pointer.Type: ShaderType.Pointer { Space: AddressSpace.Workgroup } }).Select(i => i.Result!.Value).ToArray();
        Assert.NotEmpty(readResults);
        var recovered = CollectiveReadRecovery.Run(canonical);
        foreach (var value in readResults) Assert.Contains(recovered.Functions[helper.Signature.Name].Blocks.SelectMany(b => b.Instructions),
            i => i.Result == value && i.Operation is ValueOperation.Builtin { Function: "workgroupUniformLoad" });
        string text = WgslWriter.Emit(ShaderTargetLowering.ForWgsl(canonical)); Assert.Contains("workgroupUniformLoad", text);
        WgslReader.Parse(text);
        Assert.All(canonical.Functions, p => Assert.Equal(before[p.Key], ControlFlowPrinter.Write(p.Value)));
    }

    [Fact]
    public void RecoveryKeepsAnUnchangedFunctionOutsideTheEntryGraphDeferred()
    {
        var canonical = CanonicalShaderPipeline.Prepare(WgslReader.Parse("fn f()->i32{var a=array<i32,1>(42);let p=&a;return p[0];}"));
        Assert.Empty(canonical.Functions); var function = Assert.Single(canonical.Declarations.Functions);
        var recovered = CollectiveReadRecovery.Run(canonical);
        Assert.Empty(recovered.Functions); Assert.Equal(canonical.DeferredFunctions["f"], recovered.DeferredFunctions["f"]);
        Assert.Same(function, Assert.Single(recovered.Declarations.Functions));
        Assert.Contains("(*p)[0i]", WgslWriter.Emit(ShaderTargetLowering.ForWgsl(canonical)));
    }

    [Fact]
    public void EmptyInlinedHelperRetainsItsLexicalDiagnosticScope()
    {
        var canonical = CanonicalShaderPipeline.Prepare(WgslReader.Parse("enable wgpu_ray_query;@diagnostic(off,derivative_uniformity) fn helper(q:ptr<function,ray_query>){}"
            + "@compute @workgroup_size(1) fn main(){var q:ray_query;helper(&q);}"));
        Poison(canonical); var before = canonical.Functions.ToDictionary(p => p.Key, p => ControlFlowPrinter.Write(p.Value));
        string text = WgslWriter.Emit(ShaderTargetLowering.ForWgsl(canonical));
        Assert.Contains("@diagnostic(off, derivative_uniformity)", text); WgslReader.Parse(text);
        Assert.All(canonical.Functions, p => Assert.Equal(before[p.Key], ControlFlowPrinter.Write(p.Value)));
    }

    [Fact]
    public void DynamicHelperInvocationIsRejectedFromTheOwnedGraphWithItsOrigin()
    {
        var canonical = CanonicalShaderPipeline.Prepare(WgslReader.Parse("@fragment fn main(){}")); var graph = canonical.Functions["main"];
        var span = new SourceSpan(39, 5); graph.Blocks.Single(b => b.Id == graph.Entry).Instructions.Add(new(graph.Value(ShaderType.Bool), new ValueOperation.HelperInvocation(), span));
        var error = Assert.Throws<ShaderException>(() => ShaderTargetLowering.ForWgsl(canonical));
        Assert.Equal(DiagnosticStage.WgslWrite, error.Diagnostic.Stage); Assert.Equal(span, error.Diagnostic.Span);
    }
}
