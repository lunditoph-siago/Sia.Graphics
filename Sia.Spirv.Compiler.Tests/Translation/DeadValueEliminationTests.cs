using Sia.Spirv.Compiler.Tests.IL;
using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Tests.Translation;

public sealed class DeadValueEliminationTests
{
    [Fact]
    public void IlDropsUnusedAddressCalculationsButKeepsOutput()
    {
        byte[] binary = RuntimeRefLocalTests.Compile(nameof(RefLocalShaders.UnusedAddress));
        var instructions = SpirvBinary.Parse(binary).Instructions;
        Assert.DoesNotContain(instructions, i => i.Opcode is (ushort)Op.IMul or (ushort)Op.IAdd);
        Assert.Contains(instructions, i => i.Opcode == (ushort)Op.Store);
    }

    [Fact]
    public void RemovesUnusedLoopCarriedCycleAndIncomingArguments()
    {
        var graph = new ControlFlowFunction(new ShaderFunction("main"));
        var entry = graph.Block();
        var loop = graph.Block();
        graph.Entry = entry.Id;
        var initial = graph.Value(ShaderType.U32);
        var parameter = graph.Value(ShaderType.U32);
        var next = graph.Value(ShaderType.U32);
        entry.Instructions.Add(new(initial, new ValueOperation.Literal(0u)));
        entry.Terminator = new ControlFlowTerminator.Branch(new(loop.Id, [initial]));
        loop.Parameters.Add(parameter);
        loop.Instructions.Add(new(next, new ValueOperation.Binary("+", parameter, initial)));
        loop.Terminator = new ControlFlowTerminator.Branch(new(loop.Id, [next]));

        DeadValueElimination.Run(graph);

        Assert.All(graph.Blocks, block => {
            Assert.Empty(block.Parameters);
            Assert.Empty(block.Instructions);
            Assert.Empty(Assert.Single(block.Terminator!.Edges).Arguments);
        });
    }

    [Fact]
    public void RemovesUnusedPointerArgumentOnceFromASharedEdge()
    {
        var graph = new ControlFlowFunction(new ShaderFunction("main") { ReturnType = ShaderType.U32 });
        var entry = graph.Block();
        var exit = graph.Block();
        graph.Entry = entry.Id;
        var condition = graph.Value(ShaderType.Bool);
        var pointer = graph.Value(new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage));
        var value = graph.Value(ShaderType.U32);
        entry.Instructions.Add(new(condition, new ValueOperation.Literal(true)));
        entry.Instructions.Add(new(pointer, new ValueOperation.Symbol("buffer")));
        entry.Instructions.Add(new(value, new ValueOperation.Literal(7u)));
        var edge = new ControlFlowEdge(exit.Id, [pointer, value]);
        entry.Terminator = new ControlFlowTerminator.Conditional(condition, edge, edge);
        var live = graph.Value(ShaderType.U32);
        exit.Parameters.AddRange([graph.Value(pointer.Type), live]);
        exit.Terminator = new ControlFlowTerminator.Return(live);

        DeadValueElimination.Run(graph);

        Assert.Equal(live, Assert.Single(exit.Parameters));
        Assert.Equal(value, Assert.Single(edge.Arguments));
        Assert.Equal(new SsaValue?[] { condition, value }, entry.Instructions.Select(i => i.Result));
    }

    [Fact]
    public void SourceValidationStillRejectsUnusedInvalidOperations()
    {
        Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv("""
            @compute @workgroup_size(1) fn main() { let unused = dpdx(1.0); }
            """, SpirvCompilationTarget.Default));
    }

    [Fact]
    public void WgslDropsUnusedAddressArithmeticButKeepsAtomicEffectsAndBarrier()
    {
        byte[] binary = ShaderTranslator.WgslToSpirv("""
            @group(0) @binding(0) var<storage, read_write> data: array<atomic<u32>>;
            @compute @workgroup_size(1) fn main() {
                let unused = &data[(atomicAdd(&data[0], 1u) * 3u + 1u) & 1u];
                workgroupBarrier();
            }
            """, SpirvCompilationTarget.Default);
        var instructions = SpirvBinary.Parse(binary).Instructions;
        Assert.DoesNotContain(instructions, i => i.Opcode is (ushort)Op.IMul or (ushort)Op.IAdd);
        Assert.Single(instructions, i => i.Opcode == (ushort)Op.AtomicIAdd);
        Assert.Single(instructions, i => i.Opcode == (ushort)Op.ControlBarrier);
        var result = WgslFixtureInterpreter.Execute(ShaderTranslator.SpirvToWgsl(binary, SpirvCompilationTarget.Default));
        Assert.Equal(new uint[] { 8, 0 }, result.Memory);
        Assert.Equal(new[] { "atomicAdd", "barrier" }, result.Effects);
    }
}
