using Sia.Spirv.Naga.Back;
using Sia.Spirv.Naga.Front;
using Sia.Spirv.Naga.IR;
using Sia.Spirv.Naga.Proc;
using Sia.Spirv.Naga.Spirv;
using Sia.Spirv.Naga.Valid;
using static Sia.Spirv.Naga.Tests.SpirvTests;

namespace Sia.Spirv.Naga.Tests;

public class MemberMemoryTests
{
    internal static SpirvBinary Fixture(uint decoration)
    {
        var input = MemoryAccessTests.Fixture(0, 0);
        var code = input.Instructions.ToList();
        code.Insert(code.FindIndex(i => (Op)i.Opcode == Op.TypeVoid), I(Op.MemberDecorate, 6, 0, decoration));
        return new() { Version = input.Version, Bound = input.Bound, Instructions = code };
    }

    [Theory]
    [InlineData(21u)] [InlineData(23u)]
    public void MemberMemoryDecorationsSurviveNativeRoundtrip(uint decoration)
    {
        var module = SpirvReader.Parse(Fixture(decoration).ToBytes()); ModuleValidator.Validate(module);
        module = PipelineConstantResolver.Resolve(module, new Dictionary<string, double>());
        var output = SpirvBinary.Parse(SpirvWriter.Write(module));
        Assert.Contains(output.Instructions, i => (Op)i.Opcode == Op.MemberDecorate && i.Operands[2] == decoration);
        ModuleValidator.Validate(SpirvReader.Parse(output.ToBytes()));
    }

    internal static SpirvBinary DecoratedSource(string source, string structure, uint member, uint decoration)
    {
        var input = SpirvBinary.Parse(SpirvWriter.Write(WgslReader.Parse(source)));
        uint type = input.Instructions.Single(i => (Op)i.Opcode == Op.Name && SpirvBinary.ReadString(i.Operands.AsSpan(1), out _) == structure).Operands[0];
        var code = input.Instructions.ToList();
        code.Insert(code.FindIndex(i => i.Opcode is >= 19 and <= 39), I(Op.MemberDecorate, type, member, decoration));
        return new() { Version = input.Version, Bound = input.Bound, Instructions = code };
    }

    internal const string ScalarSource = """
        struct Data { a:u32, b:u32, }
        @group(0) @binding(0) var<storage,read_write> data:Data;
        @group(0) @binding(1) var<storage,read_write> output:array<u32>;
        @compute @workgroup_size(1) fn main(){
            output[0]=data.a; data.a=data.a+1u;
            output[1]=data.b; data.b=data.b+2u;
        }
        """;

