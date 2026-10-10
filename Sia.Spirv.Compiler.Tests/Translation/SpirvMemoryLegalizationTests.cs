using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class SpirvMemoryLegalizationTests
{
    [Fact]
    public void TaskPayloadAtomicVolatilityKeepsWorkgroupScope()
    {
        var prepared = ShaderTargetLowering.ForSpirv(WorkgroupMemoryTests.TaskFixture(true, MemoryDecorations.Volatile), null, true, true, true);
        var function = prepared.Module.Functions.Single(f => f.Stage == ShaderStage.Task);
        var calls = prepared.PhysicalLayout.ControlFlow[function.Name].Graph.Blocks.SelectMany(b => b.Instructions)
            .Select(i => i.Operation).OfType<ValueOperation.Builtin>().Where(c => c.Function.StartsWith("atomic", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, calls.Length);
        Assert.All(calls, call => Assert.Equal(new SpirvAtomicMemory(2, 32768), call.AtomicMemory));
    }

    [Theory]
    [InlineData("Triangles")] [InlineData("VulkanMemory")]
    public void PublicationMappingsReferToFinalQualifiedFunctions(string variant)
    {
        var prepared = ShaderTargetLowering.ForSpirv(SpirvMeshPublicationTests.PublicationFixture(variant), null, true, true, true);
        foreach (var publication in prepared.MeshPublications.Values)
            Assert.Same(prepared.Module.Functions.Single(f => f.Name == publication.Function.Name), publication.Function);
    }

    [Fact]
    public void CapturedMemberAliasRetainsQualificationsAcrossShadowing()
    {
        var data = new ShaderType.Structure("Data", [new StructMember("value", ShaderType.U32) { MemoryDecorations = MemoryDecorations.Volatile }]);
        var input = new Module { VulkanMemoryModel = true, WorkgroupInitializationRequired = false };
        input.Structures.Add(data);
        input.Globals.Add(new("buffer", data, AddressSpace.Storage, Binding: new(0, 0)) { MemoryDecorations = MemoryDecorations.Coherent });
        var member = new Expression.Member(new Expression.Reference("buffer", data), "value", ShaderType.U32);
        var pointer = new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage);
        var alias = new Expression.Reference("alias", pointer);
        var dereference = new Expression.Unary("*", alias, ShaderType.U32);
        var function = new ShaderFunction("main") { Stage = ShaderStage.Compute };
        input.Functions.Add(function);
        function.Body.Statements.Add(new Statement.Declare("alias", pointer, new Expression.Unary("&", member, pointer), false));
        var nested = new Block();
        nested.Statements.Add(new Statement.Declare("buffer", data, new Expression.Construct(data, [Expression.U32(9)])));
        nested.Statements.Add(new Statement.Store(dereference, Expression.U32(7)) { MemoryAccess = new(2, Alignment: 4, AvailableScope: 1) });
        nested.Statements.Add(new Statement.Declare("read", ShaderType.U32, dereference, false));
        nested.Statements.Add(new Statement.Declare("local", data, new Expression.Reference("buffer", data), false));
        function.Body.Statements.Add(new Statement.Nested(nested));
        var output = SpirvMemoryAccessLowering.Run(input);
        var body = Assert.IsType<Statement.Nested>(output.Functions.Single().Body.Statements[1]).Body;
        Assert.Equal(new SpirvMemoryAccess(43, Alignment: 4, AvailableScope: 1), Assert.Single(body.Statements.OfType<Statement.Store>()).MemoryAccess);
        var read = Assert.IsType<Expression.Load>(body.Statements.OfType<Statement.Declare>().Single(d => d.Name == "read").Initializer);
        Assert.Equal(new SpirvMemoryAccess(49, VisibleScope: 5), read.MemoryAccess);
        Assert.IsType<Expression.Reference>(body.Statements.OfType<Statement.Declare>().Single(d => d.Name == "local").Initializer);
        Assert.Same(output, SpirvMemoryAccessLowering.Run(output));
        Assert.Equal(new SpirvMemoryAccess(2, Alignment: 4, AvailableScope: 1), Assert.Single(nested.Statements.OfType<Statement.Store>()).MemoryAccess);
        ModuleValidator.ValidateNative(SpirvReader.Parse(SpirvWriter.Emit(SpirvPhysicalLayoutLowering.Prepare(input)).ToBytes()));
    }

    [Fact]
    public void FunctionSnapshotsKeepOriginalOperandsWithoutInheritingTypeDecorations()
    {
        var data = new ShaderType.Structure("Data", [new StructMember("value", ShaderType.U32) { MemoryDecorations = MemoryDecorations.Coherent | MemoryDecorations.Volatile }]);
        var module = new Module { VulkanMemoryModel = true };
        module.Structures.Add(data);
        var function = new ShaderFunction("main"); module.Functions.Add(function);
        function.Body.Statements.Add(new Statement.Declare("snapshot", data, new Expression.Construct(data, [Expression.U32(9)])));
        var member = new Expression.Member(new Expression.Reference("snapshot", data), "value", ShaderType.U32);
        function.Body.Statements.Add(new Statement.Declare("read", ShaderType.U32, new Expression.Load(member) { MemoryAccess = new(3, Alignment: 4) }, false));
        var output = SpirvMemoryAccessLowering.Run(module);
        Assert.Same(module, output);
        Assert.Equal(new SpirvMemoryAccess(3, Alignment: 4), Assert.IsType<Expression.Load>(Assert.IsType<Statement.Declare>(function.Body.Statements[1]).Initializer).MemoryAccess);
    }

    [Fact]
    public void LoopContinuingSeesCapturedAliasAndItsOwnShadow()
    {
        var module = WgslReader.Parse("@group(0) @binding(0) var<storage,read_write> buffer:array<u32>; @compute @workgroup_size(1) fn main(){loop{let p=&buffer[0];continuing{*p=3u; let buffer=5u; break if buffer==5u;}}}");
        module.VulkanMemoryModel = true;
        module.Globals[0] = module.Globals[0] with { MemoryDecorations = MemoryDecorations.Coherent | MemoryDecorations.Volatile };
        var output = SpirvMemoryAccessLowering.Run(module);
        var loop = Assert.Single(output.Functions.Single().Body.Statements.OfType<Statement.Loop>());
        Assert.Equal(new SpirvMemoryAccess(41, AvailableScope: 5), Assert.Single(loop.Continuing.Statements.OfType<Statement.Store>()).MemoryAccess);
        ModuleValidator.Validate(output);
    }

    [Fact]
    public void UnresolvedUserFunctionNamedAtomicLoadRetainsIdentityAndMetadata()
    {
        var pointer = new ShaderType.Pointer(new ShaderType.Atomic(ShaderType.U32), AddressSpace.Storage);
        var module = new Module { VulkanMemoryModel = true };
        module.Globals.Add(new("counter", pointer.Base, AddressSpace.Storage) { MemoryDecorations = MemoryDecorations.Volatile });
        var user = new ShaderFunction("atomicLoad"); module.Functions.Add(user);
        var caller = new ShaderFunction("caller"); module.Functions.Add(caller);
        var call = new Expression.Call("atomicLoad", [new Expression.Unary("&", new Expression.Reference("counter", pointer.Base), pointer)], new ShaderType.Void());
        caller.Body.Statements.Add(new Statement.Evaluate(call));
        Assert.Same(module, SpirvMemoryAccessLowering.Run(module));
        Assert.Null(call.AtomicMemory);
    }

    [Fact]
    public void WorkgroupCoherenceIsVisibleBeforeSerialization()
    {
        var input = WgslReader.Parse("var<workgroup> group_value:u32; @compute @workgroup_size(1) fn main(){group_value=7u; let value=group_value;}");
        input.VulkanMemoryModel = true;
        var prepared = SpirvPhysicalLayoutLowering.Prepare(input);
        var instructions = prepared.ControlFlow["main"].Graph.Blocks.SelectMany(b => b.Instructions).Select(i => i.Operation).ToArray();
        var store = Assert.Single(instructions.OfType<ValueOperation.Store>());
        Assert.Equal(new SpirvMemoryAccess(40, AvailableScope: 2), store.MemoryAccess);
        var load = Assert.Single(instructions.OfType<ValueOperation.Load>());
        Assert.Equal(new SpirvMemoryAccess(48, VisibleScope: 2), load.MemoryAccess);
        Assert.Null(Assert.Single(input.Functions.Single().Body.Statements.OfType<Statement.Store>()).MemoryAccess);
        Assert.Null(Assert.IsType<Expression.Load>(Assert.Single(input.Functions.Single().Body.Statements.OfType<Statement.Declare>(), d => d.Initializer is Expression.Load).Initializer).MemoryAccess);
        ModuleValidator.ValidateNative(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()));
    }

    [Fact]
    public void AtomicVolatilityIsVisibleBeforeSerialization()
    {
        var input = WgslReader.Parse("@group(0) @binding(0) var<storage,read_write> counter:atomic<u32>; @compute @workgroup_size(1) fn main(){let value=atomicAdd(&counter,1u);}");
        input.VulkanMemoryModel = true;
        input.Globals[0] = input.Globals[0] with { MemoryDecorations = MemoryDecorations.Volatile };
        var prepared = SpirvPhysicalLayoutLowering.Prepare(input);
        var call = Assert.Single(prepared.ControlFlow["main"].Graph.Blocks.SelectMany(b => b.Instructions)
            .Select(i => i.Operation).OfType<ValueOperation.Builtin>());
        Assert.Equal(new SpirvAtomicMemory(5, 32768), call.AtomicMemory);
        Assert.Null(Assert.IsType<Expression.Call>(Assert.Single(input.Functions.Single().Body.Statements.OfType<Statement.Declare>(), d => d.Initializer is Expression.Call).Initializer).AtomicMemory);
        ModuleValidator.ValidateNative(SpirvReader.Parse(SpirvWriter.Emit(prepared).ToBytes()));
    }
}
