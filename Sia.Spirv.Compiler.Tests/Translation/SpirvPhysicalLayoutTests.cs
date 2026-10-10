using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class SpirvPhysicalLayoutTests
{
    internal const string SharedSource = """
        struct Data{index:u32,matrix:mat2x2f,tail:f32,}
        @group(0) @binding(0) var<uniform> input:Data;
        @group(0) @binding(1) var<storage,read_write> output:Data;
        var<workgroup> group_data:Data;
        @compute @workgroup_size(1) fn main(){group_data=input;workgroupBarrier();output=group_data;}
        """;

    [Fact]
    public void PreparationSeparatesUniformStorageAndWorkgroupIdentitiesWithExactByteOffsets()
    {
        var module = WgslReader.Parse(SharedSource);
        var logical = Assert.Single(module.Structures);
        var prepared = ShaderTargetLowering.ForSpirv(module, null, true, true, true);
        var layout = prepared.PhysicalLayout;
        Assert.Same(prepared.Module, layout.Module);
        var uniform = Assert.IsType<ShaderType.Structure>(layout.Globals["input"].PhysicalType);
        Assert.Equal(4, uniform.Members.Count);
        Assert.Equal(new uint[] { 0, 8, 16, 24 }, layout.Buffers[uniform].Members.Select(m => m.Offset));
        Assert.Equal(new[] { 0, 1, 3 }, layout.UniformFields[logical]);
        Assert.All(layout.Buffers[uniform].Members, m => Assert.Null(m.MatrixStride));
        Assert.Same(logical, layout.Globals["output"].PhysicalType);
        Assert.Equal(new uint[] { 0, 8, 24 }, layout.Buffers[logical].Members.Select(m => m.Offset));
        Assert.Equal(8u, layout.Buffers[logical].Members[1].MatrixStride);
        var shared = Assert.IsType<ShaderType.Structure>(layout.Globals["group_data"].PhysicalType);
        Assert.Equal(3, shared.Members.Count); Assert.NotEqual(logical, shared);
        Assert.False(layout.Buffers.ContainsKey(shared)); Assert.False(layout.Globals["group_data"].BufferWrapper);
        foreach (string name in new[] { "input", "output" }) {
            var declaration = Assert.IsType<ShaderType.Structure>(layout.Globals[name].DeclarationType);
            Assert.True(layout.Globals[name].BufferWrapper);
            Assert.Same(layout.Globals[name].PhysicalType, Assert.Single(declaration.Members).Type);
            Assert.Equal(0u, Assert.Single(layout.Buffers[declaration].Members).Offset);
        }
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SelectedUniformMemoryRequirementsSurvivePreparedFieldMapping(bool dynamic)
    {
        var module = UniformMemoryTests.Fixture(1, dynamic);
        var prepared = ShaderTargetLowering.ForSpirv(module, null, true, true, true);
        var physical = Assert.IsType<ShaderType.Structure>(prepared.PhysicalLayout.Globals["data"].PhysicalType);
        Assert.Equal(new uint[] { 0, 8, 16, 24 }, prepared.PhysicalLayout.Buffers[physical].Members.Select(m => m.Offset));
        var binary = SpirvWriter.Emit(prepared);
        var reads = binary.Instructions.Where(i => (Op)i.Opcode == Op.Load && i.Operands.Length > 3 && i.Operands[3] == 1).ToArray();
        Assert.Equal(dynamic ? 2 : 1, reads.Length);
        Assert.Equal(dynamic, binary.Instructions.Any(i => (Op)i.Opcode == Op.Switch));
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SharedArrayIdentityDoesNotAcquireBufferStrideDecorations(bool explicitStride)
    {
        var element = new ShaderType.Structure("Element", [new("value", ShaderType.U32)]);
        var array = new ShaderType.Array(element, 3, explicitStride ? 16u : null);
        var module = new Module(); module.Structures.Add(element);
        module.Globals.Add(new("buffer", array, AddressSpace.Storage, Binding: new(0, 0)));
        module.Globals.Add(new("group_data", array, AddressSpace.Workgroup));
        module.Functions.Add(new("main") { Stage = ShaderStage.Compute });
        var prepared = ShaderTargetLowering.ForSpirv(module, null, true, true, true);
        var layout = prepared.PhysicalLayout;
        var shared = Assert.IsType<ShaderType.Array>(layout.Globals["group_data"].PhysicalType);
        Assert.NotEqual(array, shared); Assert.False(layout.Buffers.ContainsKey(shared));
        Assert.False(layout.Buffers.ContainsKey(shared.Element));
        Assert.Equal(explicitStride ? 16u : 4u, layout.Buffers[array].ArrayStride);
        var binary = SpirvWriter.Emit(prepared);
        var pointers = binary.Instructions.Where(i => (Op)i.Opcode == Op.TypePointer && i.Operands[1] == 4).ToArray();
        uint type = Assert.Single(pointers, p => binary.Instructions.Any(i => (Op)i.Opcode == Op.TypeArray && i.Operands[0] == p.Operands[2])).Operands[2];
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.Decorate && i.Operands[0] == type && i.Operands[1] == 6);
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }

    [Fact]
    public void RuntimeBufferStructuresAreAlreadyBlocksAndKeepExplicitStrideAndOffsets()
    {
        var module = WgslReader.Parse("struct Data{header:vec4u,values:array<vec2u>,}@group(0) @binding(0) var<storage,read_write> data:Data;@compute @workgroup_size(1) fn main(){}");
        var logical = Assert.Single(module.Structures);
        var layout = SpirvPhysicalLayoutLowering.Prepare(module);
        Assert.False(layout.Globals["data"].BufferWrapper);
        Assert.Same(logical, layout.Globals["data"].DeclarationType);
        Assert.Equal(new uint[] { 0, 16 }, layout.Buffers[logical].Members.Select(m => m.Offset));
        Assert.Equal(8u, layout.Buffers[logical.Members[1].Type].ArrayStride);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Emit(layout).ToBytes()));
    }

    [Fact]
    public void LayoutPreparationPreservesBorrowedInputAndBothWriterOrders()
    {
        var input = WgslReader.Parse(SharedSource); string before = WgslWriter.Write(input);
        var globals = input.Globals.ToArray(); var members = input.Structures[0].Members.ToArray();
        var prepared = ShaderTargetLowering.ForSpirv(input, null, true, true, true);
        byte[] first = SpirvWriter.Emit(prepared).ToBytes();
        Assert.Equal(before, WgslWriter.Write(input));
        Assert.Equal(first, SpirvWriter.Write(input));
        Assert.Equal(globals, input.Globals); Assert.Equal(members, input.Structures[0].Members);
        Assert.Equal(SpirvBinary.Parse(first).ToWords(), SpirvWriter.WriteWords(input));
    }
}
