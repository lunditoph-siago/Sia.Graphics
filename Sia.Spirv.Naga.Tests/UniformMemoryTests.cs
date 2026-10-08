using Sia.Spirv.Naga.Back;
using Sia.Spirv.Naga.Front;
using Sia.Spirv.Naga.IR;
using Sia.Spirv.Naga.Spirv;
using Sia.Spirv.Naga.Valid;

namespace Sia.Spirv.Naga.Tests;

public class UniformMemoryTests
{
    internal static Module Fixture(uint flags, bool dynamic = false, bool vulkan = false)
    {
        var matrix = new ShaderType.Matrix(2, 2, ShaderType.F32);
        var data = new ShaderType.Structure("Data", [new("index", ShaderType.U32), new("matrix", matrix), new("tail", ShaderType.F32)]);
        var module = new Module { VulkanMemoryModel = vulkan }; module.Structures.Add(data);
        module.Globals.Add(new("data", data, AddressSpace.Uniform, StorageAccess.Read, new(0, 0)));
        module.Globals.Add(new("output", new ShaderType.Array(ShaderType.F32, null), AddressSpace.Storage, Binding: new(0, 1)));
        var root = new Expression.Reference("data", new ShaderType.Pointer(data, AddressSpace.Uniform, StorageAccess.Read));
        Expression index = dynamic ? new Expression.Load(new Expression.Member(root, "index", new ShaderType.Pointer(ShaderType.U32, AddressSpace.Uniform, StorageAccess.Read))) : Expression.U32(1);
        var member = new Expression.Member(root, "matrix", new ShaderType.Pointer(matrix, AddressSpace.Uniform, StorageAccess.Read));
        var column = new Expression.Access(member, index, new ShaderType.Pointer(new ShaderType.Vector(2, ShaderType.F32), AddressSpace.Uniform, StorageAccess.Read));
        var component = new Expression.Access(column, Expression.U32(1), new ShaderType.Pointer(ShaderType.F32, AddressSpace.Uniform, StorageAccess.Read));
        var target = new Expression.Access(new Expression.Reference("output", new ShaderType.Pointer(module.Globals[1].Type, AddressSpace.Storage)), Expression.U32(0), new ShaderType.Pointer(ShaderType.F32, AddressSpace.Storage));
        var main = new ShaderFunction("main") { Stage = ShaderStage.Compute };
        main.Body.Statements.Add(new Statement.Store(target, new Expression.Load(component) { MemoryAccess = new(flags, (flags & 2) != 0 ? 4u : null, VisibleScope: (flags & 16) != 0 ? 5u : null) }));
        module.Functions.Add(main); return module;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SelectedVolatileUniformComponentReadsOnlyTheSelectedLocation(bool dynamic)
    {
        var module = Fixture(1, dynamic); ModuleValidator.Validate(module);
        var output = SpirvBinary.Parse(SpirvWriter.Write(module));
        var types = output.Instructions.Where(i => (Op)i.Opcode == Op.TypeFloat).Select(i => i.Operands[0]).ToHashSet();
        var reads = output.Instructions.Where(i => (Op)i.Opcode == Op.Load && i.Operands.Length > 3 && i.Operands[3] == 1).ToArray();
        Assert.Equal(dynamic ? 2 : 1, reads.Length);
        Assert.All(reads, i => Assert.Contains(i.Operands[0], types));
        Assert.Equal(dynamic, output.Instructions.Any(i => (Op)i.Opcode == Op.Switch));
        ModuleValidator.Validate(SpirvReader.Parse(output.ToBytes()));
    }

    [Theory]
    [InlineData(0u, false)] [InlineData(2u, false)] [InlineData(4u, false)] [InlineData(7u, false)]
    [InlineData(32u, true)] [InlineData(48u, true)] [InlineData(55u, true)]
    public void DirectUniformLeafRetainsItsNativeMemoryOperands(uint flags, bool vulkan)
    {
        var binary = SpirvBinary.Parse(SpirvWriter.Write(Fixture(flags, vulkan: vulkan)));
        var read = Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Load);
        Assert.Equal(flags, read.Operands[3]);
        int index = 4;
        if ((flags & 2) != 0) Assert.Equal(4u, read.Operands[index++]);
        if ((flags & 16) != 0)
            Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Constant && i.Operands[1] == read.Operands[index] && i.Operands[2] == 5);
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }

    internal const string AliasSource = """
        struct Data { index:u32, matrix:mat2x2f, tail:f32, }
        @group(0) @binding(0) var<uniform> data:Data;
        @group(0) @binding(1) var<storage,read_write> output:array<f32>;
        @compute @workgroup_size(1) fn main(){
            var cursor=data.index;
            let column=&data.matrix[cursor];
            cursor=1u;
            let same=column;
            output[0]=(*same)[1];output[1]=data.tail;
        }
        """;

    [Fact]
    public void UniformPointerAliasesCaptureTheirDynamicIndexAtCreation()
    {
        var module = WgslReader.Parse(AliasSource); ModuleValidator.Validate(module);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
        Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Switch);
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands[0] is 4441 or 4442);
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(SpirvReader.Parse(binary.ToBytes()))));
    }

    internal static SpirvBinary UnsupportedStrideFixture()
    {
        const string source = "struct Data{matrix:mat2x2f,tail:f32,}@group(0) @binding(0) var<storage,read> data:Data;@group(0) @binding(1) var<storage,read_write> output:array<f32>;@compute @workgroup_size(1) fn main(){output[0]=data.matrix[1][1];}";
        var input = SpirvBinary.Parse(SpirvWriter.Write(WgslReader.Parse(source)));
        uint data = input.Instructions.Single(i => (Op)i.Opcode == Op.Name && SpirvBinary.ReadString(i.Operands.AsSpan(1), out _) == "Data").Operands[0];
        var code = input.Instructions.Select(i => (Op)i.Opcode == Op.MemberDecorate && i.Operands[0] == data && i.Operands[2] == 7
            ? new SpirvInstruction(i.Opcode, [i.Operands[0], i.Operands[1], 7, 16])
            : (Op)i.Opcode == Op.MemberDecorate && i.Operands[0] == data && i.Operands[1] == 1 && i.Operands[2] == 35
                ? new SpirvInstruction(i.Opcode, [data, 1, 35, 32]) : i).ToArray();
        return new() { Version = input.Version, Bound = input.Bound, Instructions = code };
    }

    [Fact]
    public void ExplicitNativeStridePreservesTheBufferAbiThroughBothWriters()
    {
        var module = SpirvReader.Parse(UnsupportedStrideFixture().ToBytes());
        ModuleValidator.Validate(module);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Decorate && i.Operands is [_, 6, 16]);
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    internal static Module AggregateFixture(string kind, uint flags = 3, bool vulkan = false, MemoryDecorations memberMemory = MemoryDecorations.None)
    {
        var module = Fixture(0, vulkan: vulkan);
        var data = (ShaderType.Structure)module.Globals[0].Type;
        data = data with { Members = data.Members.Select(m => m.Name == "matrix" ? m with { MemoryDecorations = memberMemory } : m).ToArray() };
        module.Structures[0] = data;
        ShaderType type = kind == "array" ? new ShaderType.Array(data.Members[1].Type, 2) : data;
        module.Globals[0] = module.Globals[0] with { Type = type };
        var root = new Expression.Reference("data", new ShaderType.Pointer(type, AddressSpace.Uniform, StorageAccess.Read));
        Expression pointer = kind == "matrix" ? new Expression.Member(root, "matrix", new ShaderType.Pointer(data.Members[1].Type, AddressSpace.Uniform, StorageAccess.Read)) : root;
        string name = "snapshot";
        Expression read = new Expression.Load(pointer) { MemoryAccess = flags == 0 ? null : new(flags, (flags & 2) != 0 ? 16u : null) };
        Expression value = new Expression.Reference(name, read.Type);
        if (kind == "root") value = new Expression.Member(value, "matrix", data.Members[1].Type);
        if (kind == "array") value = new Expression.Access(value, Expression.U32(1), data.Members[1].Type);
        value = new Expression.Access(new Expression.Access(value, Expression.U32(1), new ShaderType.Vector(2, ShaderType.F32)), Expression.U32(1), ShaderType.F32);
        var body = module.Functions[0].Body; var target = ((Statement.Store)body.Statements[0]).Target;
        body.Statements.Clear(); body.Statements.Add(new Statement.Declare(name, read.Type, read, false)); body.Statements.Add(new Statement.Store(target, value));
        return module;
    }

    [Theory]
    [InlineData("root", 4)] [InlineData("matrix", 2)] [InlineData("array", 4)]
    public void WholeUniformReadsSplitIntoDisjointQualifiedLeaves(string kind, int count)
    {
        var binary = SpirvBinary.Parse(SpirvWriter.Write(AggregateFixture(kind)));
        var reads = binary.Instructions.Where(i => (Op)i.Opcode == Op.Load).ToArray();
        Assert.Equal(count, reads.Length); Assert.All(reads, i => Assert.Equal(new uint[] { 1 }, i.Operands[3..]));
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }

    [Theory]
    [InlineData(MemoryDecorations.Volatile, 1u)] [InlineData(MemoryDecorations.Coherent, 48u)]
    public void VulkanWholeRootReadQualifiesOnlyItsDecoratedMatrixColumns(MemoryDecorations decoration, uint mask)
    {
        var binary = SpirvBinary.Parse(SpirvWriter.Write(AggregateFixture("root", 0, true, decoration)));
        Assert.Equal(4, binary.Instructions.Count(i => (Op)i.Opcode == Op.Load));
        Assert.Equal(2, binary.Instructions.Count(i => (Op)i.Opcode == Op.Load && i.Operands.Length > 3 && i.Operands[3] == mask));
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }

    internal static Module ShapeFixture(int columns, int rows, bool half, bool dynamic = true)
    {
        var module = Fixture(1, vulkan: false); var scalar = half ? ShaderType.F16 : ShaderType.F32;
        if (half) module.Enables.Add("f16");
        var matrix = new ShaderType.Matrix(columns, rows, scalar);
        var data = (ShaderType.Structure)module.Globals[0].Type;
        data = data with { Members = data.Members.Select(m => m.Name == "matrix" ? m with { Type = matrix } : m).ToArray() };
        module.Structures[0] = data; module.Globals[0] = module.Globals[0] with { Type = data };
        var root = new Expression.Reference("data", new ShaderType.Pointer(data, AddressSpace.Uniform, StorageAccess.Read));
        Expression index = dynamic ? new Expression.Load(new Expression.Member(root, "index", new ShaderType.Pointer(ShaderType.U32, AddressSpace.Uniform, StorageAccess.Read))) : Expression.U32((uint)columns - 1);
        Expression pointer = new Expression.Access(new Expression.Access(new Expression.Member(root, "matrix", new ShaderType.Pointer(matrix, AddressSpace.Uniform, StorageAccess.Read)), index,
            new ShaderType.Pointer(new ShaderType.Vector(rows, scalar), AddressSpace.Uniform, StorageAccess.Read)), Expression.U32((uint)rows - 1), new ShaderType.Pointer(scalar, AddressSpace.Uniform, StorageAccess.Read));
        Expression value = new Expression.Load(pointer) { MemoryAccess = new(1) };
        if (half) value = new Expression.Convert(ShaderType.F32, value);
        var store = (Statement.Store)module.Functions[0].Body.Statements[0]; module.Functions[0].Body.Statements[0] = store with { Value = value };
        return module;
    }

    public static IEnumerable<object[]> Shapes() => from columns in Enumerable.Range(2, 3) from rows in Enumerable.Range(2, 3) from half in new[] { false, true } select new object[] { columns, rows, half };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void UniformDirectReadsCoverEveryConcreteMatrixShape(int columns, int rows, bool half)
    {
        var binary = SpirvBinary.Parse(SpirvWriter.Write(ShapeFixture(columns, rows, half)));
        var floats = binary.Instructions.Where(i => (Op)i.Opcode == Op.TypeFloat && i.Operands[1] == (half ? 16 : 32)).Select(i => i.Operands[0]).ToHashSet();
        var reads = binary.Instructions.Where(i => (Op)i.Opcode == Op.Load && i.Operands.Length > 3 && i.Operands[3] == 1).ToArray();
        Assert.Equal(half || rows == 2 ? columns : 1, reads.Length); Assert.All(reads, i => Assert.Contains(i.Operands[0], floats));
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }

    internal const string NestedSource = """
        struct Inner { matrix:mat3x2f, tail:f32, }
        struct Outer { indices:vec4u, values:array<Inner,2>, }
        @group(0) @binding(0) var<uniform> data:Outer;
        @group(0) @binding(1) var<storage,read_write> output:array<f32>;
        var<private> calls:u32;
        fn column()->u32 {calls++;return data.indices.y;}
        @compute @workgroup_size(1) fn main(){
            output[0]=data.values[data.indices.x].matrix[column()][data.indices.z];
            output[1]=data.values[data.indices.x].tail;output[2]=f32(calls);
        }
        """;

    [Fact]
    public void NestedUniformArraysKeepOffsetsAndEvaluateIndexCallsOnce()
    {
        var binary = SpirvBinary.Parse(SpirvWriter.Write(WgslReader.Parse(NestedSource)));
        uint column = binary.Instructions.Single(i => (Op)i.Opcode == Op.Name && SpirvBinary.ReadString(i.Operands.AsSpan(1), out _) == "column").Operands[0];
        Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.FunctionCall && i.Operands[2] == column);
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UniformBufferBindingArraysUseTheirMappedDescriptorElement(bool dynamic)
    {
        string source = "enable wgpu_binding_array; struct Data{matrix:mat2x2f,tail:f32,}@group(0) @binding(0) var<uniform> data:binding_array<Data,2>;"
            + "@group(0) @binding(1) var<storage,read_write> output:array<f32>;@compute @workgroup_size(1) fn main(){"
            + (dynamic ? "let index=u32(output[0]);" : "const index=1u;") + "output[0]=data[index].matrix[1][1];}";
        var binary = SpirvBinary.Parse(SpirvWriter.Write(WgslReader.Parse(source)));
        Assert.Equal(dynamic, binary.Instructions.Any(i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 5306));
        uint element = binary.Instructions.Single(i => (Op)i.Opcode == Op.Name
            && SpirvBinary.ReadString(i.Operands.AsSpan(1), out _) == "SpirvUniform_Data").Operands[0];
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Decorate && i.Operands.SequenceEqual(new uint[] { element, 2 }));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.TypeArray && i.Operands[1] == element);
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }
}
