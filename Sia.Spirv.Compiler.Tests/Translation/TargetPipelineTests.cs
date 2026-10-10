using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class TargetPipelineTests
{
    [Fact]
    public void TargetLayoutLoweringDoesNotChangeTheNativeBufferAbi()
    {
        var input = SpirvReader.Parse(UniformMemoryTests.UnsupportedStrideFixture().ToBytes());
        var globals = input.Globals.ToArray();
        var functions = input.Functions.ToArray();
        var bodies = functions.Select(f => f.Body).ToArray();
        var enables = input.Enables.Order().ToArray();
        var native = SpirvWriter.Write(input, SpirvCompilationTarget.Default);

        var wgsl = WgslWriter.Write(input, SpirvCompilationTarget.Default);
        ModuleValidator.Validate(WgslReader.Parse(wgsl));
        Assert.Equal(native, SpirvWriter.Write(input, SpirvCompilationTarget.Default));
        Assert.Equal(SpirvBinary.Parse(native).ToWords(), SpirvWriter.WriteWords(input, SpirvCompilationTarget.Default));
        Assert.Equal(wgsl, WgslWriter.Write(input, SpirvCompilationTarget.Default));
        Assert.Equal(globals, input.Globals);
        Assert.Equal(functions, input.Functions);
        Assert.Equal(enables, input.Enables.Order());
        for (int i = 0; i < bodies.Length; i++) Assert.Same(bodies[i], input.Functions[i].Body);
        Assert.Contains(SpirvBinary.Parse(native).Instructions,
            i => (Op)i.Opcode == Op.Decorate && i.Operands is [_, 6, 16]);
    }

    [Fact]
    public void QueryLegalizationIsTargetSpecificAndDoesNotRemoveCallerHelpers()
    {
        var input = WgslReader.Parse(QueryHelperTests.ControlSource);
        var helpers = input.Functions.ToArray();
        var native = SpirvWriter.Write(input, SpirvCompilationTarget.Default);
        var wgsl = WgslWriter.Write(input, SpirvCompilationTarget.Default);
        Assert.DoesNotContain("fn step", wgsl);
        Assert.DoesNotContain("fn truth", wgsl);
        Assert.Equal(helpers, input.Functions);
        Assert.Contains(input.Functions, f => f.Name == "step"
            && f.Arguments.Any(a => a.Type is ShaderType.Pointer { Base: ShaderType.RayQuery }));
        Assert.Equal(native, SpirvWriter.Write(input, SpirvCompilationTarget.Default));
        Assert.Equal(SpirvBinary.Parse(native).ToWords(), SpirvWriter.WriteWords(input, SpirvCompilationTarget.Default));
        Assert.Equal(wgsl, WgslWriter.Write(input, SpirvCompilationTarget.Default));
        ModuleValidator.Validate(SpirvReader.Parse(native));
        ModuleValidator.Validate(WgslReader.Parse(wgsl));
    }

    [Fact]
    public void FailedTargetResolutionLeavesTheModuleAvailableToOtherTargets()
    {
        var input = WgslReader.Parse("@id(7) override size:u32; @compute @workgroup_size(size) fn main(){}");
        var constant = Assert.Single(input.Constants);
        var dimension = input.Functions[0].WorkgroupSize[0];
        var wgsl = WgslWriter.Write(input, SpirvCompilationTarget.Default);
        Assert.Throws<ShaderException>(() => SpirvWriter.Write(input, SpirvCompilationTarget.Default,
            new() { PipelineConstants = new Dictionary<string, double> { ["missing"] = 4 } }));
        var options = new SpirvWriteOptions { PipelineConstants = new Dictionary<string, double> { ["7"] = 4 } };
        var native = SpirvWriter.Write(input, SpirvCompilationTarget.Default, options);
        Assert.Equal(SpirvBinary.Parse(native).ToWords(), SpirvWriter.WriteWords(input, SpirvCompilationTarget.Default, options));
        Assert.Contains(SpirvBinary.Parse(native).Instructions,
            i => (Op)i.Opcode == Op.ExecutionMode && i.Operands.Skip(1).SequenceEqual(new uint[] { 17, 4, 1, 1 }));
        Assert.Same(constant, Assert.Single(input.Constants));
        Assert.True(constant.IsOverride);
        Assert.Null(constant.Value);
        Assert.Same(dimension, input.Functions[0].WorkgroupSize[0]);
        Assert.Equal(wgsl, WgslWriter.Write(input, SpirvCompilationTarget.Default));
    }

    [Fact]
    public void InvalidInputIsRejectedAtBothTargetBoundaries()
    {
        var module = new Module();
        var main = new ShaderFunction("main") { Stage = ShaderStage.Compute };
        main.Body.Statements.Add(new Statement.Evaluate(new Expression.Reference("missing", ShaderType.U32)));
        module.Functions.Add(main);
        Assert.Equal(DiagnosticStage.Validation, Assert.Throws<ShaderException>(() => ShaderTargetLowering.ForWgsl(module)).Diagnostic.Stage);
        Assert.Equal(DiagnosticStage.Validation, Assert.Throws<ShaderException>(() => ShaderTargetLowering.ForSpirv(module, null)).Diagnostic.Stage);
    }
}
