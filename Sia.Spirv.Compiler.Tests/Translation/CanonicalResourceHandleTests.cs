using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class CanonicalResourceHandleTests
{
    internal const string QueryControl = "enable wgpu_ray_query;"
        + "@group(0) @binding(0) var<storage,read> inputs:array<u32>;@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;"
        + "fn step(q:ptr<function,ray_query>,p:ptr<function,u32>,n:u32)->u32{let handle=q;*p+=5u;if n==1u{return 7u;}for(var i=0u;i<2u;i++){*p+=i;}return *p;}"
        + "@compute @workgroup_size(1) fn main(){var query:ray_query;var state=inputs[0];let result=step(&query,&state,inputs[0]);outputs[0]=result;outputs[1]=state;}";
    internal const string TextureHelper = "@group(0) @binding(0) var image:texture_2d<u32>;@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;"
        + "fn read(t:texture_2d<u32>,p:vec2i)->vec4u{return textureLoad(t,p,0);}@compute @workgroup_size(1) fn main(){outputs[0]=read(image,vec2i(0)).x;}";
    internal const string SamplerHelper = "@group(0) @binding(0) var image:texture_2d<f32>;@group(0) @binding(1) var sampling:sampler;"
        + "fn read(t:texture_2d<f32>,s:sampler)->vec4f{return textureSampleLevel(t,s,vec2f(0.5),0.0);}@fragment fn main()->@location(0) vec4f{return read(image,sampling);}";
    internal const string QueryLoop = "enable wgpu_ray_query;@group(0) @binding(0) var scene:acceleration_structure;"
        + "@compute @workgroup_size(1) fn main(){for(var i=0u;i<2u;i++){var query:ray_query;let handle=&query;_=rayQueryProceed(handle);rayQueryInitialize(handle,scene,RayDesc(0u,255u,0.0,1.0,vec3f(0),vec3f(1)));while(rayQueryProceed(handle)){}rayQueryTerminate(handle);}}";

    [Theory]
    [InlineData(QueryControl)] [InlineData(TextureHelper)] [InlineData(SamplerHelper)] [InlineData(RayQueryTests.Source)]
    public void BorrowedHandlesEnterCanonicalGraphsWithoutBecomingData(string source)
    {
        var input = WgslReader.Parse(source); var canonical = CanonicalShaderPipeline.Prepare(input);
        Assert.Empty(canonical.DeferredFunctions);
        var before = canonical.Functions.ToDictionary(p => p.Key, p => ControlFlowPrinter.Write(p.Value));
        var bodies = input.Functions.ToDictionary(f => f.Name, f => f.Body);
        try {
            foreach (var function in input.Functions) function.Body = new();
            var lowered = ShaderTargetLowering.PrepareSpirv(canonical, source == RayQueryTests.Source ? new Dictionary<string, double> { ["7"] = 0.25 } : null);
            Assert.All(canonical.Functions, p => Assert.Equal(before[p.Key], ControlFlowPrinter.Write(p.Value)));
            Assert.DoesNotContain(lowered.Declarations.Functions, f => f.Stage is null && f.Arguments.Any(a => a.Type is ShaderType.Pointer));
            var binary = SpirvWriter.Emit(SpirvEntryPointLowering.Run(lowered, true, true));
            Assert.NotEmpty(binary.Instructions);
            ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
        } finally { foreach (var function in input.Functions) function.Body = bodies[function.Name]; }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void QueryAllocationKeepsIdentityAndVertexReturnTypeWithoutImplicitDataStore(bool vertexReturn)
    {
        var module = WgslReader.Parse("enable wgpu_ray_query;" + (vertexReturn ? "enable wgpu_ray_query_vertex_return;" : "")
            + "@compute @workgroup_size(1) fn main(){var query:ray_query" + (vertexReturn ? "<vertex_return>" : "") + ";let handle=&query;_=rayQueryProceed(handle);}");
        var canonical = CanonicalShaderPipeline.Prepare(module); Assert.Empty(canonical.DeferredFunctions);
        var graph = canonical.Functions["main"];
        var local = Assert.Single(graph.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Local);
        Assert.False(((ValueOperation.Local)local.Operation).ZeroInitialize);
        Assert.Equal(new ShaderType.Pointer(new ShaderType.RayQuery(vertexReturn), AddressSpace.Function), local.Result!.Value.Type);
        Assert.DoesNotContain(graph.Blocks.SelectMany(b => b.Instructions), i => i.Operation is ValueOperation.Store);
        var call = Assert.Single(graph.Blocks.SelectMany(b => b.Instructions).Select(i => i.Operation).OfType<ValueOperation.Builtin>());
        var alias = Assert.Single(graph.Blocks.SelectMany(b => b.Instructions).Select(i => i.Operation).OfType<ValueOperation.Let>(),
            let => let.Value.Type is ShaderType.Pointer { Base: ShaderType.RayQuery });
        Assert.Equal(local.Result.Value, alias.Value); Assert.Equal(local.Result.Value.Type, call.Arguments.Single().Type);
        var copied = graph.Copy();
        var copiedLocal = copied.Blocks.SelectMany(b => b.Instructions).Single(i => i.Result == local.Result);
        var block = copied.Blocks.Single(b => b.Instructions.Contains(copiedLocal));
        block.Instructions[block.Instructions.IndexOf(copiedLocal)] = copiedLocal with { Operation = ((ValueOperation.Local)copiedLocal.Operation) with { ZeroInitialize = true } };
        Assert.Contains("illegal Local", Assert.Throws<ShaderException>(() => ControlFlowVerifier.Validate(copied, module)).Message);
        ControlFlowVerifier.Validate(graph, module);
    }

    [Fact]
    public void PerIterationQueryAllocationRemainsInsideItsLoopInTheExplicitTargetAdapter()
    {
        var canonical = CanonicalShaderPipeline.Prepare(WgslReader.Parse(QueryLoop)); Assert.Empty(canonical.DeferredFunctions);
        var graph = canonical.Functions["main"]; string before = ControlFlowPrinter.Write(graph);
        var adapted = StructuredControlFlowLowering.Run(graph, canonical.Declarations);
        Assert.DoesNotContain(adapted.Body.Statements, s => s is Statement.Declare { Type: ShaderType.RayQuery });
        var loop = Assert.Single(adapted.Body.Statements.OfType<Statement.Loop>());
        var declaration = Assert.Single(Statements(loop.Body), s => s is Statement.Declare { Type: ShaderType.RayQuery });
        Assert.True(((Statement.Declare)declaration).Initialize);
        Assert.Equal(before, ControlFlowPrinter.Write(graph));
        var module = new Module(); module.Enables.UnionWith(canonical.Declarations.Enables);
        module.Globals.AddRange(canonical.Declarations.Globals); module.Structures.AddRange(canonical.Declarations.Structures);
        module.Functions.Add(adapted); ModuleValidator.Validate(module);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module, SpirvCompilationTarget.Default)));
    }

    [Fact]
    public void QueryEffectFactsDistinguishUpdatesFromNativeAndGuardedReads()
    {
        foreach (string name in new[] { "rayQueryProceed", "rayQueryInitialize", "spirvRayQueryGenerateIntersectionKHR" }) {
            Assert.True(ShaderBuiltinEffects.IsKnown(name));
            Assert.Equal(ShaderEffects.Resource | ShaderEffects.ReadMemory | ShaderEffects.WriteMemory, ShaderBuiltinEffects.For(name));
        }
        foreach (string name in new[] { "rayQueryGetCandidateIntersection", "spirvRayQueryGetRayFlagsKHR", "getCommittedHitVertexPositions" }) {
            Assert.True(ShaderBuiltinEffects.IsKnown(name));
            Assert.Equal(ShaderEffects.Resource | ShaderEffects.ReadMemory, ShaderBuiltinEffects.For(name));
        }
        Assert.False(ShaderBuiltinEffects.IsKnown("spirvRayQueryPretendKHR"));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void QueryStateResultsDoNotProveUniformBarrierControl(bool canonical)
    {
        var module = WgslReader.Parse("enable wgpu_ray_query;@compute @workgroup_size(4) fn main(){var query:ray_query;if rayQueryProceed(&query){}}");
        var function = Assert.Single(module.Functions);
        var branch = Assert.Single(function.Body.Statements.OfType<Statement.If>());
        branch.Accept.Statements.Add(new Statement.Barrier(true, false));
        var graphs = new Dictionary<string, ControlFlowFunction>();
        if (canonical) {
            Assert.True(StructuredControlFlowReader.TryRead(function, module, out var graph, out var reason), reason);
            ControlFlowAnalysis.RemoveUnreachable(graph!); ControlFlowVerifier.Validate(graph!, module); graphs.Add(function.Name, graph!);
        }
        Assert.Contains("Uniformity violation", Assert.Throws<ShaderException>(() => UniformityAnalysis.Validate(module, graphs)).Message);
    }

    private static IEnumerable<Statement> Statements(Block body)
    {
        foreach (var statement in body.Statements) {
            yield return statement;
            foreach (var block in statement switch {
                Statement.Nested n => new[] { n.Body }, Statement.If i => new[] { i.Accept, i.Reject },
                Statement.Loop l => new[] { l.Body, l.Continuing }, Statement.Switch s => s.Cases.Select(c => c.Body), _ => [] })
                foreach (var child in Statements(block)) yield return child;
        }
    }
}
