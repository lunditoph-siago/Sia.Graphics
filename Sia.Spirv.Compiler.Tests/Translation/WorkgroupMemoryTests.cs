using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class WorkgroupMemoryTests
{
    internal static Module Fixture(string kind, MemoryDecorations decoration)
    {
        if (kind is "rawmember" or "rawarray") return ImplicitFixture(kind == "rawarray", decoration);
        if (kind == "multi")
        {
            var shared = WgslReader.Parse(MultiSource); Decorate(shared, "Data", 0, decoration); shared.VulkanMemoryModel = true; return shared;
        }
        string data = kind == "atomic" ? "struct Data{x:atomic<u32>,y:u32,}" : "struct Data{x:u32,y:u32,}";
        string prefix = kind == "specialized" ? "override count:u32=2u;" : "";
        string type = kind is "nested" or "specialized" ? "array<Data," + (kind == "specialized" ? "count" : "2") + ">" : "Data";
        string selected = kind is "nested" or "specialized" ? "group_data[input[2]]" : "group_data";
        string body = kind == "atomic"
            ? "output[0]=atomicLoad(&group_data.x);atomicStore(&group_data.x,input[0]);output[1]=atomicAdd(&group_data.x,2u);output[2]=atomicLoad(&group_data.x);group_data.y=input[1];output[3]=group_data.y;"
            : "output[0]=" + selected + ".x;" + selected + ".x=input[0];" + selected + ".y=input[1];output[1]=" + selected + ".x;output[2]=" + selected + ".y;output[3]=workgroupUniformLoad(&" + selected + ".x);";
        if (kind == "alias") body = "let p=&group_data.x;output[0]=*p;*p=input[0];group_data.y=input[1];output[1]=*p;output[2]=group_data.y;output[3]=*p;";
        var module = WgslReader.Parse(prefix + data + "var<workgroup> group_data:" + type + ";"
            + "@group(0) @binding(0) var<storage,read> input:array<u32>;@group(0) @binding(1) var<storage,read_write> output:array<u32>;"
            + "@compute @workgroup_size(1) fn main(){" + body + "}");
        Decorate(module, "Data", 0, decoration);
        module.VulkanMemoryModel = true; return module;
    }
    internal static void Decorate(Module module, string name, int index, MemoryDecorations decoration)
    {
        var members = (IList<StructMember>)module.Structures.Single(s => s.Name == name).Members;
        members[index] = members[index] with { MemoryDecorations = decoration };
    }
    internal static Module ImplicitFixture(bool array, MemoryDecorations decoration)
    {
        var module = Fixture(array ? "multi" : "scalar", decoration);
        var structure = (ShaderType.Structure)module.Globals[0].Type;
        Expression root = new Expression.Reference("group_data", structure);
        Expression source = new Expression.Member(root, "x", structure.Members[0].Type);
        if (array) source = new Expression.Access(source, Expression.U32(1), ShaderType.U32);
        Expression neighbor = new Expression.Member(root, "y", ShaderType.U32);
        Expression Input(uint index) => new Expression.Access(new Expression.Reference("input", module.Globals[1].Type), Expression.U32(index), ShaderType.U32);
        Expression Output(uint index) => new Expression.Access(new Expression.Reference("output", module.Globals[2].Type), Expression.U32(index), ShaderType.U32);
        var main = module.Functions[0]; main.Arguments.Clear(); main.WorkgroupSize = [Expression.U32(1), Expression.U32(1), Expression.U32(1)];
        main.Body.Statements.Clear();
        main.Body.Statements.Add(new Statement.Store(Output(0), source));
        main.Body.Statements.Add(new Statement.Store(source, Input(0)));
        main.Body.Statements.Add(new Statement.Store(neighbor, Input(1)));
        main.Body.Statements.Add(new Statement.Store(Output(1), source));
        main.Body.Statements.Add(new Statement.Store(Output(2), neighbor));
        main.Body.Statements.Add(new Statement.Store(Output(3), source));
        return module;
    }
    internal const string MultiSource = """
        struct Data{x:array<u32,64>,y:u32,} var<workgroup> group_data:Data;
        @group(0) @binding(0) var<storage,read> input:array<u32>;
        @group(0) @binding(1) var<storage,read_write> output:array<u32>;
        @compute @workgroup_size(64) fn main(@builtin(local_invocation_index) i:u32){
            output[64u+i]=group_data.x[i];group_data.x[i]=input[i];workgroupBarrier();
            output[i]=group_data.x[(i+1u)%64u];workgroupBarrier();
            group_data.x[i]=input[i]+100u;workgroupBarrier();output[128u+i]=group_data.x[(i+1u)%64u];
        }
        """;
    internal static Module TaskFixture(bool atomic, MemoryDecorations decoration)
    {
        var module = WgslReader.Parse("enable wgpu_mesh_shader;struct Data{x:" + (atomic ? "atomic<u32>" : "u32") + ",y:u32,}"
            + "var<task_payload> payload:Data;@task @payload(payload) @workgroup_size(1) fn main()->@builtin(mesh_task_size) vec3u{"
            + (atomic ? "atomicStore(&payload.x,1u);let old=atomicAdd(&payload.x,1u);payload.y=atomicLoad(&payload.x);" : "payload.x=1u;payload.y=payload.x;") + "return vec3u(1);}");
        Decorate(module, "Data", 0, decoration); module.VulkanMemoryModel = true; return module;
    }
    internal static Module MeshFixture(MemoryDecorations decoration)
    {
        var module = WgslReader.Parse("""
            enable wgpu_mesh_shader;
            struct Vertex{@builtin(position) position:vec4f,}
            struct Primitive{@builtin(point_index) index:u32,}
            struct Output{@builtin(vertices) vertices:array<Vertex,1>,@builtin(primitives) primitives:array<Primitive,1>,@builtin(vertex_count) vc:u32,@builtin(primitive_count) pc:u32,}
            var<workgroup> output:Output;
            @mesh(output) @workgroup_size(1) fn main(){output.vc=1u;output.pc=1u;output.vertices[0].position=vec4f(0,1,0,1);output.primitives[0].index=0u;}
            """);
        Decorate(module, "Vertex", 0, decoration); Decorate(module, "Output", 2, decoration);
        module.VulkanMemoryModel = true; return module;
    }

    internal static uint[] Pointers(SpirvBinary binary, uint storage)
    {
        var types = binary.Instructions.Where(i => (Op)i.Opcode == Op.TypePointer && i.Operands[1] == storage).Select(i => i.Operands[0]).ToHashSet();
        return binary.Instructions.Where(i => (Op)i.Opcode is Op.Variable or Op.AccessChain or Op.CopyObject or Op.FunctionParameter && types.Contains(i.Operands[0])).Select(i => i.Operands[1]).ToArray();
    }

    [Theory]
    [InlineData("scalar", MemoryDecorations.None)] [InlineData("scalar", MemoryDecorations.Volatile)]
    [InlineData("scalar", MemoryDecorations.Coherent)] [InlineData("scalar", MemoryDecorations.Volatile | MemoryDecorations.Coherent)]
    [InlineData("alias", MemoryDecorations.Volatile)] [InlineData("nested", MemoryDecorations.Volatile)]
    [InlineData("specialized", MemoryDecorations.Volatile)] [InlineData("atomic", MemoryDecorations.Volatile)]
    public void InitializationAndBodyKeepWorkgroupMemoryRequirements(string kind, MemoryDecorations decoration)
    {
        var module = Fixture(kind, decoration); ModuleValidator.Validate(module);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module)); var pointers = Pointers(binary, 4).ToHashSet();
        var constants = binary.Instructions.Where(i => (Op)i.Opcode == Op.Constant).ToDictionary(i => i.Operands[1], i => i.Operands[2]);
        var loads = binary.Instructions.Where(i => (Op)i.Opcode == Op.Load && pointers.Contains(i.Operands[2])).ToArray();
        var stores = binary.Instructions.Where(i => (Op)i.Opcode == Op.Store && pointers.Contains(i.Operands[0])).ToArray();
        Assert.NotEmpty(loads); Assert.NotEmpty(stores);
        Assert.All(loads, i => { Assert.True(i.Operands.Length > 4); Assert.Equal(48u, i.Operands[3] & ~1u); Assert.Equal(2u, constants[i.Operands[4]]); });
        Assert.All(stores, i => { Assert.True(i.Operands.Length > 3); Assert.Equal(40u, i.Operands[2] & ~1u); Assert.Equal(2u, constants[i.Operands[3]]); });
        if ((decoration & MemoryDecorations.Volatile) != 0 && kind != "atomic")
        {
            Assert.Contains(loads, i => i.Operands[3] == 49); Assert.Contains(loads, i => i.Operands[3] == 48);
            Assert.Contains(stores, i => i.Operands[2] == 41); Assert.Contains(stores, i => i.Operands[2] == 40);
        }
        if (kind == "atomic")
            Assert.All(binary.Instructions.Where(i => (Op)i.Opcode is Op.AtomicStore or Op.AtomicLoad or Op.AtomicIAdd),
                i => Assert.Equal(32768u, constants[i.Operands[(Op)i.Opcode == Op.AtomicStore ? 2 : 4]]));
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }

    [Fact]
    public void NativeRoundtripCannotInsertAnotherVolatileInitialization()
    {
        var module = Fixture("scalar", MemoryDecorations.Volatile); module.VulkanMemoryModel = false;
        var original = SpirvBinary.Parse(SpirvWriter.Write(module));
        var imported = PipelineConstantResolver.Resolve(SpirvReader.Parse(original.ToBytes()), new Dictionary<string, double>());
        imported.VulkanMemoryModel = true;
        var output = SpirvBinary.Parse(SpirvWriter.Write(imported));
        int Stores(SpirvBinary binary) { var pointers = Pointers(binary, 4).ToHashSet(); return binary.Instructions.Count(i => (Op)i.Opcode == Op.Store && pointers.Contains(i.Operands[0])); }
        Assert.Equal(Stores(original), Stores(output));
    }

    [Fact]
    public void SpecializedInitializationUsesThePipelineLengthWithoutAnArrayZeroConstructor()
    {
        var binary = SpirvBinary.Parse(SpirvWriter.Write(Fixture("specialized", MemoryDecorations.None)));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.LoopMerge);
        var parsed = WgslReader.Parse(WgslWriter.Write(SpirvReader.Parse(binary.ToBytes())));
        ModuleValidator.Validate(parsed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ImplicitPlaceReadsAlsoRetainSelectedWorkgroupRequirements(bool array)
    {
        var module = ImplicitFixture(array, MemoryDecorations.Volatile);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
        var pointers = Pointers(binary, 4).ToHashSet();
        var loads = binary.Instructions.Where(i => (Op)i.Opcode == Op.Load && pointers.Contains(i.Operands[2]) && i.Operands.Length > 3 && i.Operands[3] == 49).ToArray();
        Assert.Equal(3, loads.Length);
    }

    [Theory]
    [InlineData(1u)] [InlineData(5u)]
    public void ImplicitWorkgroupCoherenceDoesNotNarrowAnExplicitWiderScope(uint scope)
    {
        var module = new Module { VulkanMemoryModel = true, WorkgroupInitializationRequired = false };
        module.Globals.Add(new("data", ShaderType.U32, AddressSpace.Workgroup));
        module.Globals.Add(new("output", ShaderType.U32, AddressSpace.Storage, Binding: new(0, 0)));
        var shared = new Expression.Reference("data", new ShaderType.Pointer(ShaderType.U32, AddressSpace.Workgroup));
        var main = new ShaderFunction("main") { Stage = ShaderStage.Compute };
        main.Body.Statements.Add(new Statement.Store(shared, Expression.U32(7)) { MemoryAccess = new(40, AvailableScope: scope) });
        main.Body.Statements.Add(new Statement.Store(new Expression.Reference("output", new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage)),
            new Expression.Load(shared) { MemoryAccess = new(48, VisibleScope: scope) }));
        module.Functions.Add(main);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
        var constants = binary.Instructions.Where(i => (Op)i.Opcode == Op.Constant).ToDictionary(i => i.Operands[1], i => i.Operands[2]);
        Assert.Equal(scope, constants[Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Load).Operands[4]]);
        Assert.Equal(scope, constants[Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Store && i.Operands.Length > 3).Operands[3]]);
        Assert.Equal(scope == 1, binary.Instructions.Any(i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 5346));
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TaskPayloadVolatileMembersUseLegalOrdinaryOrAtomicFlags(bool atomic)
    {
        var binary = SpirvBinary.Parse(SpirvWriter.Write(TaskFixture(atomic, MemoryDecorations.Volatile)));
        if (atomic)
        {
            var constants = binary.Instructions.Where(i => (Op)i.Opcode == Op.Constant).ToDictionary(i => i.Operands[1], i => i.Operands[2]);
            Assert.Equal(3, binary.Instructions.Count(i => (Op)i.Opcode is Op.AtomicStore or Op.AtomicLoad or Op.AtomicIAdd));
            Assert.All(binary.Instructions.Where(i => (Op)i.Opcode is Op.AtomicStore or Op.AtomicLoad or Op.AtomicIAdd),
                i => Assert.Equal(32768u, constants[i.Operands[(Op)i.Opcode == Op.AtomicStore ? 2 : 4]]));
        }
        else
        {
            Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Load && i.Operands.Length == 4 && i.Operands[3] == 1);
            Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Store && i.Operands.Length == 3 && i.Operands[2] == 1);
        }
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }

    [Fact]
    public void TaskPayloadCoherenceCannotDisappearUnderTheVulkanModel()
    {
        Assert.Contains("non-private lowering", Assert.Throws<ShaderException>(() => SpirvWriter.Write(TaskFixture(false, MemoryDecorations.Coherent))).Message);
    }

    [Theory]
    [InlineData(MemoryDecorations.None)] [InlineData(MemoryDecorations.Volatile)] [InlineData(MemoryDecorations.Coherent)]
    public void MeshPublishingReadsKeepSelectedWorkgroupRequirements(MemoryDecorations decoration)
    {
        var binary = SpirvBinary.Parse(SpirvWriter.Write(MeshFixture(decoration))); var pointers = Pointers(binary, 4).ToHashSet();
        var loads = binary.Instructions.Where(i => (Op)i.Opcode == Op.Load && pointers.Contains(i.Operands[2])).ToArray();
        Assert.Equal(4, loads.Length);
        Assert.All(loads, i => Assert.Equal(48u, i.Operands[3] & ~1u));
        Assert.Equal(decoration == MemoryDecorations.Volatile ? 2 : 0, loads.Count(i => i.Operands[3] == 49));
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }
}
