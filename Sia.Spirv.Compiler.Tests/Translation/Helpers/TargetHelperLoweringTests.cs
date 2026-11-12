using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;

namespace Sia.Spirv.Compiler.Tests.Translation.Helpers;

public sealed class TargetHelperLoweringTests
{
    [Fact]
    public void PreservesPointerIdentityAndEffectOrderAcrossRepeatedCalls()
    {
        var module = WgslReader.Parse("""
            @group(0) @binding(0) var<storage, read_write> data: array<u32>;
            fn update(p: ptr<function, u32>) {
                *p = *p + 2u;
                *p = *p * 3u;
            }
            @compute @workgroup_size(1) fn main() {
                var value = data[0];
                update(&value);
                update(&value);
                data[1] = value;
            }
            """);

        var canonical = TargetHelperLowering.Run(CanonicalShaderPipeline.Prepare(module));

        Assert.Equal("main", Assert.Single(canonical.Declarations.Functions).Name);
        Assert.Equal(new uint[] { 7, 87 }, WgslFixtureInterpreter.Execute(
            WgslWriter.Write(module, SpirvCompilationTarget.Default)).Memory);
    }

    [Fact]
    public void ProjectsOnlyCallersThatReachAnUnreadableHelper()
    {
        var module = WgslReader.Parse("""
            fn deferred(p: ptr<function, u32>) { *p = *p + 2u; }
            fn bridge(p: ptr<function, u32>) { deferred(p); }
            fn ordinary(p: ptr<function, u32>) { *p = *p * 3u; }
            @compute @workgroup_size(1) fn main() {
                var value = 7u;
                bridge(&value);
                ordinary(&value);
            }
            fn independent() {
                var value = 11u;
                ordinary(&value);
            }
            """);
        // A native cooperative matrix local is valid structured IR but is not imported by this reader.
        var deferred = module.Functions.Single(f => f.Name == "deferred");
        deferred.Body.Statements.Insert(0, new Statement.Declare("matrix",
            new ShaderType.CooperativeMatrix(8, 8, ShaderType.F32, CooperativeRole.C), null) { Initialize = false });
        var canonical = CanonicalShaderPipeline.Prepare(module);
        Assert.Contains("deferred", canonical.DeferredFunctions.Keys);

        var adapted = StructuredHelperAdapter.Prepare(canonical,
            new HashSet<string> { "deferred", "bridge", "ordinary" });

        Assert.Contains("main", adapted.DeferredFunctions.Keys);
        Assert.DoesNotContain("main", adapted.Functions.Keys);
        Assert.Same(canonical.Functions["independent"], adapted.Functions["independent"]);
        var body = adapted.Declarations.Functions.Single(f => f.Name == "main").Body;
        Assert.Empty(ControlFlowAnalysis.Calls(body));
        Assert.Equal(new[] { "bridge", "ordinary" }, ControlFlowAnalysis.Calls(
            module.Functions.Single(f => f.Name == "main").Body));

        var lowered = TargetHelperLowering.Run(canonical);
        Assert.Equal(new[] { "main", "independent" }, lowered.Declarations.Functions.Select(f => f.Name));
        Assert.Equal("main", Assert.Single(lowered.DeferredFunctions).Key);
        Assert.Empty(ControlFlowAnalysis.Calls(lowered.Declarations.Functions.Single(f => f.Name == "independent"),
            lowered.Functions));
    }
}
