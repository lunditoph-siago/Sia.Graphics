using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class SpirvOutputPolicyTests
{
    internal const string RasterVertex = "@vertex fn main(@builtin(vertex_index) i:u32)->@builtin(position) vec4f{var p=array<vec2f,3>(vec2f(-1.0,0.0),vec2f(3.0,0.0),vec2f(-1.0,2.0));return vec4f(p[i],0.5,1.0);}";
    internal const string RasterVertexStruct = """
        struct Out{@builtin(position) position:vec4f,@location(0) @interpolate(flat) marker:u32,}
        var<private> calls:u32;
        fn make(i:u32)->vec4f{calls+=1u;var p=array<vec2f,3>(vec2f(-1.0,0.0),vec2f(3.0,0.0),vec2f(-1.0,2.0));return vec4f(p[i],0.5,1.0);}
        @vertex fn main(@builtin(vertex_index) i:u32)->Out{if(i==2u){return Out(make(i),calls);}return Out(make(i),calls);}
        """;
    internal const string Vertex = "@vertex fn main()->@builtin(position) vec4f{return vec4f(0.25,0.75,0.5,1.0);}";
    internal const string VertexStruct = "struct Out{@location(0) marker:f32,@builtin(position) position:vec4f,} @vertex fn main()->Out{return Out(7.0,vec4f(0.25,0.75,0.5,1.0));}";
    internal const string Depth = "@fragment fn main()->@builtin(frag_depth) f32{return 2.0;}";
    internal const string DepthStruct = "struct Out{@location(0) marker:u32,@builtin(frag_depth) depth:f32,} @fragment fn main()->Out{return Out(19u,-0.5);}";
    internal const string RasterDepthHigh = "struct Out{@location(0) marker:u32,@builtin(frag_depth) depth:f32,} @fragment fn main()->Out{return Out(19u,2.0);}";
    internal const string RasterDepthInRange = "struct Out{@location(0) marker:u32,@builtin(frag_depth) depth:f32,} @fragment fn main()->Out{return Out(19u,0.25);}";
    internal const string Mesh = """
        enable wgpu_mesh_shader;
        struct Vertex{@builtin(position) position:vec4f,@location(0) marker:f32,}
        struct Primitive{@builtin(point_index) index:u32,}
        struct Output{@builtin(vertices) vertices:array<Vertex,2>,@builtin(primitives) primitives:array<Primitive,2>,
            @builtin(vertex_count) vc:u32,@builtin(primitive_count) pc:u32,}
        var<workgroup> output:Output;
        @mesh(output) @workgroup_size(2) fn main(@builtin(local_invocation_index) i:u32){
            output.vertices[i].position=vec4f(0.25,0.75,0.5,1.0);output.vertices[i].marker=f32(i);
            output.primitives[i].index=i;if(i==0u){output.vc=2u;output.pc=2u;}}
        """;

    [Theory]
    [InlineData(Vertex)] [InlineData(VertexStruct)] [InlineData(Depth)] [InlineData(DepthStruct)] [InlineData(Mesh)]
    public void RawSerializationDoesNotChooseCoordinateOrDepthPolicy(string source)
    {
        var module = WgslReader.Parse(source);
        // Raw serialization now requires explicit mesh publication preparation as
        // well as layout. All entry policies are disabled in that prepared result.
        var prepared = SpirvEntryPointLowering.Run(module, false, false, zeroInitializeWorkgroupMemory: false);
        Assert.Equal(SpirvWriter.Emit(prepared).ToBytes(),
            SpirvWriter.Emit(prepared).ToBytes());
    }

    [Theory]
    [InlineData(Vertex, null, "position")] [InlineData(VertexStruct, "position", "position")]
    [InlineData(Depth, null, "depth")] [InlineData(DepthStruct, "depth", "depth")] [InlineData(Mesh, "position", "position")]
    public void PreparationOwnsTheOutputSelectionAndTypedConversion(string source, string? member, string policy)
    {
        var input = WgslReader.Parse(source); string original = WgslWriter.Emit(input);
        var prepared = ShaderTargetLowering.ForSpirv(input, null, true, true, true);
        var conversion = Assert.Single(prepared.OutputFunctions);
        Assert.Equal(("main", member), conversion.Key);
        Assert.Equal("sia_spv_output_" + policy, conversion.Value.Name);
        Assert.Same(conversion.Value, prepared.Module.Functions.Single(f => f.Name == conversion.Value.Name));
        Assert.Null(conversion.Value.Stage); Assert.Null(conversion.Value.ReturnBinding);
        Assert.Equal(conversion.Value.ReturnType, Assert.Single(conversion.Value.Arguments).Type);
        Assert.Empty(conversion.Value.Body.Statements);
        var graph = prepared.PhysicalLayout.ControlFlow[conversion.Value.Name].Graph;
        var block = Assert.Single(graph.Blocks);
        var definitions = block.Instructions.ToDictionary(i => i.Result!.Value.Id, i => i.Operation);
        var result = Assert.IsType<ControlFlowTerminator.Return>(block.Terminator).Value!.Value;
        if (policy == "position") {
            var vector = Assert.IsType<ValueOperation.Construct>(definitions[result.Id]);
            Assert.Equal(4, vector.Components.Count);
            var negate = Assert.IsType<ValueOperation.Unary>(definitions[vector.Components[1].Id]); Assert.Equal("-", negate.Operator);
            Assert.Equal("y", Assert.IsType<ValueOperation.Swizzle>(definitions[negate.Operand.Id]).Components);
            Assert.Equal(new[] { "x", "z", "w" }, new[] { 0, 2, 3 }.Select(i => Assert.IsType<ValueOperation.Swizzle>(definitions[vector.Components[i].Id]).Components));
        }
        else {
            var clamp = Assert.IsType<ValueOperation.Builtin>(definitions[result.Id]); Assert.Equal("clamp", clamp.Function);
            Assert.Equal(0f, Assert.IsType<ValueOperation.Literal>(definitions[clamp.Arguments[1].Id]).Value);
            Assert.Equal(1f, Assert.IsType<ValueOperation.Literal>(definitions[clamp.Arguments[2].Id]).Value);
        }
        var binary = SpirvWriter.Emit(prepared);
        Assert.Equal(binary.ToBytes(), SpirvWriter.Emit(prepared).ToBytes());
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
        Assert.Equal(original, WgslWriter.Emit(input));
    }

    [Theory]
    [InlineData(Vertex, true, false, true)] [InlineData(Vertex, false, true, false)]
    [InlineData(Depth, true, false, false)] [InlineData(Depth, false, true, true)]
    [InlineData(Mesh, true, false, true)] [InlineData(Mesh, false, true, false)]
    public void EachOptionSelectsOnlyItsOwnPolicy(string source, bool coordinate, bool depth, bool converted)
    {
        var input = WgslReader.Parse(source);
        var prepared = ShaderTargetLowering.ForSpirv(input, null, true, coordinate, depth);
        Assert.Equal(converted ? 1 : 0, prepared.OutputFunctions.Count);
        var options = new SpirvWriteOptions(AdjustCoordinateSpace: coordinate, ClampFragmentDepth: depth);
        Assert.Equal(SpirvWriter.Emit(prepared).ToBytes(), SpirvWriter.Write(input, options));
        Assert.Equal(SpirvBinary.Parse(SpirvWriter.Write(input, options)).ToWords(), SpirvWriter.WriteWords(input, options));
    }

    [Theory]
    [InlineData(Vertex)] [InlineData(VertexStruct)] [InlineData(Depth)] [InlineData(DepthStruct)] [InlineData(Mesh)]
    public void EntryBodyIsCalledOnceAndOutputConversionStaysAtTheBoundary(string source)
    {
        var prepared = ShaderTargetLowering.ForSpirv(WgslReader.Parse(source), null, true, true, true);
        var binary = SpirvWriter.Emit(prepared);
        uint Named(string name) => Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Name
            && SpirvBinary.ReadString(i.Operands.AsSpan(1), out _) == name).Operands[0];
        uint original = Named("main"), conversion = Named(Assert.Single(prepared.OutputFunctions).Value.Name);
        uint entry = Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.EntryPoint).Operands[1];
        int first = binary.Instructions.ToList().FindIndex(i => (Op)i.Opcode == Op.Function && i.Operands[1] == entry);
        var wrapper = binary.Instructions.Skip(first + 1).TakeWhile(i => (Op)i.Opcode != Op.FunctionEnd).ToList();
        var call = Assert.Single(wrapper, i => (Op)i.Opcode == Op.FunctionCall && i.Operands[2] == original);
        if (source == Mesh) {
            uint finish = Named(prepared.MeshPublications["main"].Function.Name);
            var publication = Assert.Single(wrapper, i => (Op)i.Opcode == Op.FunctionCall && i.Operands[2] == finish);
            Assert.True(wrapper.IndexOf(call) < wrapper.IndexOf(publication));
            int publishedStart = binary.Instructions.ToList().FindIndex(i => (Op)i.Opcode == Op.Function && i.Operands[1] == finish);
            var published = binary.Instructions.Skip(publishedStart + 1).TakeWhile(i => (Op)i.Opcode != Op.FunctionEnd).ToList();
            var converted = Assert.Single(published, i => (Op)i.Opcode == Op.FunctionCall && i.Operands[2] == conversion);
            var barrier = Assert.Single(published, i => (Op)i.Opcode == Op.ControlBarrier && i.Operands.Length == 3);
            Assert.True(published.IndexOf(barrier) < published.IndexOf(converted));
            uint loaded = converted.Operands[3];
            Assert.Contains(published, i => (Op)i.Opcode == Op.Load && i.Operands[1] == loaded);
            Assert.Contains(published, i => (Op)i.Opcode == Op.SetMeshOutputsEXT);
        }
        else {
            var converted = Assert.Single(wrapper, i => (Op)i.Opcode == Op.FunctionCall && i.Operands[2] == conversion);
            Assert.True(wrapper.IndexOf(call) < wrapper.IndexOf(converted));
            if (source is Vertex or Depth) Assert.Equal(call.Operands[1], converted.Operands[3]);
            else Assert.Contains(wrapper, i => (Op)i.Opcode == Op.CompositeExtract
                && i.Operands[1] == converted.Operands[3] && i.Operands[2] == call.Operands[1]);
        }
    }

    [Fact]
    public void SharedConversionsAvoidModuleNamesAndPreserveBorrowedBodies()
    {
        string source = Vertex.Replace("fn main", "fn first", StringComparison.Ordinal) + VertexStruct.Replace("fn main", "fn second", StringComparison.Ordinal)
            + Depth + "fn sia_spv_output_position(){} const sia_spv_output_depth=1u;";
        var input = WgslReader.Parse(source); string original = WgslWriter.Emit(input);
        var prepared = SpirvEntryPointLowering.Run(input, true, true);
        Assert.Equal(3, prepared.OutputFunctions.Count);
        Assert.Same(prepared.OutputFunctions[("first", null)], prepared.OutputFunctions[("second", "position")]);
        Assert.Equal("sia_spv_output_position_1", prepared.OutputFunctions[("first", null)].Name);
        Assert.Equal("sia_spv_output_depth_1", prepared.OutputFunctions[("main", null)].Name);
        foreach (var function in input.Functions) Assert.Same(function, prepared.Module.Functions.Single(f => f.Name == function.Name));
        Assert.Equal(original, WgslWriter.Emit(input));
        Assert.Equal(SpirvWriter.Write(input), SpirvWriter.Write(input));
    }

    // Execute the exact prepared pure conversion as compute, so fixed-function depth clamping cannot mask a shader defect.
    internal static Module PolicyProbe(bool depth)
    {
        var prepared = ShaderTargetLowering.ForSpirv(WgslReader.Parse(depth ? Depth : Vertex), null, true, true, true);
        var helper = Assert.Single(prepared.OutputFunctions).Value;
        string source = "@group(0) @binding(0) var<storage,read> inputs:array<u32>;@group(0) @binding(1) var<storage,read_write> outputs:array<u32>;"
            + (depth
                ? "fn conversion(value:f32)->f32{return clamp(value,0.0,1.0);}@compute @workgroup_size(1) fn main(){outputs[0]=bitcast<u32>(conversion(bitcast<f32>(inputs[0])));outputs[1]=inputs[1];}"
                : "fn conversion(value:vec4f)->vec4f{return vec4f(value.x,-value.y,value.z,value.w);}@compute @workgroup_size(1) fn main(){let value=conversion(vec4f(bitcast<f32>(inputs[0]),bitcast<f32>(inputs[1]),0.5,1.0));outputs[0]=bitcast<u32>(value.y);outputs[1]=bitcast<u32>(value.x);}");
        var probe = WgslReader.Parse(source.Replace("conversion", helper.Name, StringComparison.Ordinal));
        // This standalone legacy Module consumer crosses its explicit adapter;
        // the production conversion has a CFG body and no structured executable.
        var adapted = StructuredControlFlowLowering.Run(prepared.PhysicalLayout.ControlFlow[helper.Name].Graph, prepared.Module);
        probe.Functions.RemoveAll(f => f.Name == helper.Name); probe.Functions.Add(adapted);
        ModuleValidator.Validate(probe); return probe;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PreparedConversionsRemainValidWhenExecutedOutsideTheRasterPipeline(bool depth)
    {
        var probe = PolicyProbe(depth);
        var binary = SpirvWriter.Write(probe); ModuleValidator.Validate(SpirvReader.Parse(binary));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(probe)));
    }

    [Fact]
    public void ScalarOutputSelectorsSerializeAsCompositeExtract()
    {
        var prepared = SpirvEntryPointLowering.Run(WgslReader.Parse(Vertex), true, false);
        var binary = SpirvWriter.Emit(prepared);
        Assert.Equal(4, binary.Instructions.Count(i => (Op)i.Opcode == Op.CompositeExtract));
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.VectorShuffle);
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }
}
