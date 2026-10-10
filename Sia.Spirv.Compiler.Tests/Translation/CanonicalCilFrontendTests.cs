using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Tests;
using Sia.Spirv.Compiler.IL;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalCilFrontendTests
{
    [Theory]
    [InlineData(typeof(ControlFlowShaders), nameof(ControlFlowShaders.IntegerControlFlow))]
    [InlineData(typeof(ComputeShaders), nameof(ComputeShaders.UseHelpers))]
    [InlineData(typeof(TextureShaders), nameof(TextureShaders.SampleAndLoad))]
    public void CilFrontendDoesNotEncodeBranchesAsMutableProgramCounters(Type owner, string name)
    {
        var kernel = SpirvTestAssembly.GetKernel(owner, name);
        var request = new SpirvModuleCompilationRequest(File.ReadAllBytes(SpirvTestAssembly.Path), kernel.MetadataToken,
            File.ReadAllBytes(typeof(SpirvKernelAttribute).Assembly.Location));
        var module = new SpirvCompiler().CompileModule(request);
        Assert.DoesNotContain(module.Functions.SelectMany(f => Statements(f.Body)).OfType<Statement.Declare>(),
            d => d.Name.StartsWith("sia_block_", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(typeof(ControlFlowShaders), nameof(ControlFlowShaders.IntegerControlFlow))]
    [InlineData(typeof(ComputeShaders), nameof(ComputeShaders.UseHelpers))]
    [InlineData(typeof(ComputeShaders), nameof(ComputeShaders.CopyLogicalStructs))]
    [InlineData(typeof(ShortCircuitShaders), nameof(ShortCircuitShaders.Fragment))]
    public void DirectCilGraphsOwnTheirControlFlowAndSurviveBodyReplacement(Type owner, string name)
    {
        var kernel = SpirvTestAssembly.GetKernel(owner, name);
        var input = RuntimeShaderLowering.ReadCanonical(File.ReadAllBytes(SpirvTestAssembly.Path),
            File.ReadAllBytes(typeof(SpirvKernelAttribute).Assembly.Location), kernel, SpirvKernelAbi.WebGpu);
        Assert.Empty(input.DeferredFunctions);
        Assert.All(input.Declarations.Functions, f => Assert.Empty(f.Body.Statements));
        Assert.All(input.Functions.Values, graph => Assert.Empty(graph.Loops));
        Assert.DoesNotContain(input.Functions.Values.SelectMany(g => g.Blocks).SelectMany(b => b.Instructions),
            i => i.Operation is ValueOperation.Local l && l.Name.StartsWith("sia_block_", StringComparison.Ordinal));
        if (name == nameof(ControlFlowShaders.IntegerControlFlow)) {
            var reachable = new HashSet<int>(); var pending = new Stack<int>(); pending.Push(0);
            while (pending.TryPop(out int id)) if (reachable.Add(id)) foreach (int target in kernel.ControlFlowGraph.Blocks[id].Successors) pending.Push(target);
            Assert.Equal(reachable.Count + 1, input.Functions[kernel.Name].Blocks.Count); // initialization plus original CIL blocks
            Assert.Contains(input.Functions[kernel.Name].Blocks, b => b.Terminator is ControlFlowTerminator.Conditional);
        }
        var before = input.Functions.ToDictionary(p => p.Key, p => ControlFlowPrinter.Write(p.Value));
        foreach (var function in input.Declarations.Functions) function.Body.Statements.Add(new Statement.Evaluate(new Expression.Reference("obsolete", ShaderType.U32)));
        ModuleValidator.Validate(input, native: true);
        var shared = CanonicalShaderPipeline.Prepare(input.Declarations, frontendGraphs: input.Functions,
            verifyFrontend: (graph, module) => ControlFlowVerifier.Validate(graph, module));
        Assert.Empty(shared.DeferredFunctions);
        var regions = CanonicalControlFlowRegions.Run(shared);
        Module structured;
        try { structured = StructuredControlFlowLowering.Run(regions); }
        catch (ShaderException error) { throw new InvalidOperationException(ControlFlowPrinter.Write(regions.Functions[kernel.Name]), error); }
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(structured, SpirvCompilationTarget.Default)));
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(structured, SpirvCompilationTarget.Default)));
        Assert.All(input.Functions, pair => Assert.Equal(before[pair.Key], ControlFlowPrinter.Write(pair.Value)));
    }

    [Fact]
    public void FreshCilReadsDoNotShareMutableGraphStorage()
    {
        var kernel = SpirvTestAssembly.GetKernel(typeof(ControlFlowShaders), nameof(ControlFlowShaders.SpeculativeSelection));
        var image = File.ReadAllBytes(SpirvTestAssembly.Path); var intrinsics = File.ReadAllBytes(typeof(SpirvKernelAttribute).Assembly.Location);
        var first = RuntimeShaderLowering.ReadCanonical(image, intrinsics, kernel, SpirvKernelAbi.WebGpu);
        var second = RuntimeShaderLowering.ReadCanonical(image, intrinsics, kernel, SpirvKernelAbi.WebGpu);
        var graph = first.Functions[kernel.Name]; var other = second.Functions[kernel.Name];
        Assert.NotSame(graph, other); Assert.Equal(ControlFlowPrinter.Write(graph), ControlFlowPrinter.Write(other));
        graph.Blocks[0].Instructions.Clear(); Assert.NotEmpty(other.Blocks[0].Instructions);
    }

    private static IEnumerable<Statement> Statements(Block body)
    {
        foreach (var statement in body.Statements) {
            yield return statement;
            IEnumerable<Block> nested = statement switch {
                Statement.Nested n => [n.Body], Statement.If i => [i.Accept, i.Reject],
                Statement.Loop l => [l.Body, l.Continuing], Statement.Switch s => s.Cases.Select(c => c.Body),
                _ => []
            };
            foreach (var block in nested) foreach (var child in Statements(block)) yield return child;
        }
    }
}
