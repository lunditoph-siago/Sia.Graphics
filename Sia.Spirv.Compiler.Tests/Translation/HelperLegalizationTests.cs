using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class HelperLegalizationTests
{
    [Fact]
    public void UncalledPointerLibraryFunctionsRemainAvailable()
    {
        var input = WgslReader.Parse("fn library(p:ptr<function,u32>)->u32{return *p;}");
        var output = WgslReader.Parse(WgslWriter.Write(input, SpirvCompilationTarget.Default));
        Assert.Equal("library", Assert.Single(output.Functions).Name);
        Assert.IsType<ShaderType.Pointer>(Assert.Single(output.Functions[0].Arguments).Type);
    }
    private static void AliasCalls(Block body)
    {
        for (int i = 0; i < body.Statements.Count; i++) {
            var statement = body.Statements[i];
            if (statement is Statement.Declare { Initializer: Expression.Call { Function: "writes" } call } declaration)
                body.Statements[i] = declaration with { Initializer = call with { Arguments = [call.Arguments[0], call.Arguments[0]] } };
            foreach (var child in statement switch {
                Statement.Nested n => new[] { n.Body }, Statement.If branch => [branch.Accept, branch.Reject],
                Statement.Loop loop => [loop.Body, loop.Continuing], Statement.Switch s => s.Cases.Select(c => c.Body), _ => []
            }) AliasCalls(child);
        }
    }
    [Fact]
    public void NativeFunctionPointerAliasesAreLegalizedForWgslWithoutCopyOut()
    {
        var input = NativeAliasModule();
        string before = WgslWriter.Emit(input);
        var output = WgslReader.Parse(WgslWriter.Write(input, SpirvCompilationTarget.Default)); ModuleValidator.Validate(output);
        Assert.DoesNotContain(output.Functions, f => f.Arguments.Any(a => a.Type is ShaderType.Pointer));
        Assert.Equal(new uint[] { 7, 1606 }, new CanonicalExecution(output, [5]).Run().Output);
        var native = SpirvReader.Parse(SpirvWriter.Write(input, SpirvCompilationTarget.Default)); ModuleValidator.Validate(native);
        Assert.DoesNotContain(native.Functions, f => f.Arguments.Any(a => a.Type is ShaderType.Pointer));
        var roundtrip = WgslReader.Parse(WgslWriter.Write(native, SpirvCompilationTarget.Default));
        Assert.Equal(new uint[] { 7, 1606 }, new CanonicalExecution(roundtrip, [5]).Run().Output);
        Assert.Equal(before, WgslWriter.Emit(input));
    }

    internal static Module NativeAliasModule()
    {
        var input = WgslReader.Parse(CanonicalPointerTests.PointerDistinct);
        AliasCalls(input.Functions.Single(f => f.Stage is not null).Body);
        return input;
    }

    [Fact]
    public void NativeSerializationKeepsTheSameAddressForBothArguments()
    {
        var binary = SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(NativeAliasModule()));
        var call = Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.FunctionCall && i.Operands.Length == 5);
        Assert.Equal(call.Operands[3], call.Operands[4]);
    }

    [Theory]
    [InlineData("off")] [InlineData("warning")] [InlineData("info")]
    public void InlinedCalleeFiltersOverrideTheCallerOnlyInsideTheExpandedBody(string severity)
    {
        var input = WgslReader.Parse($"@diagnostic({severity},derivative_uniformity) fn writes(a:ptr<function,f32>,b:ptr<function,f32>)->f32{{*a=*b;return dpdx(*b);}}@diagnostic(error,derivative_uniformity) @fragment fn main(@builtin(position) p:vec4f)->@location(0) f32{{var a=p.y;var b=p.y;if p.x>0.0{{return writes(&a,&b);}}return 0.0;}}");
        AliasCalls(input.Functions.Single(f => f.Stage is not null).Body);
        string emitted = WgslWriter.Write(input, SpirvCompilationTarget.Default);
        Assert.Contains($"@diagnostic({severity}, derivative_uniformity)", emitted);
        _ = WgslReader.Parse(emitted);
        Assert.DoesNotContain(input.Functions.Single(f => f.Stage is not null).DiagnosticFilters, f => f.Severity != DiagnosticSeverity.Error);
    }

    [Fact]
    public void InliningDoesNotInheritTheCallersDisabledRuleIntoAnUnfilteredCallee()
    {
        var input = WgslReader.Parse("@diagnostic(off,derivative_uniformity) fn writes(a:ptr<function,f32>,b:ptr<function,f32>)->f32{*a=*b;return dpdx(*b);}@diagnostic(off,derivative_uniformity) @fragment fn main(@builtin(position) p:vec4f)->@location(0) f32{var a=p.y;var b=p.y;if p.x>0.0{return writes(&a,&b);}return 0.0;}");
        input.Functions[0].DiagnosticFilters.Clear();
        var error = Assert.Throws<ShaderException>(() => WgslWriter.Write(input, SpirvCompilationTarget.Default));
        Assert.Equal(DiagnosticStage.WgslWrite, error.Diagnostic.Stage); Assert.Contains("Uniformity violation", error.Message);
    }
}
