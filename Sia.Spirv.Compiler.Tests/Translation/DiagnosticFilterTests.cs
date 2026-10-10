using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class DiagnosticFilterTests
{
    [Theory]
    [InlineData("off", DiagnosticSeverity.Off)]
    [InlineData("info", DiagnosticSeverity.Info)]
    [InlineData("warning", DiagnosticSeverity.Warning)]
    [InlineData("error", DiagnosticSeverity.Error)]
    public void ModuleAndFunctionSettingsSurviveWritingAndPipelineResolution(string severity, DiagnosticSeverity expected)
    {
        string source = $"diagnostic({severity}, derivative_uniformity); diagnostic(info, vendor.rule,); override size = 1u; @diagnostic(warning, derivative_uniformity) @diagnostic(off, vendor.rule) @compute @workgroup_size(size) fn main() {{}}";
        var module = WgslReader.Parse(source); ModuleValidator.Validate(module);
        Assert.Equal(new DiagnosticFilter(expected, "derivative_uniformity"), module.DiagnosticFilters[0]);
        Assert.Equal(new DiagnosticFilter(DiagnosticSeverity.Info, "rule", "vendor"), module.DiagnosticFilters[1]);
        var resolved = PipelineConstantResolver.Resolve(module, new Dictionary<string, double> { ["size"] = 2 });
        var back = WgslReader.Parse(WgslWriter.Write(resolved, SpirvCompilationTarget.Default)); ModuleValidator.Validate(back);
        Assert.Equal(module.DiagnosticFilters, back.DiagnosticFilters);
        Assert.Equal(module.Functions[0].DiagnosticFilters, back.Functions[0].DiagnosticFilters);
        Assert.NotEmpty(SpirvWriter.Write(back, SpirvCompilationTarget.Default));
        Assert.Equal(2, module.DiagnosticFilters.Count);
        Assert.Equal(2, module.Functions[0].DiagnosticFilters.Count);
    }

    [Fact]
    public void IdenticalDirectivesAreIdempotentAndDifferentNamespacesDoNotConflict()
    {
        var module = WgslReader.Parse("diagnostic(off, derivative_uniformity); diagnostic(off, derivative_uniformity); diagnostic(info, first.rule); diagnostic(error, second.rule); @diagnostic(info, first.rule) @diagnostic(error, second.rule) fn helper() {}");
        ModuleValidator.Validate(module);
        Assert.Equal(3, module.DiagnosticFilters.Count);
        Assert.Equal(2, module.Functions[0].DiagnosticFilters.Count);
        Assert.Equal(module.DiagnosticFilters, WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default)).DiagnosticFilters);
    }

    [Fact]
    public void UnknownAndUnicodeRulesAreRetained()
    {
        var module = WgslReader.Parse("diagnostic(info, unknown_rule); diagnostic(off, 供应商.规则); fn helper() {}");
        var back = WgslReader.Parse(WgslWriter.Write(module, SpirvCompilationTarget.Default));
        Assert.Equal(module.DiagnosticFilters, back.DiagnosticFilters);
    }

    [Theory]
    [InlineData("diagnostic(fatal, derivative_uniformity); fn main() {}")]
    [InlineData("diagnostic(off derivative_uniformity); fn main() {}")]
    [InlineData("diagnostic(off, derivative_uniformity, vendor.rule); fn main() {}")]
    [InlineData("diagnostic(off, vendor.rule.extra); fn main() {}")]
    [InlineData("diagnostic(off, 1); fn main() {}")]
    [InlineData("diagnostic(off, derivative_uniformity); diagnostic(error, derivative_uniformity); fn main() {}")]
    [InlineData("@diagnostic(off, derivative_uniformity) @diagnostic(off, derivative_uniformity) fn main() {}")]
    [InlineData("@diagnostic(off, derivative_uniformity) @diagnostic(error, derivative_uniformity) fn main() {}")]
    [InlineData("fn main() {} diagnostic(off, derivative_uniformity);")]
    [InlineData("@diagnostic(off, derivative_uniformity) const value = 1;")]
    [InlineData("@diagnostic(off, derivative_uniformity) var<private> value:u32;")]
    [InlineData("@diagnostic(off, derivative_uniformity) struct Value { value:u32 }")]
    [InlineData("@diagnostic(off, derivative_uniformity) alias Value = u32;")]
    [InlineData("fn main(@diagnostic(off, derivative_uniformity) value:u32) {}")]
    [InlineData("fn main() -> @diagnostic(off, derivative_uniformity) u32 { return 0u; }")]
    [InlineData("fn main() { @diagnostic(off, derivative_uniformity) return; }")]
    [InlineData("fn main() { @diagnostic(off, derivative_uniformity) @diagnostic(error, derivative_uniformity) {} }")]
    [InlineData("fn main() @diagnostic(off, derivative_uniformity) @diagnostic(error, derivative_uniformity) {}")]
    public void InvalidConflictingAndMisplacedSettingsAreRejected(string source)
    {
        Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default));
    }

    [Theory]
    [InlineData("fn main() { @diagnostic(off, derivative_uniformity) {} }")]
    [InlineData("fn main() @diagnostic(off, derivative_uniformity) {}")]
    [InlineData("fn main() { @diagnostic(off, derivative_uniformity) if true {} }")]
    [InlineData("fn main() { loop @diagnostic(info, vendor.rule) { continuing @diagnostic(off, derivative_uniformity) { break if true; } } }")]
    [InlineData("fn main() { switch 0u { case 0u, @diagnostic(off, derivative_uniformity) {} default: {} } }")]
    [InlineData("fn main() { switch 0u @diagnostic(info, vendor.rule) { case 0u: @diagnostic(off, vendor.rule) {} default: {} } }")]
    public void CompoundDiagnosticRangesSurviveTargetWriting(string source)
    {
        var module = WgslReader.Parse(source);
        string before = WgslWriter.Emit(module);
        string emitted = WgslWriter.Write(module, SpirvCompilationTarget.Default);
        Assert.Contains("@diagnostic(", emitted);
        ModuleValidator.Validate(WgslReader.Parse(emitted));
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)));
        Assert.Equal(before, WgslWriter.Emit(module));
    }

    [Fact]
    public void BlockFiltersOverrideFunctionSettingsWithoutLeakingPastTheBlock()
    {
        const string valid = "@diagnostic(error,derivative_uniformity) @fragment fn main(@builtin(position) p:vec4f)->@location(0) f32{var v=0.0;@diagnostic(off,derivative_uniformity) if p.x>0.0{v=dpdx(p.y);}return v;}";
        var input = WgslReader.Parse(valid);
        _ = WgslReader.Parse(WgslWriter.Write(input, SpirvCompilationTarget.Default));
        var error = Assert.Throws<ShaderException>(() => WgslReader.Parse(valid.Replace("return v;", "if p.x>0.0{v=dpdx(p.y);}return v;", StringComparison.Ordinal)));
        Assert.Contains("Uniformity violation", error.Message);
    }

    [Fact]
    public void BlockFiltersCannotSuppressExecutionBarrierRequirements()
    {
        const string source = "@compute @workgroup_size(4) fn main(@builtin(local_invocation_index) i:u32){@diagnostic(off,derivative_uniformity) if i>0u{workgroupBarrier();}}";
        Assert.Contains("Uniformity violation", Assert.Throws<ShaderException>(() => WgslReader.Parse(source)).Message);
    }

    [Fact]
    public void ContinuingFilterIncludesItsBreakIfExpression()
    {
        const string source = "@compute @workgroup_size(4) fn main(@builtin(local_invocation_index) i:u32){if i>0u{loop{continuing @diagnostic(off,subgroup_uniformity){break if subgroupAll(i>0u);}}}}";
        var input = WgslReader.Parse(source);
        var traces = new List<CanonicalPassTrace>(); var deferrals = new List<CanonicalDeferral>();
        var canonical = CanonicalShaderPipeline.Run(input, traces, deferrals);
        Assert.Empty(deferrals); Assert.Equal(3, traces.Count);
        Assert.Contains(traces, trace => trace.After.Contains("subgroup_uniformity:Off", StringComparison.Ordinal));
        _ = WgslReader.Parse(WgslWriter.Write(canonical, SpirvCompilationTarget.Default));
        Assert.Contains("Uniformity violation", Assert.Throws<ShaderException>(() => WgslReader.Parse(source.Replace("@diagnostic(off,subgroup_uniformity)", "", StringComparison.Ordinal))).Message);
    }

    [Theory]
    [InlineData((DiagnosticSeverity)4, "rule", null)]
    [InlineData(DiagnosticSeverity.Off, "rule.extra", null)]
    [InlineData(DiagnosticSeverity.Off, "1rule", null)]
    [InlineData(DiagnosticSeverity.Off, "", null)]
    [InlineData(DiagnosticSeverity.Off, "rule", "")]
    [InlineData(DiagnosticSeverity.Off, "rule", "vendor.extra")]
    [InlineData(DiagnosticSeverity.Off, "rule\n);fn injected(){}", null)]
    public void InvalidIrSettingsCannotProduceMalformedWgsl(DiagnosticSeverity severity, string rule, string? ns)
    {
        var module = new Module(); module.DiagnosticFilters.Add(new(severity, rule, ns));
        Assert.Throws<ShaderException>(() => WgslWriter.Write(module, SpirvCompilationTarget.Default));
    }

    [Fact]
    public void DuplicateIrFunctionSettingsAreRejected()
    {
        var module = WgslReader.Parse("@diagnostic(off, derivative_uniformity) fn helper() {}");
        module.Functions[0].DiagnosticFilters.Add(new(DiagnosticSeverity.Error, "derivative_uniformity"));
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(module));
    }

    [Theory]
    [InlineData("@fragment fn main(@builtin(position) p:vec4f) -> @location(0) f32 { if p.x > 0.0 { return dpdx(p.y); } return 0.0; }")]
    [InlineData("@compute @workgroup_size(4) fn main(@builtin(local_invocation_index) i:u32) { if i > 0u { workgroupBarrier(); } }")]
    [InlineData("fn helper() { workgroupBarrier(); } @compute @workgroup_size(4) fn main(@builtin(local_invocation_index) i:u32) { if i > 0u { helper(); } }")]
    [InlineData("var<workgroup> value:u32; @compute @workgroup_size(4) fn main(@builtin(local_invocation_index) i:u32) { if i > 0u { _ = workgroupUniformLoad(&value); } }")]
    public void UnfilteredUniformityFailuresAreRejectedRegardlessOfReferenceValidatorSettings(string source)
    {
        var error = Assert.Throws<ShaderException>(() => WgslReader.Parse(source));
        Assert.Equal(DiagnosticStage.Validation, error.Diagnostic.Stage);
        Assert.Contains("Uniformity violation", error.Message);
        Assert.True(error.Diagnostic.Span.Length > 0);
    }
}
