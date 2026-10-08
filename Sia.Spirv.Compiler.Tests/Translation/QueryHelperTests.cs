using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;
using Xunit;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class QueryHelperTests
{
    // No traversal operations: independent GPU controls can remove only the
    // unused query handles after inlining and exercise the remaining effects.
    internal const string ControlSource = """
        enable wgpu_ray_query;
        @group(0) @binding(0) var<storage,read> inputs:array<u32>;
        @group(0) @binding(1) var<storage,read_write> outputs:array<u32>;
        fn bump(p:ptr<function,u32>,v:u32)->u32 { *p=*p*10u+v; return v; }
        fn step(q:ptr<function,ray_query>,p:ptr<function,u32>,v:u32)->u32 {
            let handle=q;
            *p=*p*10u+v;
            if v%3u==0u { return v+10u; }
            loop {
                switch v%3u {
                    case 1u: { return v+20u; }
                    default: {
                        var j=0u;
                        loop { if j==2u { return v+30u; } j++; }
                    }
                }
            }
            return 99u;
        }
        fn truth(q:ptr<function,ray_query>,p:ptr<function,u32>,v:u32)->bool {
            _=step(q,p,v); return true;
        }
        fn take(q:ptr<function,ray_query>,p:ptr<function,u32>,a:u32,b:u32)->u32 {
            *p+=1000u; return a*100u+b;
        }
        @compute @workgroup_size(1) fn main(@builtin(global_invocation_id) id:vec3u) {
            var query:ray_query; var state=0u; let n=inputs[id.x]; let base=id.x*12u;
            outputs[base]=step(&query,&state,n); outputs[base+1u]=state;
            let skipped=false && truth(&query,&state,9u);
            let skipped2=true || truth(&query,&state,8u);
            let chosen=true && truth(&query,&state,2u); outputs[base+2u]=state;
            outputs[base+3u]=bump(&state,3u)+step(&query,&state,1u); outputs[base+4u]=state;
            outputs[base+5u]=select(step(&query,&state,4u),step(&query,&state,5u),false);
            outputs[base+6u]=state;
            outputs[base+7u]=take(&query,&state,bump(&state,6u),step(&query,&state,7u));
            outputs[base+8u]=state;
            var index=0u; var values=array<u32,2>(100u,200u);
            values[index]=step(&query,&index,1u); outputs[base+9u]=values[0];
            outputs[base+10u]=values[1]; outputs[base+11u]=index;
        }
        """;

    [Fact]
    public void NestedReturnsShortCircuitAndOperandOrderHaveValidLowerings()
    {
        var input = WgslReader.Parse(ControlSource); ModuleValidator.Validate(input);
        var output = QueryHelperInliner.Run(input); ModuleValidator.Validate(output);
        Assert.DoesNotContain(output.Functions, f => f.Arguments.Any(a => a.Type is ShaderType.Pointer { Base: ShaderType.RayQuery }));
        Assert.Equal(5, input.Functions.Count); // The pass does not mutate its input.
        string wgsl = WgslWriter.Write(input); ModuleValidator.Validate(WgslReader.Parse(wgsl));
        Assert.DoesNotContain("fn step", wgsl); Assert.DoesNotContain("fn truth", wgsl);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(input)));
    }

    [Theory]
    [InlineData("if n>0u { return n; } return 2u;")]
    [InlineData("switch n { case 0u: {return 3u;} default: {return 4u;} }")]
    [InlineData("loop { loop { if n>0u {return 5u;} break;} return 6u; }")]
    [InlineData("{let n=7u; if n==7u {return n;}} return n;")]
    public void EarlyReturnAndShadowingPathsRoundtrip(string body)
    {
        string source = "enable wgpu_ray_query; fn helper(q:ptr<function,ray_query>,n:u32)->u32 {"+body+"} @compute @workgroup_size(1) fn main(){var q:ray_query; _=helper(&q,1u);}";
        var input = WgslReader.Parse(source); ModuleValidator.Validate(input);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(input)));
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(input)));
    }

    [Fact]
    public void HelperDiagnosticFiltersAreNotSilentlyDropped()
    {
        var module = WgslReader.Parse("enable wgpu_ray_query; @diagnostic(off,derivative_uniformity) fn helper(q:ptr<function,ray_query>){} @compute @workgroup_size(1) fn main(){var q:ray_query; helper(&q);}");
        Assert.Contains("diagnostic", Assert.Throws<ShaderException>(() => WgslWriter.Write(module)).Message);
        Assert.Contains("diagnostic", Assert.Throws<ShaderException>(() => SpirvWriter.Write(module)).Message);
    }

    [Fact]
    public void EarlyReturningHelperCanBeCalledFromContinuing()
    {
        const string source = "enable wgpu_ray_query; fn helper(q:ptr<function,ray_query>,n:u32)->u32 {if n>1u{return n+1u;}return 0u;} @compute @workgroup_size(1) fn main(){var query:ray_query;var n=0u;loop{n++;continuing{_=helper(&query,n);break if n>=3u;}}}";
        var module = WgslReader.Parse(source); ModuleValidator.Validate(module);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module)));
    }

    [Fact]
    public void NestedSwitchBreakDoesNotDuplicateTheFollowingCall()
    {
        var module = WgslReader.Parse("fn effect(){} @compute @workgroup_size(1) fn main(@builtin(global_invocation_id) id:vec3u){switch id.x{default:{if id.x==0u{break;}}}effect();}");
        var back = SpirvReader.Parse(SpirvWriter.Write(module)); ModuleValidator.Validate(back);
        string effect = back.Functions.Single(f => f.Name.StartsWith("n_effect_", StringComparison.Ordinal)).Name;
        Assert.Single(back.Functions.SelectMany(f => AllStatements(f.Body)).OfType<Statement.Evaluate>(),
            s => s.Value is Expression.Call c && c.Function == effect);
    }

    [Fact]
    public void MixedInitializationCannotProduceStaleDescriptorState()
    {
        var module = WgslReader.Parse("enable wgpu_ray_query; @group(0) @binding(0) var scene:acceleration_structure; @compute @workgroup_size(1) fn main(){var query:ray_query; rayQueryInitialize(&query,scene,RayDesc(0u,255u,0.0,1.0,vec3f(0),vec3f(1)));}");
        var initialize = (Expression.Call)((Statement.Evaluate)module.Functions[0].Body.Statements[1]).Value;
        module.Functions[0].Body.Statements.Add(new Statement.Declare("flags", ShaderType.U32,
            new Expression.Call("spirvRayQueryGetRayFlagsKHR", [initialize.Arguments[0]], ShaderType.U32), false));
        ModuleValidator.Validate(module);
        var error = Assert.Throws<ShaderException>(() => WgslWriter.Write(module));
        Assert.Equal(DiagnosticStage.WgslWrite, error.Diagnostic.Stage); Assert.Contains("guarded state tracking", error.Message);
        // Native writing retains the original raw getter and guarded initialization.
        Assert.Contains(SpirvBinary.Parse(SpirvWriter.Write(module)).Instructions, i => (Op)i.Opcode == Op.RayQueryGetRayFlagsKHR);
    }

    [Fact]
    public void RawDescriptorStateTracksAliasesAndRepeatedInitialization()
    {
        var module = RawDescriptorModule(); ModuleValidator.Validate(module);
        var binary = SpirvWriter.Write(module);
        string wgsl = WgslWriter.Write(SpirvReader.Parse(binary));
        Assert.DoesNotContain("spirvRayQuery", wgsl);
        ModuleValidator.Validate(WgslReader.Parse(wgsl));
        var lowered = QueryStateLowering.Run(QueryHelperInliner.Run(module));
        ModuleValidator.Validate(lowered);
        var stores = AllStatements(lowered.Functions.Single(f => f.Name == "main").Body).OfType<Statement.Store>()
            .Where(s => s.Target is Expression.Reference r && r.Name.StartsWith("naga_query_state_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, stores.Length); // Two fields, two initializations of the same aliased query.
        Assert.Equal(stores[0].Target, stores[2].Target); Assert.Equal(stores[1].Target, stores[3].Target);
        Assert.Equal(2, AllStatements(module.Functions[0].Body).OfType<Statement.Evaluate>().Count());
    }

    [Fact]
    public void GeneratedStateNamesDoNotShadowStructureConstructors()
    {
        var module = RawDescriptorModule();
        var structure = new ShaderType.Structure("naga_query_state_0", [new("value", ShaderType.U32)]);
        module.Structures.Add(structure);
        module.Functions.Single(f => f.Name == "main").Body.Statements.Add(new Statement.Declare("value", structure,
            new Expression.Construct(structure, [Expression.U32(1)]), false));
        var output = QueryStateLowering.Run(QueryHelperInliner.Run(module));
        Assert.DoesNotContain(AllStatements(output.Functions.Single(f => f.Name == "main").Body),
            s => s is Statement.Declare { Name: "naga_query_state_0" });
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    internal static Module RawDescriptorModule()
    {
        var module = WgslReader.Parse("""
            enable wgpu_ray_query;
            @group(0) @binding(0) var scene:acceleration_structure;
            @group(0) @binding(1) var<storage,read_write> outputs:array<u32>;
            fn initialize(q:ptr<function,ray_query>,near:f32,flags:u32) {
                rayQueryInitialize(q,scene,RayDesc(flags,255u,near,10.0,vec3f(0),vec3f(1)));
                rayQueryTerminate(q);
            }
            fn inspect(q:ptr<function,ray_query>)->vec2u { return vec2u(0u,0u); }
            @compute @workgroup_size(1) fn main() {
                var query:ray_query; let handle=&query;
                initialize(handle,0.25,1u); let first=inspect(handle);
                initialize(handle,0.5,2u); let second=inspect(handle);
                outputs[0]=first.x; outputs[1]=first.y; outputs[2]=second.x; outputs[3]=second.y;
            }
            """);
        var body = module.Functions[0].Body;
        for (int i = 0; i < body.Statements.Count; i++)
        {
            if (body.Statements[i] is not Statement.Evaluate { Value: Expression.Call call }) continue;
            if (call.Function == "rayQueryTerminate")
                body.Statements[i] = new Statement.Evaluate(call with { Function = "spirvRayQueryTerminateKHR" });
            else if (call.Function == "rayQueryInitialize")
            {
                var desc = (Expression.Construct)call.Arguments[2]; var c = desc.Components;
                body.Statements[i] = new Statement.Evaluate(new Expression.Call("spirvRayQueryInitializeKHR",
                    [call.Arguments[0], call.Arguments[1], c[0], c[1], c[4], c[2], c[5], c[3]], new ShaderType.Void()));
            }
        }
        var pointer = new Expression.Reference("q", module.Functions[1].Arguments[0].Type);
        module.Functions[1].Body.Statements[0] = new Statement.Return(new Expression.Construct(new ShaderType.Vector(2, ShaderType.U32),
            [new Expression.Call("spirvRayQueryGetRayFlagsKHR", [pointer], ShaderType.U32),
             new Expression.Convert(ShaderType.U32, new Expression.Call("spirvRayQueryGetRayTMinKHR", [pointer], ShaderType.F32), true)]));
        return module;
    }

    private static IEnumerable<Statement> AllStatements(Block body)
    {
        foreach (var s in body.Statements)
        {
            yield return s;
            IEnumerable<Block> children = s switch
            {
                Statement.Nested n => [n.Body], Statement.If i => [i.Accept, i.Reject],
                Statement.Loop l => [l.Body, l.Continuing], Statement.Switch sw => sw.Cases.Select(c => c.Body), _ => []
            };
            foreach (var child in children) foreach (var nested in AllStatements(child)) yield return nested;
        }
    }
}
