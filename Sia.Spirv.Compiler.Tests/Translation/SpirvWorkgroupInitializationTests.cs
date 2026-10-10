using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;
using Sia.Spirv.Compiler.Compilation;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class SpirvWorkgroupInitializationTests
{
    internal const string Source = "var<workgroup> group_data:u32;@group(0) @binding(0) var<storage,read_write> output:u32;@compute @workgroup_size(4) fn main(){output=group_data;}";
    internal const string PendingSource = """
        @id(7) override count:u32=5u;
        var<workgroup> group_data:array<u32,count>;
        @group(0) @binding(0) var<storage,read> input:array<u32>;
        @group(0) @binding(1) var<storage,read_write> output:array<u32>;
        @compute @workgroup_size(16) fn main(@builtin(local_invocation_index) i:u32){
            if(i<count){output[2u*i]=group_data[i];group_data[i]=input[i];output[2u*i+1u]=group_data[i];}
        }
        """;

    [Fact]
    public void RawSerializationDoesNotSelectImplicitInitialization()
    {
        var module = WgslReader.Parse(Source);
        Assert.Equal(SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(module)).ToBytes(),
            SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(module)).ToBytes());
    }

    [Fact]
    public void PublicPolicyRemainsEnabledByDefaultAndCanBeDisabled()
    {
        var module = WgslReader.Parse(Source);
        var enabled = SpirvBinary.Parse(SpirvWriter.Write(module));
        var disabled = SpirvBinary.Parse(SpirvWriter.Write(module, new(ZeroInitializeWorkgroupMemory: false)));
        Assert.Single(enabled.Instructions, i => (Op)i.Opcode == Op.ControlBarrier);
        Assert.DoesNotContain(disabled.Instructions, i => (Op)i.Opcode == Op.ControlBarrier);
    }

    [Theory]
    [InlineData("scalar", MemoryDecorations.None)] [InlineData("scalar", MemoryDecorations.Volatile)]
    [InlineData("nested", MemoryDecorations.Volatile)] [InlineData("alias", MemoryDecorations.Volatile)]
    [InlineData("specialized", MemoryDecorations.None)] [InlineData("specialized", MemoryDecorations.Volatile)]
    [InlineData("atomic", MemoryDecorations.None)] [InlineData("atomic", MemoryDecorations.Volatile)]
    public void PreparedInitializationHasVerifiedMemoryAndSynchronizationEffects(string kind, MemoryDecorations decoration)
    {
        var input = WorkgroupMemoryTests.Fixture(kind, decoration);
        var prepared = ShaderTargetLowering.ForSpirv(input, null, true, true, true);
        var initializer = Assert.Single(prepared.WorkgroupInitializers).Value;
        Assert.Same(initializer, prepared.Module.Functions.Single(f => f.Name == initializer.Name));
        Assert.Null(initializer.Stage);
        var graph = prepared.PhysicalLayout.ControlFlow[initializer.Name].Graph;
        ControlFlowVerifier.Validate(graph, prepared.Module);
        var effects = graph.Blocks.SelectMany(b => b.Instructions).ToArray();
        var barrier = Assert.Single(effects.Select(i => i.Operation).OfType<ValueOperation.Barrier>());
        Assert.True(barrier.Control && barrier.Workgroup);
        Assert.Equal(new SpirvBarrierMemory(2, 0x108, 2), barrier.NativeMemory);
        Assert.Contains(effects, i => (i.Effects & ShaderEffects.WriteMemory) != 0);
        Assert.All(effects.Select(i => i.Operation).OfType<ValueOperation.Store>().Where(s => s.Pointer.Type is ShaderType.Pointer { Space: AddressSpace.Workgroup }),
            s => { Assert.Equal(40u, s.MemoryAccess!.Flags & ~1u); Assert.Equal(2u, s.MemoryAccess.AvailableScope); });
        if (kind == "atomic") {
            var store = Assert.Single(effects.Select(i => i.Operation).OfType<ValueOperation.Builtin>());
            Assert.Equal("atomicStore", store.Function);
            Assert.Equal(new SpirvAtomicMemory(2, decoration == MemoryDecorations.Volatile ? 32768u : 0u), store.AtomicMemory);
        }
        var binary = SpirvWriter.Emit(prepared);
        Assert.Equal(binary.ToBytes(), SpirvWriter.Emit(prepared).ToBytes());
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EntryCallsInitializationBeforeTheBodyAndCollectsUnusedSharedGlobals(bool structInput)
    {
        string argument = structInput ? "input:In" : "@builtin(local_invocation_index) i:u32";
        string source = "struct In{@builtin(local_invocation_index) i:u32,}var<workgroup> unused:u32;var<workgroup> group_data:u32;"
            + "@compute @workgroup_size(4) fn main(" + argument + "){group_data=7u;}";
        // Version-sensitive interface policy is chosen during target preparation.
        var prepared = ShaderTargetLowering.ForSpirv(WgslReader.Parse(source), null, true, true, true,
            version: SpirvCompilationTarget.Default.Version);
        var binary = SpirvWriter.Emit(prepared);
        uint Named(string name) => Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Name
            && SpirvBinary.ReadString(i.Operands.AsSpan(1), out _) == name).Operands[0];
        uint original = Named("main"), initialize = Named(Assert.Single(prepared.WorkgroupInitializers).Value.Name);
        var entry = Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.EntryPoint);
        int first = binary.Instructions.ToList().FindIndex(i => (Op)i.Opcode == Op.Function && i.Operands[1] == entry.Operands[1]);
        var wrapper = binary.Instructions.Skip(first + 1).TakeWhile(i => (Op)i.Opcode != Op.FunctionEnd).ToList();
        Assert.Equal(new[] { initialize, original }, wrapper.Where(i => (Op)i.Opcode == Op.FunctionCall).Select(i => i.Operands[2]));
        Assert.DoesNotContain(wrapper, i => (Op)i.Opcode is Op.Store or Op.ControlBarrier or Op.LoopMerge or Op.SelectionMerge);
        Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.Decorate && i.Operands.SequenceEqual(new[] { i.Operands[0], 11u, 29u }));
        Assert.Contains(Named("unused"), entry.Operands); Assert.Contains(Named("group_data"), entry.Operands);
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }

    [Fact]
    public void MultipleEntriesShareInitializationWithoutMutatingBorrowedBodies()
    {
        var input = WgslReader.Parse(Source + "@compute @workgroup_size(2) fn other(){output=group_data;}fn sia_spv_workgroup_initialize(){} const sia_local_invocation_index=1u;");
        var bodies = input.Functions.Select(f => f.Body).ToArray();
        var prepared = ShaderTargetLowering.ForSpirv(input, null, true, true, true);
        Assert.Equal(2, prepared.WorkgroupInitializers.Count);
        Assert.Same(prepared.WorkgroupInitializers["main"], prepared.WorkgroupInitializers["other"]);
        Assert.Equal("sia_spv_workgroup_initialize_1", prepared.WorkgroupInitializers["main"].Name);
        Assert.Equal("sia_local_invocation_index_1", Assert.Single(prepared.WorkgroupInitializers["main"].Arguments).Name);
        Assert.Equal(bodies, input.Functions.Select(f => f.Body));
        Assert.Equal(SpirvWriter.Write(input), SpirvWriter.Write(input));
    }

    [Fact]
    public void NativeInputAndOptoutCannotGenerateAnotherInitializer()
    {
        var input = WgslReader.Parse(Source);
        Assert.Empty(ShaderTargetLowering.ForSpirv(input, null, true, true, true, false).WorkgroupInitializers);
        var imported = SpirvReader.Parse(SpirvWriter.Write(input));
        Assert.False(imported.WorkgroupInitializationRequired);
        Assert.Empty(ShaderTargetLowering.ForSpirv(imported, null, true, true, true).WorkgroupInitializers);
        Assert.Single(SpirvBinary.Parse(SpirvWriter.Write(imported)).Instructions, i => (Op)i.Opcode == Op.ControlBarrier);
    }

    [Theory]
    [InlineData("@compute @workgroup_size(1) fn main(){}")]
    [InlineData("var<workgroup> group_data:u32;@fragment fn main(){}")]
    public void EntriesWithoutEligibleSharedInitializationHaveNoHelper(string source)
        => Assert.Empty(ShaderTargetLowering.ForSpirv(WgslReader.Parse(source), null, true, true, true).WorkgroupInitializers);

    [Theory]
    [InlineData("u32", false)] [InlineData("i32", false)] [InlineData("u32", true)] [InlineData("i32", true)]
    public void SpecializedArrayLengthRetainsItsIdentityOrResolvesBeforeInitialization(string kind, bool resolve)
    {
        var input = WgslReader.Parse("@id(7) override count:" + kind + "=3;var<workgroup> group_data:array<u32,count>;@compute @workgroup_size(1) fn main(){}");
        var options = new SpirvWriteOptions { PipelineConstants = resolve ? new Dictionary<string, double> { ["7"] = 5 } : null };
        var binary = SpirvBinary.Parse(SpirvWriter.Write(input, options));
        Assert.Equal(!resolve, binary.Instructions.Any(i => (Op)i.Opcode == Op.LoopMerge));
        Assert.Equal(!resolve, binary.Instructions.Any(i => (Op)i.Opcode == Op.Decorate && i.Operands.Length == 3 && i.Operands[1] == 1 && i.Operands[2] == 7));
        ModuleValidator.Validate(SpirvReader.Parse(binary.ToBytes()));
    }
}
