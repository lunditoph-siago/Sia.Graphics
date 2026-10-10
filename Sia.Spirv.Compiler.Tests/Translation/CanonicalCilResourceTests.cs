using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Validation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.Valid;
using Sia.Spirv.Compiler.IL;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalCilResourceTests
{
    [Theory]
    [InlineData(nameof(CilResourceShaders.ImageAliases))]
    [InlineData(nameof(CilResourceShaders.ArrayAliases))]
    [InlineData(nameof(CilResourceShaders.SamplerAliases))]
    public void RealResourceLocalsAndHelperReturnsCompileThroughBothTargets(string name)
    {
        var image = File.ReadAllBytes(typeof(CilResourceShaders).Assembly.Location);
        var core = File.ReadAllBytes(typeof(SpirvKernelAttribute).Assembly.Location);
        var frontend = new SpirvFrontend().Analyze(image, core);
        Assert.Empty(frontend.Diagnostics);
        var kernel = Assert.Single(frontend.Kernels, k => k.Name == name);
        var module = new SpirvCompiler().CompileModule(new SpirvModuleCompilationRequest(image, kernel.MetadataToken, core));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)));
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)));
    }

    [Theory]
    [InlineData(nameof(CilResourceShaders.ImageAliases))]
    [InlineData(nameof(CilResourceShaders.ArrayAliases))]
    [InlineData(nameof(CilResourceShaders.SamplerAliases))]
    public void ResourceLocationsBecomeOwnedSsaBeforeTargetIdentityDispatch(string name)
    {
        var image = File.ReadAllBytes(typeof(CilResourceShaders).Assembly.Location);
        var core = File.ReadAllBytes(typeof(SpirvKernelAttribute).Assembly.Location);
        var kernel = new SpirvFrontend().Analyze(image, core).Kernels.Single(k => k.Name == name);
        var source = RuntimeShaderLowering.ReadCanonical(image, core, kernel, SpirvKernelAbi.WebGpu);
        var before = source.Functions.ToDictionary(p => p.Key, p => ControlFlowPrinter.Write(p.Value));
        Assert.Empty(source.DeferredFunctions);
        Assert.Contains(source.Functions.Values.SelectMany(g => g.Blocks).SelectMany(b => b.Parameters), p => CanonicalTypes.Resource(p.Type));
        Assert.DoesNotContain(source.Functions.Values.SelectMany(g => g.Blocks).SelectMany(b => b.Instructions),
            i => i.Operation is ValueOperation.Local && i.Result?.Type is ShaderType.Pointer p && CanonicalTypes.Resource(p.Base)
                || i.Operation is ValueOperation.Load l && l.Pointer.Type is ShaderType.Pointer pointer && CanonicalTypes.Resource(pointer.Base)
                || i.Operation is ValueOperation.Store s && CanonicalTypes.Resource(s.Value.Type));
        if (name != nameof(CilResourceShaders.ArrayAliases))
            Assert.Contains(source.Functions.Values.SelectMany(g => g.Blocks).SelectMany(b => b.Instructions).Select(i => i.Operation).OfType<ValueOperation.Call>(),
                c => CanonicalTypes.Resource(c.ReturnType) && (c.CalleeEffects & ShaderEffects.WriteMemory) != 0);
        foreach (var function in source.Declarations.Functions) function.Body = new Block { Statements = { new Statement.Evaluate(new Expression.Reference("obsolete", ShaderType.U32)) } };
        ModuleValidator.Validate(source, native: true);
        var shared = CanonicalShaderPipeline.Prepare(source.Declarations, frontendGraphs: source.Functions,
            verifyFrontend: (graph, module) => ControlFlowVerifier.Validate(graph, module));
        var target = CanonicalResourceLowering.Run(CanonicalHelperInliner.RunReferences(shared));
        Assert.Empty(target.DeferredFunctions);
        Assert.DoesNotContain(target.Functions.Values.SelectMany(g => g.Blocks).SelectMany(b => b.Parameters), p => CanonicalTypes.Resource(p.Type));
        var structured = StructuredControlFlowLowering.Run(CanonicalControlFlowRegions.Run(target));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(structured, SpirvCompilationTarget.Default)));
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(structured, SpirvCompilationTarget.Default)));
        Assert.All(source.Functions, p => Assert.Equal(before[p.Key], ControlFlowPrinter.Write(p.Value)));
    }
}
