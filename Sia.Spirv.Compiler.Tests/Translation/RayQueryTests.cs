using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;
using Xunit;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class RayQueryTests
{
    internal const string Source = """
        enable wgpu_ray_query;
        @id(7) override near=0.1f;
        @group(0) @binding(0) var scene: acceleration_structure;
        @group(0) @binding(1) var<storage, read_write> output: array<u32>;
        fn trace(acs: acceleration_structure) -> RayIntersection {
            var query: ray_query;
            let handle = &query;
            _ = rayQueryProceed(handle);
            rayQueryInitialize(handle, acs, RayDesc(RAY_FLAG_NONE, 255u, near, 100.0f, vec3f(0), vec3f(0,1,0)));
            while (rayQueryProceed(handle)) {
                let hit = rayQueryGetCandidateIntersection(handle);
                if (hit.kind == RAY_QUERY_INTERSECTION_AABB) { rayQueryGenerateIntersection(handle, 0.5); }
                else { rayQueryConfirmIntersection(handle); }
            }
            _ = rayQueryProceed(handle);
            rayQueryTerminate(handle);
            return rayQueryGetCommittedIntersection(handle);
        }
        @compute @workgroup_size(1) fn main() { let hit=trace(scene); output[0]=hit.kind; }
        """;

    [Fact]
    public void QueryHandlesAndOperationsSurviveWgslAndSpirvWriting()
    {
        var source = WgslReader.Parse(Source); ModuleValidator.Validate(source);
        string wgsl = WgslWriter.Write(source); ModuleValidator.Validate(WgslReader.Parse(wgsl));
        Assert.Contains("RayIntersection", wgsl); Assert.DoesNotContain("struct RayIntersection", wgsl);
        var bytes = SpirvWriter.Write(source, new() { PipelineConstants = new Dictionary<string, double> { ["7"] = 0.25 } });
        var binary = SpirvBinary.Parse(bytes);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.RayQueryInitializeKHR);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.RayQueryGenerateIntersectionKHR);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.RayQueryConfirmIntersectionKHR);
        var back = SpirvReader.Parse(bytes); ModuleValidator.Validate(back);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(back)));
        // Raw committed getters can run during traversal in SPIR-V. WGSL's
        // getter has stronger restrictions, so refuse an unproven conversion.
        var error = Assert.Throws<ShaderException>(() => WgslWriter.Write(back));
        Assert.Equal(DiagnosticStage.WgslWrite, error.Diagnostic.Stage);
        Assert.Contains("traversal", error.Message);
    }

    [Fact]
    public void CandidateOnlyQueriesRoundtripToWgsl()
    {
        string source = Source.Replace("return rayQueryGetCommittedIntersection(handle);", "return rayQueryGetCandidateIntersection(handle);")
            .Replace("rayQueryGenerateIntersection(handle, 0.5);", "rayQueryConfirmIntersection(handle);");
        var module = SpirvReader.Parse(ShaderTranslator.WgslToSpirv(source));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Theory]
    [InlineData("getCommittedHitVertexPositions")]
    [InlineData("getCandidateHitVertexPositions")]
    public void VertexPositionFetchRequiresMatchingQueryAndAccelerationTypes(string getter)
    {
        string source = "enable wgpu_ray_query; enable wgpu_ray_query_vertex_return; @group(0) @binding(0) var scene: acceleration_structure<vertex_return>; @compute @workgroup_size(1) fn main(){ var query:ray_query<vertex_return>; rayQueryInitialize(&query,scene,RayDesc(0u,255u,0.0,1.0,vec3f(0),vec3f(1))); while(rayQueryProceed(&query)){} _="+getter+"(&query); }";
        var module = WgslReader.Parse(source); ModuleValidator.Validate(module);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands is [5391]);
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(WgslReader.Parse(source.Replace("var query:ray_query<vertex_return>", "var query:ray_query"))));
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(WgslReader.Parse(source.Replace("var scene: acceleration_structure<vertex_return>", "var scene: acceleration_structure"))));
    }

    [Theory]
    [InlineData("var query:ray_query = ray_query();")]
    [InlineData("var query:ray_query; let copy=query;")]
    [InlineData("var query:ray_query; var other:ray_query; query=other;")]
    [InlineData("var query:u32; _=rayQueryProceed(&query);")]
    [InlineData("var query:ray_query; rayQueryGenerateIntersection(&query,1u);")]
    [InlineData("var query:ray_query; rayQueryProceed();")]
    [InlineData("var query:ray_query; rayQueryInitialize(&query,1u,RayDesc());")]
    [InlineData("var query:ray_query; _=getCandidateHitVertexPositions(&query);")]
    public void InvalidQueryUsesAreRejected(string body)
    {
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(WgslReader.Parse("enable wgpu_ray_query; @compute @workgroup_size(1) fn main(){"+body+"}")));
    }

    [Fact]
    public void QueryTypesNeedTheirEnablesAndCannotBeModuleVariables()
    {
        Assert.Throws<ShaderException>(() => WgslReader.Parse("@compute @workgroup_size(1) fn main(){var query:ray_query;}"));
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(WgslReader.Parse("enable wgpu_ray_query; var<private> query:ray_query;")));
        Assert.Throws<ShaderException>(() => WgslReader.Parse("enable wgpu_ray_query; @compute @workgroup_size(1) fn main(){var query:ray_query<vertex_return>;}"));
    }

    [Fact]
    public void RayHandlesAreNeverLoadedStoredOrZeroInitialized()
    {
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(Source));
        uint queryType = Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.TypeRayQueryKHR).Operands[0];
        var pointers = binary.Instructions.Where(i => (Op)i.Opcode == Op.TypePointer && i.Operands[2] == queryType).Select(i => i.Operands[0]).ToHashSet();
        var variables = binary.Instructions.Where(i => (Op)i.Opcode == Op.Variable && pointers.Contains(i.Operands[0])).Select(i => i.Operands[1]).ToHashSet();
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.Load && i.Operands[0] == queryType);
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.ConstantNull && i.Operands[0] == queryType);
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.Store && variables.Contains(i.Operands[0]));
    }

    [Fact]
    public void DescriptorMembersMatchReferenceNames()
    {
        string source = "enable wgpu_ray_query; @compute @workgroup_size(1) fn main(){let d=RayDesc(0u,255u,0.0,1.0,vec3f(0),vec3f(1)); _=d.tmin; _=d.tmax;}";
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(WgslReader.Parse(source))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AccelerationStructureDescriptorArraysRetainResourceTypes(bool nonuniform)
    {
        string source = Source.Replace("enable wgpu_ray_query;", "enable wgpu_ray_query; enable wgpu_binding_array;")
            .Replace("var scene: acceleration_structure;", "var scene: binding_array<acceleration_structure,4>;")
            .Replace("fn main()", "fn main(@builtin(global_invocation_id) id:vec3u)")
            .Replace("trace(scene)", nonuniform ? "trace(scene[id.x%4u])" : "trace(scene[0])");
        var module = WgslReader.Parse(source); ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
        if(nonuniform) Assert.Contains(binary.Instructions,i=>(Op)i.Opcode==Op.Decorate && i.Operands is [_,5300]);
        var back = SpirvReader.Parse(binary.ToBytes()); ModuleValidator.Validate(back);
        Assert.Contains(back.Globals,g=>g.Type is ShaderType.BindingArray { Element:ShaderType.AccelerationStructure, Length:4 });
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(back)));
    }

    [Fact]
    public void RawQueryHelpersAndOpaquePointerParametersRoundtripWithoutCopies()
    {
        SpirvInstruction I(Op op, params uint[] operands) => new((ushort)op, operands);
        var binary = new SpirvBinary { Bound = 60, Version = 0x00010300, Instructions = [
            I(Op.Capability,1), I(Op.Capability,4472), I(Op.Extension,SpirvBinary.StringWords("SPV_KHR_ray_query")), I(Op.MemoryModel,0,1),
            I(Op.EntryPoint,new uint[]{5,40}.Concat(SpirvBinary.StringWords("main")).ToArray()), I(Op.ExecutionMode,40,17,1,1,1),
            I(Op.Decorate,20,34,0), I(Op.Decorate,20,33,0),
            I(Op.TypeVoid,1), I(Op.TypeFloat,2,32), I(Op.TypeInt,3,32,0), I(Op.TypeBool,4), I(Op.TypeVector,5,2,3),
            I(Op.TypeRayQueryKHR,6), I(Op.TypeAccelerationStructureKHR,7), I(Op.TypePointer,8,7,6), I(Op.TypePointer,9,0,7),
            I(Op.TypeFunction,10,1), I(Op.TypeFunction,11,1,8,9),
            I(Op.Constant,2,12,0), I(Op.Constant,2,13,0x3f800000), I(Op.Constant,3,14,0), I(Op.Constant,3,15,255),
            I(Op.ConstantComposite,5,16,12,12,12), I(Op.Variable,9,20,0),
            I(Op.Function,1,30,0,11), I(Op.FunctionParameter,8,31), I(Op.FunctionParameter,9,32), I(Op.Label,33),
            I(Op.Load,7,34,32), I(Op.RayQueryInitializeKHR,31,34,14,15,16,12,16,13), I(Op.RayQueryProceedKHR,4,35,31),
            I(Op.RayQueryGetRayTMinKHR,2,36,31), I(Op.RayQueryGetRayFlagsKHR,3,37,31), I(Op.Return), I(Op.FunctionEnd),
            I(Op.Function,1,40,0,10), I(Op.Label,41), I(Op.Variable,8,42,7), I(Op.FunctionCall,1,43,30,42,20), I(Op.Return), I(Op.FunctionEnd)] };
        var module = SpirvReader.Parse(binary.ToBytes()); ModuleValidator.Validate(module);
        Assert.IsType<ShaderType.AccelerationStructure>(module.Functions.Single(f=>f.Name=="sia_fn30").Arguments[1].Type);
        var output = SpirvBinary.Parse(SpirvWriter.Write(module));
        Assert.Contains(output.Instructions,i=>(Op)i.Opcode==Op.RayQueryGetRayTMinKHR);
        Assert.Contains(output.Instructions,i=>(Op)i.Opcode==Op.RayQueryGetRayFlagsKHR);
        ModuleValidator.Validate(SpirvReader.Parse(output.ToBytes()));
        string wgsl = WgslWriter.Write(module);
        ModuleValidator.Validate(WgslReader.Parse(wgsl));
        Assert.DoesNotContain("spirvRayQueryGetRay", wgsl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LogicalBooleanStructureOffsetsDoNotBecomeBufferLayout(bool buffer)
    {
        SpirvInstruction I(Op op,params uint[] args)=>new((ushort)op,args);
        var instructions = new List<SpirvInstruction> { I(Op.Capability,1), I(Op.MemoryModel,0,1),
            I(Op.MemberDecorate,5,0,35,0), I(Op.MemberDecorate,5,1,35,4), I(Op.MemberDecorate,5,2,35,12),
            I(Op.TypeFloat,1,32), I(Op.TypeInt,2,32,0), I(Op.TypeBool,3), I(Op.TypeVector,4,1,2), I(Op.TypeStruct,5,2,4,3) };
        if(buffer) { instructions.Insert(2,I(Op.Decorate,5,2));instructions.Add(I(Op.TypePointer,6,12,5));instructions.Add(I(Op.Decorate,7,34,0));instructions.Add(I(Op.Decorate,7,33,0));instructions.Add(I(Op.Variable,6,7,12)); }
        var module = SpirvReader.Parse(new SpirvBinary { Bound=8, Version=0x00010300, Instructions=instructions }.ToBytes());
        if(buffer) Assert.Throws<ShaderException>(()=>ModuleValidator.Validate(module));
        else { ModuleValidator.Validate(module); Assert.All(module.Structures.Single().Members,m=>Assert.Null(m.Offset)); }
    }
}
