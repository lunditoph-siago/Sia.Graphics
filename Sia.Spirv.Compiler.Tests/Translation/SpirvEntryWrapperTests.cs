using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class SpirvEntryWrapperTests
{
    internal const string InputStruct = """
        struct Input{@builtin(local_invocation_index) index:u32,@builtin(global_invocation_id) id:vec3u,}
        var<workgroup> memory:array<u32,1>;
        @group(0) @binding(0) var<storage,read_write> output:array<u32>;
        @compute @workgroup_size(4) fn main(input:Input){
            if input.index==0u{memory[0]=memory[0]+17u;}workgroupBarrier();
            output[input.index]=memory[0]+input.id.x;
        }
        """;
    internal const string Multiple = """
        @group(0) @binding(0) var<storage,read_write> output:array<u32>;
        @compute @workgroup_size(1) fn first(@builtin(global_invocation_id) gid:vec3u){output[0]=gid.x+7u;}
        @compute @workgroup_size(1) fn second(@builtin(global_invocation_id) gid:vec3u){output[0]=gid.x+11u;}
        """;
    internal const string SampleMask = """
        struct Input{@location(0) color:vec4f,@builtin(sample_mask) mask:u32,}
        struct Output{@location(0) color:vec4f,@builtin(sample_mask) mask:u32,@builtin(frag_depth) depth:f32,}
        @fragment fn main(input:Input)->Output{return Output(input.color,input.mask,0.25);}
        """;

    [Theory] [InlineData(true)] [InlineData(false)]
    public void InitializationAndInputAssemblyArePreparedInEvaluationOrder(bool initialize)
    {
        var input = WgslReader.Parse(InputStruct); string before = WgslWriter.Write(input);
        var prepared = ShaderTargetLowering.ForSpirv(input, null, true, true, true, initialize);
        var wrapper = prepared.PhysicalLayout.EntryWrappers["main"];
        Assert.Equal(before, WgslWriter.Write(input));
        Assert.Single(wrapper.Interfaces, f => f.Binding.Builtin == "local_invocation_index");
        var operations = wrapper.Function.Graph.Blocks.Single().Instructions.Select(i => i.Operation).ToArray();
        var calls = operations.OfType<ValueOperation.Call>().Select(c => c.Function).ToArray();
        Assert.Equal(initialize ? new[] { prepared.WorkgroupInitializers["main"].Name, "main" } : new[] { "main" }, calls);
        if (initialize) {
            int init = Array.FindIndex(operations, o => o is ValueOperation.Call c && c.Function == calls[0]);
            int globalId = Array.FindIndex(operations, o => o is ValueOperation.InterfaceLoad { Field.Binding.Builtin: "global_invocation_id" });
            Assert.True(init < globalId);
        }
        Assert.Contains(operations, o => o is ValueOperation.Construct);
        var bytes = SpirvWriter.Emit(prepared).ToBytes();
        ModuleValidator.ValidateNative(SpirvReader.Parse(bytes));
    }

    [Fact]
    public void SampleMaskPhysicalArraysAndDepthConversionAreExplicitIoEffects()
    {
        var prepared = ShaderTargetLowering.ForSpirv(WgslReader.Parse(SampleMask), null, true, true, true);
        var wrapper = prepared.PhysicalLayout.EntryWrappers["main"];
        Assert.True(wrapper.WritesDepth);
        var masks = wrapper.Interfaces.Where(f => f.Binding.Builtin == "sample_mask").ToArray();
        Assert.Equal(2, masks.Length);
        Assert.All(masks, f => Assert.Equal(new ShaderType.Array(ShaderType.U32, 1), f.Type));
        var instructions = wrapper.Function.Graph.Blocks.Single().Instructions;
        Assert.All(instructions.Where(i => i.Operation is ValueOperation.InterfaceLoad), i => Assert.True((i.Effects & ShaderEffects.ReadMemory) != 0));
        Assert.All(instructions.Where(i => i.Operation is ValueOperation.InterfaceStore), i => Assert.True((i.Effects & ShaderEffects.WriteMemory) != 0));
        Assert.Contains(instructions, i => i.Operation is ValueOperation.Call c && c.Function == prepared.OutputFunctions[("main", "depth")].Name);
        var binary = SpirvWriter.Emit(prepared);
        ModuleValidator.ValidateNative(SpirvReader.Parse(binary.ToBytes()));
        var changed = prepared with { OutputFunctions = new Dictionary<(string, string?), ShaderFunction>(), WorkgroupInitializers = new Dictionary<string, ShaderFunction>() };
        Assert.Equal(binary.ToBytes(), SpirvWriter.Emit(changed).ToBytes());
    }

    [Fact]
    public void IdenticalInterfacesInDifferentEntriesHaveDistinctVariableIdentities()
    {
        var prepared = ShaderTargetLowering.ForSpirv(WgslReader.Parse(Multiple), null, true, true, true);
        var binary = SpirvWriter.Emit(prepared);
        var entries = binary.Instructions.Where(i => i.Opcode == (ushort)Op.EntryPoint).ToArray();
        Assert.Equal(2, entries.Length);
        uint[] Interfaces(SpirvInstruction entry) { SpirvBinary.ReadString(entry.Operands.AsSpan(2), out int words); return entry.Operands[(2 + words)..]; }
        Assert.Empty(Interfaces(entries[0]).Intersect(Interfaces(entries[1])));
        Assert.Equal(2, binary.Instructions.Count(i => i.Opcode == (ushort)Op.Variable && i.Operands[2] == 1));
        ModuleValidator.ValidateNative(SpirvReader.Parse(binary.ToBytes()));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void MeshAndTaskPublicationCallsAndTerminalStateBelongToTheWrapperGraph(bool task)
    {
        var prepared = ShaderTargetLowering.ForSpirv(SpirvMeshPublicationTests.PublicationFixture("Triangles"), null, true, true, true);
        var name = task ? "task_main" : "mesh_main";
        var wrapper = prepared.PhysicalLayout.EntryWrappers[name];
        var graph = wrapper.Function.Graph;
        var calls = graph.Blocks.Single().Instructions.Select(i => i.Operation).OfType<ValueOperation.Call>().Select(c => c.Function).ToArray();
        Assert.Equal(prepared.MeshPublications[name].Function.Name, calls[^1]);
        Assert.True(Array.IndexOf(calls, name) < calls.Length - 1);
        if (task) Assert.IsType<ControlFlowTerminator.Unreachable>(graph.Blocks.Single().Terminator);
        else Assert.IsType<ControlFlowTerminator.Return>(graph.Blocks.Single().Terminator);
    }

    [Fact]
    public void EntryWrapperNamesAvoidExistingShaderFunctionNames()
    {
        var module = WgslReader.Parse(InputStruct); module.Functions.Add(new("sia_spv_entry_main"));
        var prepared = ShaderTargetLowering.ForSpirv(module, null, true, true, true);
        Assert.NotEqual("sia_spv_entry_main", prepared.PhysicalLayout.EntryWrappers["main"].Function.Graph.Signature.Name);
        ModuleValidator.ValidateNative(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void VerifierRejectsWrongIoDirectionAndResultType(bool store)
    {
        var module = new Module(); var signature = new ShaderFunction("wrapper") { Stage = ShaderStage.Fragment };
        module.Functions.Add(signature);
        var graph = new ControlFlowFunction(signature); var block = graph.Block(); graph.Entry = block.Id;
        var value = graph.Value(ShaderType.U32);
        block.Instructions.Add(new(value, new ValueOperation.Literal(1u)));
        if (store) block.Instructions.Add(new(null, new ValueOperation.InterfaceStore(new("input", ShaderType.U32, new(Location: 0), true), value)));
        else block.Instructions.Add(new(graph.Value(ShaderType.U32), new ValueOperation.InterfaceLoad(new("input", ShaderType.F32, new(Location: 0), true))));
        block.Terminator = new ControlFlowTerminator.Return();
        var error = Assert.Throws<ShaderException>(() => ControlFlowVerifier.Validate(graph, module));
        Assert.Contains(store ? "illegal InterfaceStore" : "illegal InterfaceLoad", error.Message);
    }
}
