using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;
using Xunit;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class MeshShaderTests
{
    internal const string Source = """
        enable wgpu_mesh_shader;
        struct Payload { visible:bool, mask:u32 }
        struct Vertex { @builtin(position) pos:vec4f, @location(0) color:vec4f }
        struct Primitive { @builtin(triangle_indices) indices:vec3u, @builtin(cull_primitive) cull:bool, @per_primitive @location(1) color:vec4f }
        struct Output { @builtin(vertices) v:array<Vertex,3>, @builtin(primitives) p:array<Primitive,1>, @builtin(vertex_count) vc:u32, @builtin(primitive_count) pc:u32 }
        var<task_payload> payload:Payload;
        var<workgroup> output:Output;
        fn visible()->bool { return payload.visible; }
        @task @payload(payload) @workgroup_size(2) fn task_main(@builtin(local_invocation_index) i:u32)->@builtin(mesh_task_size) vec3u {
            if i==0u {payload.visible=true; return vec3u(1);}
            return vec3u(2);
        }
        @mesh(output) @payload(payload) @workgroup_size(2) fn mesh_main(@builtin(local_invocation_index) i:u32) {
            if i==0u {
                output.vc=3u; output.pc=1u;
                output.v[0].pos=vec4f(0,1,0,1);
                output.p[0].indices=vec3u(0,1,2);
                output.p[0].cull=!visible();
                return;
            }
        }
        @fragment fn fragment_main(@per_primitive @location(1) color:vec4f)->@location(0) vec4f {return color;}
        """;

    [Theory]
    [InlineData("triangle_indices", "vec3u", "vec3u(0,1,2)", 5298u)]
    [InlineData("line_indices", "vec2u", "vec2u(0,1)", 5269u)]
    [InlineData("point_index", "u32", "0u", 27u)]
    public void StagePayloadOutputAndTopologySurviveWgslAndEmitNativeInstructions(string builtin, string type, string indices, uint mode)
    {
        string source = Source.Replace("triangle_indices", builtin).Replace("indices:vec3u", "indices:" + type).Replace("vec3u(0,1,2)", indices);
        var module = WgslReader.Parse(source); ModuleValidator.Validate(module);
        string canonical = WgslWriter.Write(module); var back = WgslReader.Parse(canonical); ModuleValidator.Validate(back);
        Assert.Equal("payload", back.Functions.Single(f => f.Stage == ShaderStage.Task).TaskPayload);
        Assert.Equal("output", back.Functions.Single(f => f.Stage == ShaderStage.Mesh).MeshOutput);
        Assert.Contains("@per_primitive", canonical); Assert.Contains("@builtin(vertices)", canonical);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
        Assert.Equal(0x00010400u, binary.Version);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands is [5283]);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Variable && i.Operands[2] == 5402);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.ExecutionMode && i.Operands.Length == 2 && i.Operands[1] == mode);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.SetMeshOutputsEXT);
        int emit = Array.FindIndex(binary.Instructions.ToArray(), i => (Op)i.Opcode == Op.EmitMeshTasksEXT);
        Assert.True(emit >= 0); Assert.Equal(Op.FunctionEnd, (Op)binary.Instructions[emit + 1].Opcode);
        Assert.All(binary.Instructions.Where(i => (Op)i.Opcode == Op.EntryPoint), i =>
        {
            _ = SpirvBinary.ReadString(i.Operands.AsSpan(2), out int length);
            uint[] interfaces = i.Operands[(2 + length)..]; Assert.Equal(interfaces.Distinct().Count(), interfaces.Length);
        });
        var imported = SpirvReader.Parse(binary.ToBytes()); ModuleValidator.Validate(imported);
        string reverse = WgslWriter.Write(imported); ModuleValidator.Validate(WgslReader.Parse(reverse));
        Assert.Contains(imported.Functions, f => f.Stage == ShaderStage.Task && f.TaskPayload is not null);
        Assert.Contains(imported.Functions, f => f.Stage == ShaderStage.Mesh && f.MeshOutput is not null);
        var reemitted = SpirvBinary.Parse(SpirvWriter.Write(imported));
        Assert.Contains(reemitted.Instructions, i => (Op)i.Opcode == Op.SetMeshOutputsEXT);
        Assert.Contains(reemitted.Instructions, i => (Op)i.Opcode == Op.EmitMeshTasksEXT);
    }

    [Theory]
    [InlineData("@payload(payload) @workgroup_size(2) fn task_main", "@workgroup_size(2) fn task_main")]
    [InlineData("@mesh(output)", "@mesh(payload)")]
    [InlineData("@per_primitive @location(1) color:vec4f }", "@location(1) color:vec4f }")]
    [InlineData("indices:vec3u", "indices:vec2u")]
    [InlineData("@builtin(vertex_count) vc:u32", "@builtin(vertex_count) vc:i32")]
    [InlineData("@builtin(position) pos:vec4f", "@location(2) pos:vec4f")]
    [InlineData("output.p[0].cull=!visible();", "payload.visible=false;")]
    [InlineData("return payload.visible;", "payload.visible=false; return payload.visible;")]
    [InlineData("var<task_payload> payload:Payload", "var<private> payload:Payload")]
    [InlineData("enable wgpu_mesh_shader;", "")]
    [InlineData("@per_primitive @location(1) color:vec4f }", "@per_primitive color:vec4f }")]
    [InlineData("@fragment fn fragment_main", "@workgroup_size(1) @fragment fn fragment_main")]
    public void InvalidMeshContractsFail(string from, string to)
    {
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(WgslReader.Parse(Source.Replace(from, to))));
    }

    [Fact]
    public void PipelineResolutionPreservesEntryAssociations()
    {
        var module = WgslReader.Parse(Source.Replace("struct Payload", "@id(3) override size=2u; struct Payload").Replace("@workgroup_size(2)", "@workgroup_size(size)"));
        var resolved = PipelineConstantResolver.Resolve(module, new Dictionary<string, double> { ["3"] = 4 });
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(resolved)));
        Assert.Equal("payload", resolved.Functions.Single(f => f.Stage == ShaderStage.Task).TaskPayload);
        Assert.Equal("output", resolved.Functions.Single(f => f.Stage == ShaderStage.Mesh).MeshOutput);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(resolved));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.ExecutionMode && i.Operands is [_, 17, 4, 1, 1]);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void NativeOutputBlocksAndWholeArrayCopiesLowerToMeshWorkgroupData(int mode)
    {
        var module = SpirvReader.Parse(AggregateFixture(mode).ToBytes()); ModuleValidator.Validate(module);
        Assert.DoesNotContain(module.Globals, g => g.Type is ShaderType.BindingArray);
        string source = WgslWriter.Write(module); ModuleValidator.Validate(WgslReader.Parse(source));
        var output = SpirvBinary.Parse(SpirvWriter.Write(module));
        Assert.Contains(output.Instructions, i => (Op)i.Opcode == Op.SetMeshOutputsEXT);
    }

    internal static SpirvBinary AggregateFixture(int mode)
    {
        static SpirvInstruction I(Op op, params uint[] args) => new((ushort)op, args);
        var code = new List<SpirvInstruction>
        {
            I(Op.Capability, 1), I(Op.Capability, 5283), I(Op.Extension, SpirvBinary.StringWords("SPV_EXT_mesh_shader")), I(Op.MemoryModel, 0, 1),
            I(Op.EntryPoint, new uint[] { 5365, 30 }.Concat(SpirvBinary.StringWords("main")).Concat([12u,15u]).ToArray()),
            I(Op.ExecutionMode, 30, 17, 1, 1, 1), I(Op.ExecutionMode, 30, 26, 1), I(Op.ExecutionMode, 30, 5270, 1), I(Op.ExecutionMode, 30, 27),
            I(Op.Decorate, 9, 2), I(Op.MemberDecorate, 9, 0, 11, 0), I(Op.Decorate, 15, 11, 5294),
            I(Op.TypeVoid, 1), I(Op.TypeFunction, 2, 1), I(Op.TypeFloat, 3, 32), I(Op.TypeVector, 4, 3, 4), I(Op.TypeInt, 5, 32, 0),
            I(Op.Constant, 5, 6, 1), I(Op.Constant, 5, 7, 0), I(Op.TypeStruct, 9, 4), I(Op.TypeArray, 10, 9, 6),
            I(Op.TypePointer, 11, 3, 10), I(Op.Variable, 11, 12, 3), I(Op.TypeArray, 13, 5, 6), I(Op.TypePointer, 14, 3, 13), I(Op.Variable, 14, 15, 3),
            I(Op.TypePointer, 16, 3, 9), I(Op.Constant, 3, 17, 0), I(Op.Constant, 3, 18, 0x3f800000),
            I(Op.ConstantComposite, 4, 19, 17, 18, 17, 18), I(Op.ConstantComposite, 9, 20, 19), I(Op.ConstantComposite, 10, 21, 20),
            I(Op.TypePointer, 24, 3, 5),
            I(Op.Function, 1, 30, 0, 2), I(Op.Label, 31), I(Op.SetMeshOutputsEXT, 6, 6),
        };
        if (mode == 0) code.Add(I(Op.Store, 12, 21));
        else if (mode == 1) { code.Add(I(Op.AccessChain, 16, 33, 12, 7)); code.Add(I(Op.Store, 33, 20)); code.Add(I(Op.Load, 9, 34, 33)); code.Add(I(Op.Store, 33, 34)); }
        else if (mode == 2) { code.Add(I(Op.Store, 12, 21)); code.Add(I(Op.Load, 10, 34, 12)); code.Add(I(Op.Store, 12, 34)); }
        else { code.Add(I(Op.Store, 12, 21)); code.Add(I(Op.CopyMemory, 12, 12)); }
        code.AddRange([I(Op.AccessChain, 24, 35, 15, 7), I(Op.Store, 35, 7), I(Op.Return), I(Op.FunctionEnd)]);
        return new() { Version = 0x00010400, Bound = 36, Instructions = code };
    }

    [Fact]
    public void FragmentPerPrimitiveInUnregisteredIrStructureRequiresMeshVersion()
    {
        var module = new Module();
        var input = new ShaderType.Structure("Input", [new("color", ShaderType.F32, Binding: new(Location: 0, PerPrimitive: true))]);
        var entry = new ShaderFunction("main") { Stage = ShaderStage.Fragment };
        entry.Arguments.Add(new("input", input)); module.Functions.Add(entry);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module)); Assert.Equal(0x00010400u, binary.Version);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Decorate && i.Operands is [_, 5271]);
    }

    [Fact]
    public void NativeFragmentBlockInheritsPerPrimitiveDecoration()
    {
        var module = SpirvReader.Parse(FragmentBlockFixture().ToBytes()); ModuleValidator.Validate(module);
        Assert.True(module.Functions.Single(f => f.Stage == ShaderStage.Fragment).Arguments.Single().Binding?.PerPrimitive);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    internal static SpirvBinary FragmentBlockFixture()
    {
        static SpirvInstruction I(Op op, params uint[] args) => new((ushort)op, args);
        return new() { Version = 0x00010400, Bound = 12, Instructions = [
            I(Op.Capability, 1), I(Op.Capability, 5283), I(Op.Extension, SpirvBinary.StringWords("SPV_EXT_mesh_shader")), I(Op.MemoryModel, 0, 1),
            I(Op.EntryPoint, new uint[] { 4, 10 }.Concat(SpirvBinary.StringWords("main")).Concat([8u]).ToArray()), I(Op.ExecutionMode, 10, 7),
            I(Op.Decorate, 3, 2), I(Op.MemberDecorate, 3, 0, 30, 0), I(Op.Decorate, 8, 5271),
            I(Op.TypeVoid, 1), I(Op.TypeFloat, 2, 32), I(Op.TypeStruct, 3, 2), I(Op.TypePointer, 4, 1, 3), I(Op.TypeFunction, 5, 1), I(Op.Variable, 4, 8, 1),
            I(Op.Function, 1, 10, 0, 5), I(Op.Label, 11), I(Op.Return), I(Op.FunctionEnd),
        ] };
    }
}