    [Theory]
    [InlineData(21u, false)] [InlineData(23u, false)] [InlineData(21u, true)] [InlineData(23u, true)]
    public void SelectedMembersLowerWithoutQualifyingTheirNeighbors(uint decoration, bool vulkan)
    {
        var module = SpirvReader.Parse(DecoratedSource(ScalarSource, "Data", 0, decoration).ToBytes());
        module.VulkanMemoryModel = vulkan;
        var data = Assert.IsType<ShaderType.Structure>(module.Globals[0].Type);
        while (data.Members is [{ Type: ShaderType.Structure nested }]) data = nested;
        Assert.Equal(decoration == 21 ? MemoryDecorations.Volatile : MemoryDecorations.Coherent, data.Members[0].MemoryDecorations);
        Assert.Equal(MemoryDecorations.None, data.Members[1].MemoryDecorations);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
        if (vulkan)
        {
            Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode is Op.Decorate && i.Operands[1] is 21 or 23
                || (Op)i.Opcode is Op.MemberDecorate && i.Operands[2] is 21 or 23);
            Assert.Equal(2, binary.Instructions.Count(i => (Op)i.Opcode == Op.Load && i.Operands.Length > 3 && i.Operands[3] == (decoration == 21 ? 1 : 48)));
            Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Store && i.Operands.Length > 2 && i.Operands[2] == (decoration == 21 ? 1 : 40));
            var constants = binary.Instructions.Where(i => (Op)i.Opcode == Op.Constant).ToDictionary(i => i.Operands[1], i => i.Operands[2]);
            if (decoration == 23)
                Assert.All(binary.Instructions.Where(i => (Op)i.Opcode == Op.Load && i.Operands.Length > 4), i => Assert.Equal(5u, constants[i.Operands[4]]));
        }
        else Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.MemberDecorate && i.Operands[2] == decoration);
        string wgsl = WgslWriter.Write(module);
        var parsed = WgslReader.Parse(wgsl); ModuleValidator.Validate(parsed);
        Assert.Equal(decoration == 21 ? MemoryDecorations.Volatile : MemoryDecorations.Coherent, parsed.Globals[0].MemoryDecorations);
        Assert.Equal(MemoryDecorations.None, parsed.Globals[1].MemoryDecorations);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(SpirvReader.Parse(binary.ToBytes()))));
    }

    [Theory]
    [InlineData(21u)] [InlineData(23u)]
    public void NestedArrayMemberRequirementsLiftToOnlyItsStorageOwner(uint decoration)
    {
        string source = """
            struct Inner { x:u32, y:u32, } struct Outer { values:array<Inner,2>, }
            @group(0) @binding(0) var<storage,read_write> data:Outer;
            @group(0) @binding(1) var<storage,read_write> output:array<u32>;
            @compute @workgroup_size(1) fn main(){output[0]=data.values[1].x;output[1]=data.values[0].y;}
            """;
        var input = SpirvReader.Parse(DecoratedSource(source, "Inner", 0, decoration).ToBytes());
        string wgsl = WgslWriter.Write(input); var roundtrip = WgslReader.Parse(wgsl); ModuleValidator.Validate(roundtrip);
        Assert.NotEqual(MemoryDecorations.None, roundtrip.Globals[0].MemoryDecorations);
        Assert.Equal(MemoryDecorations.None, roundtrip.Globals[1].MemoryDecorations);
        input.VulkanMemoryModel = true;
        var binary = SpirvBinary.Parse(SpirvWriter.Write(input));
        Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Load && i.Operands.Length > 3 && i.Operands[3] == (decoration == 21 ? 1 : 48));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MemberVolatileStrongExchangeRetainsOneNativeOperationAndRejectsWgslRetry(bool vulkan)
    {
        const string source = """
            struct Data { x:atomic<u32>, }
            @group(0) @binding(0) var<storage,read_write> data:Data;
            @group(0) @binding(1) var<storage,read_write> output:array<u32>;
            @compute @workgroup_size(1) fn main(){output[0]=atomicCompareExchangeWeak(&data.x,9u,10u).old_value;}
            """;
        var input = SpirvReader.Parse(DecoratedSource(source, "Data", 0, 21).ToBytes()); input.VulkanMemoryModel = vulkan;
        var binary = SpirvBinary.Parse(SpirvWriter.Write(input));
        var exchange = Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.AtomicCompareExchange);
        var constants = binary.Instructions.Where(i => (Op)i.Opcode == Op.Constant).ToDictionary(i => i.Operands[1], i => i.Operands[2]);
        Assert.Equal(vulkan ? 32768u : 0u, constants[exchange.Operands[4]]);
        Assert.Equal(vulkan ? 32768u : 0u, constants[exchange.Operands[5]]);
        Assert.Contains("cannot be duplicated", Assert.Throws<ShaderException>(() => WgslWriter.Write(input)).Message);
    }

    [Fact]
    public void LaterVolatileAccessIsKnownBeforeAnEarlierStrongExchangeIsLowered()
    {
        const string source = """
            @group(0) @binding(0) var<storage,read_write> data:atomic<u32>;
            @group(0) @binding(1) var<storage,read_write> output:array<u32>;
            @compute @workgroup_size(1) fn main(){
                output[0]=atomicCompareExchangeWeak(&data,9u,10u).old_value;
                output[1]=atomicLoad(&data);
            }
            """;
        var input = SpirvBinary.Parse(SpirvWriter.Write(WgslReader.Parse(source)));
        var code = input.Instructions.Select(i => (Op)i.Opcode == Op.AtomicLoad
            ? I(Op.Load, i.Operands[0], i.Operands[1], i.Operands[2], 1) : i).ToArray();
        var module = SpirvReader.Parse(new SpirvBinary { Version = input.Version, Bound = input.Bound, Instructions = code }.ToBytes());
        Assert.Contains("cannot be duplicated", Assert.Throws<ShaderException>(() => WgslWriter.Write(module)).Message);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CoherentAndVolatileBufferFlagsCombineForAllOrdinaryAccesses(bool pointerAlias)
    {
        string body = pointerAlias ? "let p=&data;output[0]=*p;*p=*p+1u;output[1]=*p;" : "output[0]=data;data=data+1u;output[1]=data;";
        var module = WgslReader.Parse("@coherent @volatile @group(0) @binding(0) var<storage,read_write> data:u32;"
            + "@group(0) @binding(1) var<storage,read_write> output:array<u32>;@compute @workgroup_size(1) fn main(){" + body + "}");
        module.VulkanMemoryModel = true;
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
        Assert.Equal(3, binary.Instructions.Count(i => (Op)i.Opcode == Op.Load && i.Operands.Length > 3 && i.Operands[3] == 49));
        Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Store && i.Operands.Length > 2 && i.Operands[2] == 41);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(SpirvReader.Parse(binary.ToBytes()))));
    }

    [Theory]
    [InlineData(1u)] [InlineData(2u)] [InlineData(3u)] [InlineData(4u)] [InlineData(5u)]
    public void OnlyProvenQueueFamilyRequirementsLiftToCoherentWgsl(uint scope)
    {
        var module = SpirvReader.Parse(MemoryAccessTests.Fixture(48, 40, true, scope: scope).ToBytes());
        if (scope == 5)
        {
            var parsed = WgslReader.Parse(WgslWriter.Write(module));
            Assert.All(parsed.Globals.Where(g => g.Space == AddressSpace.Storage), g => Assert.Equal(MemoryDecorations.Coherent, g.MemoryDecorations));
        }
        else Assert.Contains("availability/visibility", Assert.Throws<ShaderException>(() => WgslWriter.Write(module)).Message);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module)));
    }

    [Fact]
    public void CoherentDecorationDoesNotNarrowExplicitDeviceAccessScope()
    {
        var module = SpirvReader.Parse(MemoryAccessTests.Fixture(48, 40, true, scope: 1).ToBytes());
        for (int i = 0; i < module.Globals.Count; i++)
            if (module.Globals[i].Space == AddressSpace.Storage) module.Globals[i] = module.Globals[i] with { MemoryDecorations = MemoryDecorations.Coherent };
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
        var constants = binary.Instructions.Where(i => (Op)i.Opcode == Op.Constant).ToDictionary(i => i.Operands[1], i => i.Operands[2]);
        var load = Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Load && i.Operands.Length > 4);
        var store = Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Store && i.Operands.Length > 3);
        Assert.Equal(1u, constants[load.Operands[4]]); Assert.Equal(1u, constants[store.Operands[3]]);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 5346);
    }

    internal static SpirvBinary AggregateFixture(int mode, uint decoration)
    {
        var input = AtomicMemoryTests.AggregateFixture(mode); var code = input.Instructions.ToList();
        code.Insert(code.FindIndex(i => i.Opcode is >= 19 and <= 39), I(Op.MemberDecorate, 4, 0, decoration));
        return new() { Version = input.Version, Bound = input.Bound, Instructions = code };
    }

    [Theory]
    [InlineData(0, 21u)] [InlineData(1, 21u)] [InlineData(2, 21u)] [InlineData(3, 21u)]
    [InlineData(0, 23u)] [InlineData(1, 23u)] [InlineData(2, 23u)] [InlineData(3, 23u)]
    public void AtomicAggregateCopiesKeepMemberRequirements(int mode, uint decoration)
    {
        var module = SpirvReader.Parse(AggregateFixture(mode, decoration).ToBytes()); ModuleValidator.Validate(module);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
        module.VulkanMemoryModel = true;
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.MemberDecorate && i.Operands[2] == decoration);
        if (decoration == 21)
        {
            var constants = binary.Instructions.Where(i => (Op)i.Opcode == Op.Constant).ToDictionary(i => i.Operands[1], i => i.Operands[2]);
            var atomic = Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.AtomicIAdd);
            Assert.Equal(32768u, constants[atomic.Operands[4]]);
        }
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(SpirvReader.Parse(binary.ToBytes()))));
    }

    [Fact]
    public void OriginalVolatileFunctionMemoryRemainsExplicitWhileValueCopiesStayUnqualified()
    {
        const string source = """
            struct Data{x:u32, y:u32,}
            @group(0) @binding(0) var<storage,read> input:array<u32>;
            @group(0) @binding(1) var<storage,read_write> output:array<u32>;
            @compute @workgroup_size(1) fn main(){var local:Data;local.x=input[0];output[0]=local.x;}
            """;
        var input = DecoratedSource(source, "Data", 0, 21);
        var module = SpirvReader.Parse(input.ToBytes()); module.VulkanMemoryModel = true;
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
        Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Load && i.Operands.Length > 3 && i.Operands[3] == 1);
        // The input contains both the zero initializer store and local.x's store.
        Assert.Equal(2, binary.Instructions.Count(i => (Op)i.Opcode == Op.Store && i.Operands.Length > 2 && i.Operands[2] == 1));
        Assert.Contains("storage-buffer root", Assert.Throws<ShaderException>(() => WgslWriter.Write(SpirvReader.Parse(binary.ToBytes()))).Message);
    }

    [Theory]
    [InlineData("matrix")] [InlineData("neighbor")]
    public void UniformRootConversionCannotInventReadsOfQualifiedNeighbors(string selected)
    {
        var matrix = new ShaderType.Matrix(2, 2, ShaderType.F32);
        var data = new ShaderType.Structure("Data", [new("matrix", matrix) { MemoryDecorations = MemoryDecorations.Volatile }, new("neighbor", ShaderType.F32)]);
        var module = new Module(); module.Structures.Add(data);
        module.Globals.Add(new("data", data, AddressSpace.Uniform, StorageAccess.Read, new(0, 0)));
        module.Globals.Add(new("output", ShaderType.F32, AddressSpace.Storage, Binding: new(0, 1)));
        var root = new Expression.Reference("data", new ShaderType.Pointer(data, AddressSpace.Uniform, StorageAccess.Read));
        ShaderType type = selected == "matrix" ? matrix : ShaderType.F32;
        Expression value = new Expression.Load(new Expression.Member(root, selected, new ShaderType.Pointer(type, AddressSpace.Uniform, StorageAccess.Read)));
        if (selected == "matrix") value = new Expression.Access(new Expression.Access(value, Expression.U32(0), new ShaderType.Vector(2, ShaderType.F32)), Expression.U32(0), ShaderType.F32);
        var main = new ShaderFunction("main") { Stage = ShaderStage.Compute };
        main.Body.Statements.Add(new Statement.Store(new Expression.Reference("output", new ShaderType.Pointer(ShaderType.F32, AddressSpace.Storage)), value));
        module.Functions.Add(main); ModuleValidator.Validate(module);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
        var pointers = binary.Instructions.Where(i => (Op)i.Opcode == Op.TypePointer && i.Operands[1] == 2).Select(i => i.Operands[0]).ToHashSet();
        var addresses = binary.Instructions.Where(i => (Op)i.Opcode is Op.AccessChain or Op.Variable && pointers.Contains(i.Operands[0])).Select(i => i.Operands[1]).ToHashSet();
        var reads = binary.Instructions.Where(i => (Op)i.Opcode == Op.Load && addresses.Contains(i.Operands[2])).ToArray();
        Assert.Equal(selected == "matrix" ? 2 : 1, reads.Length);
        var floats = binary.Instructions.Where(i => (Op)i.Opcode == Op.TypeFloat).Select(i => i.Operands[0]).ToHashSet();
        if (selected == "neighbor") Assert.Contains(Assert.Single(reads).Operands[0], floats);
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }

    [Theory]
    [InlineData(21u)] [InlineData(23u)]
    public void WorkgroupMemberRequirementsCannotDisappearDuringVulkanConversion(uint decoration)
    {
        const string source = """
            struct Data{x:u32,}
            var<workgroup> group_data:Data;
            @group(0) @binding(0) var<storage,read> input:array<u32>;
            @group(0) @binding(1) var<storage,read_write> output:array<u32>;
            @compute @workgroup_size(1) fn main(){group_data.x=input[0];output[0]=group_data.x;}
            """;
        var module = SpirvReader.Parse(DecoratedSource(source, "Data", 0, decoration).ToBytes());
        module.VulkanMemoryModel = true;
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Load && i.Operands.Length > 4 && i.Operands[3] == (decoration == 21 ? 49 : 48));
        Assert.DoesNotContain(binary.Instructions, i => (Op)i.Opcode == Op.MemberDecorate && i.Operands[2] == decoration);
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
        if (decoration == 21) Assert.Contains("storage-buffer root", Assert.Throws<ShaderException>(() => WgslWriter.Write(SpirvReader.Parse(binary.ToBytes()))).Message);
        else ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(SpirvReader.Parse(binary.ToBytes()))));
    }
}
