using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class UniformityTests
{
    private const string Resources = "@group(0) @binding(0) var<storage,read> inputs:array<u32>;@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;";
    internal const string UniformHelper = Resources + "fn gate(v:u32)->u32{if v==0u{workgroupBarrier();}return v+5u;}@compute @workgroup_size(4) fn main(@builtin(local_invocation_index) i:u32){let v=gate(inputs[0]);if i==0u{outputs[0]=v;outputs[1]=inputs[0]*4u;}}";
    internal const string UniformLoopOverwrite = Resources + "@compute @workgroup_size(4) fn main(@builtin(local_invocation_index) i:u32){let count=inputs[0]+2u;var mixed=i;var j=0u;loop{mixed=0u;workgroupBarrier();j++;if j==count{break;}}if i==0u{outputs[0]=j;outputs[1]=mixed+17u;}}";
    internal const string ReconvergentBranches = Resources + "var<workgroup> values:array<u32,4>;@compute @workgroup_size(4) fn main(@builtin(local_invocation_index) i:u32){if (i&1u)==1u{values[i]=inputs[0]+1u;}else{values[i]=inputs[0]+3u;}workgroupBarrier();if i==0u{outputs[0]=values[0]+values[1]+values[2]+values[3];outputs[1]=values[3];}}";
    private const string Entry = "@compute @workgroup_size(4) fn main(@builtin(local_invocation_index) lane:u32)";
    [Theory]
    [InlineData("if lane==0u{workgroupBarrier();}")]
    [InlineData("if lane==0u{return;}workgroupBarrier();")]
    [InlineData("switch lane{case 0u:{return;}default:{}}workgroupBarrier();")]
    [InlineData("var flag=0u;if lane==0u{flag=1u;}if flag==0u{workgroupBarrier();}")]
    [InlineData("var flag=lane;var x=0u;loop{workgroupBarrier();if flag==0u{break;}x++;if x==2u{break;}}")]
    [InlineData("loop{if lane==0u{continue;}workgroupBarrier();break;}")]
    [InlineData("var a=array<u32,2>();a[lane&1u]=1u;if a[0]==0u{workgroupBarrier();}")]
    public void DivergentControlAndLoopCarriedValuesAreRejected(string body)
    {
        var error = Assert.Throws<ShaderException>(() => WgslReader.Parse(Entry + "{" + body + "}"));
        Assert.Contains("Uniformity violation", error.Message); Assert.True(error.Diagnostic.Span.Length > 0);
    }
    [Theory]
    [InlineData("if lane==0u{let x=1u;}workgroupBarrier();")]
    [InlineData("switch lane{case 0u:{}default:{}}workgroupBarrier();")]
    [InlineData("loop{if lane==0u{break;}break;}workgroupBarrier();")]
    [InlineData("var flag=lane;flag=0u;if flag==0u{workgroupBarrier();}")]
    [InlineData("var flag=0u;loop{workgroupBarrier();flag++;if flag==2u{break;}}")]
    [InlineData("var a=array<u32,2>();a[0]=1u;if a[0]==1u{workgroupBarrier();}")]
    public void ReconvergenceUniformOverwritesAndLoopFixedPointsAreAccepted(string body)
    {
        var module = WgslReader.Parse(Entry + "{" + body + "}");
        Assert.Empty(UniformityAnalysis.Validate(module));
        var output = CanonicalShaderPipeline.Run(module); Assert.Empty(UniformityAnalysis.Validate(output));
        Assert.Empty(UniformityAnalysis.Validate(WgslReader.Parse(WgslWriter.Write(module))));
        Assert.Empty(UniformityAnalysis.Validate(SpirvReader.Parse(SpirvWriter.Write(module))));
    }
    [Theory]
    [InlineData("fn fence(x:u32){if x==0u{workgroupBarrier();}}", "fence(lane);", false)]
    [InlineData("fn fence(x:u32){if x==0u{workgroupBarrier();}}", "fence(0u);", true)]
    [InlineData("fn unused(x:u32)->u32{return 0u;}", "if unused(lane)==0u{workgroupBarrier();}", true)]
    [InlineData("fn echo(x:u32)->u32{return x;}", "if echo(lane)==0u{workgroupBarrier();}", false)]
    [InlineData("fn branch(x:u32)->u32{if x==0u{return 0u;}return 1u;}", "if branch(lane)==0u{workgroupBarrier();}", false)]
    [InlineData("fn fence(){workgroupBarrier();}fn nested(){fence();}", "if lane==0u{nested();}", false)]
    [InlineData("fn fence(){workgroupBarrier();}fn nested(){fence();}", "if lane==0u{}nested();", true)]
    public void HelperSummariesDistinguishParameterControlAndReturnDependencies(string helper, string body, bool accepted)
    {
        string source = helper + Entry + "{" + body + "}";
        if (accepted) Assert.Empty(UniformityAnalysis.Validate(WgslReader.Parse(source)));
        else Assert.Contains("Uniformity violation", Assert.Throws<ShaderException>(() => WgslReader.Parse(source)).Message);
    }
    [Theory]
    [InlineData("fn change(p:ptr<function,u32>,v:u32){*p=v;}", "var x=0u;change(&x,lane);if x==0u{workgroupBarrier();}", false)]
    [InlineData("fn change(p:ptr<function,u32>,v:u32){*p=0u;}", "var x=lane;change(&x,lane);if x==0u{workgroupBarrier();}", true)]
    [InlineData("fn fence(p:ptr<function,u32>){if *p==0u{workgroupBarrier();}}", "var x=lane;fence(&x);", false)]
    [InlineData("fn fence(p:ptr<function,u32>){if *p==0u{workgroupBarrier();}}", "var x=0u;fence(&x);", true)]
    public void PointerContentsFlowThroughHelpers(string helper, string body, bool accepted)
    {
        string source = helper + Entry + "{" + body + "}";
        if (accepted) Assert.Empty(UniformityAnalysis.Validate(WgslReader.Parse(source)));
        else Assert.Throws<ShaderException>(() => WgslReader.Parse(source));
    }
    [Theory]
    [InlineData("workgroup_id", true)] [InlineData("global_invocation_id", false)]
    public void BuiltinInputsHaveTheirDefinedUniformity(string builtin, bool accepted)
    {
        string source = "@compute @workgroup_size(4) fn main(@builtin(" + builtin + ") id:vec3u){if id.x==0u{workgroupBarrier();}}";
        if (accepted) Assert.Empty(UniformityAnalysis.Validate(WgslReader.Parse(source))); else Assert.Throws<ShaderException>(() => WgslReader.Parse(source));
    }
    [Theory]
    [InlineData("read", true)] [InlineData("read_write", false)]
    public void ReadOnlyStorageWithUniformAddressIsUniform(string access, bool accepted)
    {
        string source = "@group(0) @binding(0) var<storage," + access + "> a:array<u32>;" + Entry + "{if a[0]==0u{workgroupBarrier();}}";
        if (accepted) Assert.Empty(UniformityAnalysis.Validate(WgslReader.Parse(source))); else Assert.Throws<ShaderException>(() => WgslReader.Parse(source));
    }
    [Theory]
    [InlineData("error", false)] [InlineData("warning", true)] [InlineData("info", true)] [InlineData("off", true)]
    public void FilterableDerivativeFailuresKeepTheirSeverity(string severity, bool accepted)
    {
        string source = "diagnostic(" + severity + ",derivative_uniformity);@fragment fn main(@builtin(position) p:vec4f)->@location(0) f32{if p.x>0.0f{return dpdx(p.y);}return 0.0f;}";
        if (!accepted) Assert.Throws<ShaderException>(() => WgslReader.Parse(source));
        else {
            var diagnostics = UniformityAnalysis.Validate(WgslReader.Parse(source));
            if (severity == "off") Assert.Empty(diagnostics);
            else Assert.Equal(Enum.Parse<DiagnosticSeverity>(severity, true), Assert.Single(diagnostics).Severity);
        }
    }
    [Fact]
    public void WorkgroupUniformLoadRequiresTheSamePointerAndReturnsAUniformValue()
    {
        const string shared = "var<workgroup> a:array<u32,4>;";
        Assert.Throws<ShaderException>(() => UniformityAnalysis.Validate(WgslReader.Parse(shared + Entry + "{_=workgroupUniformLoad(&a[lane]);}")));
        Assert.Empty(UniformityAnalysis.Validate(WgslReader.Parse(shared + Entry + "{let p=&a[0];let v=workgroupUniformLoad(p);if v==0u{workgroupBarrier();}}")));
    }
    [Fact]
    public void FiltersAtTheCalleeAreNotOverriddenByTheCaller()
    {
        Assert.Throws<ShaderException>(() => WgslReader.Parse("@diagnostic(error,derivative_uniformity) fn d(x:f32)->f32{return dpdx(x);}@diagnostic(off,derivative_uniformity) @fragment fn main(@builtin(position) p:vec4f)->@location(0) f32{if p.x>0.0f{return d(p.y);}return 0.0f;}"));
    }
    [Fact]
    public void SynchronizationRequirementsCannotBeFiltered()
    {
        Assert.Throws<ShaderException>(() => WgslReader.Parse("diagnostic(off,derivative_uniformity);diagnostic(off,subgroup_uniformity);" + Entry + "{if lane==0u{workgroupBarrier();}}"));
    }
    [Fact]
    public void TargetLegalizationChecksMutatedOrNativeSharedIr()
    {
        var module = WgslReader.Parse(Entry + "{workgroupBarrier();}");
        var main = module.Functions.Single(); var accept = new Block(); accept.Statements.Add(main.Body.Statements.Single());
        main.Body.Statements.Clear(); main.Body.Statements.Add(new Statement.If(
            new Expression.Binary("==", new Expression.Reference("lane", ShaderType.U32), Expression.U32(0), ShaderType.Bool), accept, new Block()));
        var error = Assert.Throws<ShaderException>(() => WgslWriter.Write(module));
        Assert.Equal(DiagnosticStage.WgslWrite, error.Diagnostic.Stage); Assert.Contains("Uniformity violation", error.Message);
        Assert.NotEmpty(SpirvWriter.Write(module)); // Native SPIR-V legality is not the WGSL source-language contract.
    }
    [Theory]
    [InlineData("1", true)] [InlineData("2", false)] [InlineData("size", false)]
    public void EntryBuiltinFactsRequireResolvedSingleInvocationDimensions(string dimension, bool lowered)
    {
        string source = "override size=1u;@group(0) @binding(0) var<storage,read_write> o:array<u32>;@compute @workgroup_size(" + dimension + ") fn main(@builtin(local_invocation_index) i:u32,@builtin(global_invocation_id) g:vec3u){o[0]=i;o[1]=g.x;}";
        var module = WgslReader.Parse(source); string before = WgslWriter.Emit(module);
        string result = WgslWriter.Emit(WgslEntryPointLowering.Run(module));
        Assert.Equal(lowered, result.Contains("o[0i] = 0u;", StringComparison.Ordinal));
        var after = WgslEntryPointLowering.Run(module);
        Assert.Equal(module.Functions[0].Body.Statements[1], after.Functions[0].Body.Statements[1]);
        Assert.Equal(before, WgslWriter.Emit(module));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ImplicitNativeStoragePlacesRemainNonuniform(bool array)
    {
        string declarations = array ? "@group(0) @binding(0) var<storage,read_write> data:array<u32>;" : "struct S{x:u32,}@group(0) @binding(0) var<storage,read_write> data:S;";
        var module = WgslReader.Parse(declarations + Entry + "{workgroupBarrier();}");
        var global = module.Globals.Single(); var root = new Expression.Reference("data", global.Type);
        Expression value = array ? new Expression.Access(root, Expression.U32(0), ShaderType.U32) : new Expression.Member(root, "x", ShaderType.U32);
        var accept = new Block(); accept.Statements.Add(new Statement.Barrier(false, true));
        module.Functions[0].Body.Statements.Clear(); module.Functions[0].Body.Statements.Add(new Statement.If(new Expression.Binary("==", value, Expression.U32(0), ShaderType.Bool), accept, new Block()));
        ModuleValidator.Validate(module);
        Assert.Throws<ShaderException>(() => UniformityAnalysis.Validate(module));
        Assert.Throws<ShaderException>(() => WgslWriter.Write(module));
    }
    [Theory]
    [InlineData("slot=0u;o[0]=slot;", true)]
    [InlineData("slot=1u;o[0]=slot;", false)]
    [InlineData("let p=&slot;*p=0u;o[0]=slot;", false)]
    [InlineData("{let slot=3u;o[0]=slot;}o[1]=slot;", false)]
    public void PrivateZeroFactsRequireNoChangingWritesEscapeOrShadowing(string body, bool folded)
    {
        var module = WgslReader.Parse("var<private> slot:u32;@group(0) @binding(0) var<storage,read_write> o:array<u32>;@compute @workgroup_size(2) fn main(){" + body + "}");
        string before = WgslWriter.Emit(module); string after = WgslWriter.Emit(WgslEntryPointLowering.Run(module));
        Assert.Equal(folded, before != after); Assert.Equal(before, WgslWriter.Emit(module));
    }
    [Theory]
    [InlineData(UniformHelper)] [InlineData(UniformLoopOverwrite)] [InlineData(ReconvergentBranches)]
    public void SharedUniformityFixturesActuallyMigrateBothReaderRoutes(string source)
    {
        var input = WgslReader.Parse(source);
        foreach (var module in new[] { input, SpirvReader.Parse(SpirvWriter.Write(input)) }) {
            var deferrals = new List<CanonicalDeferral>(); var traces = new List<CanonicalPassTrace>();
            var result = CanonicalShaderPipeline.Run(module, traces, deferrals);
            Assert.Empty(deferrals); Assert.Equal(module.Functions.Count * 3, traces.Count);
            Assert.All(module.Functions, f => Assert.NotSame(f, result.Functions.Single(r => r.Name == f.Name)));
            Assert.Empty(UniformityAnalysis.Validate(WgslReader.Parse(WgslWriter.Write(result))));
            ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(result)));
        }
    }
}
