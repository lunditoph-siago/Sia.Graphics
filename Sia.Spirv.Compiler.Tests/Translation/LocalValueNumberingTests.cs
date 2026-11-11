using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;

namespace Sia.Spirv.Compiler.Tests.Translation;

public sealed class LocalValueNumberingTests
{
    [Fact]
    public void DoesNotMergeConversionsAcrossARuntimeLetBoundary()
    {
        var graph = new ControlFlowFunction(new ShaderFunction("stages") { ReturnType = ShaderType.I32 });
        var block = graph.Block(); graph.Entry = block.Id;
        var zero = graph.Value(ShaderType.U32);
        var constant = graph.Value(ShaderType.I32);
        var alias = graph.Value(ShaderType.U32);
        var runtime = graph.Value(ShaderType.I32);
        block.Instructions.Add(new(zero, new ValueOperation.Literal(0u)));
        block.Instructions.Add(new(constant, new ValueOperation.Convert(zero, false)));
        block.Instructions.Add(new(alias, new ValueOperation.Let("runtime", zero)));
        block.Instructions.Add(new(runtime, new ValueOperation.Convert(alias, false)));
        block.Terminator = new ControlFlowTerminator.Return(runtime);

        LocalValueNumbering.Run(graph);
        ConstantConversionFolding.Run(graph);

        Assert.IsType<ValueOperation.Literal>(Assert.Single(block.Instructions, i => i.Result == constant).Operation);
        Assert.IsType<ValueOperation.Convert>(Assert.Single(block.Instructions, i => i.Result == runtime).Operation);
        Assert.Equal(runtime, Assert.IsType<ControlFlowTerminator.Return>(block.Terminator).Value);
    }

    [Theory]
    [InlineData(2, 0x8000UL, 0x7e01UL, 0x7e02UL)]
    [InlineData(4, 0x80000000UL, 0x7fc00001UL, 0x7fc00002UL)]
    [InlineData(8, 0x8000000000000000UL, 0x7ff8000000000001UL, 0x7ff8000000000002UL)]
    public void FloatingKeysPreserveSignedZeroAndNanPayloads(int width, ulong negativeZero, ulong nanA, ulong nanB)
    {
        ulong[] bits = [0, negativeZero, nanA, nanB, nanA];
        var type = new ShaderType.Scalar(ScalarKind.Float, width);
        var graph = new ControlFlowFunction(new ShaderFunction("floating") { ReturnType = type });
        var block = graph.Block(); graph.Entry = block.Id;
        foreach (ulong payload in bits) {
            object value = width switch {
                2 => (object)BitConverter.UInt16BitsToHalf((ushort)payload),
                4 => BitConverter.UInt32BitsToSingle((uint)payload),
                _ => BitConverter.UInt64BitsToDouble(payload)
            };
            block.Instructions.Add(new(graph.Value(type), new ValueOperation.Literal(value)));
        }
        block.Terminator = new ControlFlowTerminator.Return(block.Instructions[^1].Result);
        var nan = block.Instructions[2].Result;

        LocalValueNumbering.Run(graph);

        Assert.Equal(4, block.Instructions.Count);
        Assert.Equal(nan, Assert.IsType<ControlFlowTerminator.Return>(block.Terminator).Value);
        Assert.Equal(bits.Take(4), block.Instructions.Select(i => ((ValueOperation.Literal)i.Operation).Value switch {
            Half h => (ulong)BitConverter.HalfToUInt16Bits(h),
            float f => BitConverter.SingleToUInt32Bits(f),
            double d => BitConverter.DoubleToUInt64Bits(d),
            _ => throw new InvalidOperationException()
        }));
    }

    [Fact]
    public void RewritesOnlyTheIncomingValueFromTheBranchWithADuplicate()
    {
        var graph = new ControlFlowFunction(new ShaderFunction("branch") { ReturnType = ShaderType.U32 });
        var entry = graph.Block();
        var yes = graph.Block();
        var no = graph.Block();
        var exit = graph.Block();
        graph.Entry = entry.Id;
        var x = graph.Value(ShaderType.U32);
        var condition = graph.Value(ShaderType.Bool);
        entry.Instructions.Add(new(x, new ValueOperation.Symbol("x")));
        entry.Instructions.Add(new(condition, new ValueOperation.Symbol("condition")));
        entry.Terminator = new ControlFlowTerminator.Conditional(condition, new(yes.Id), new(no.Id));
        var product = new ValueOperation.Binary("*", x, x);
        var first = graph.Value(ShaderType.U32);
        var duplicate = graph.Value(ShaderType.U32);
        var other = graph.Value(ShaderType.U32);
        yes.Instructions.Add(new(first, product));
        yes.Instructions.Add(new(duplicate, product));
        no.Instructions.Add(new(other, product));
        var yesEdge = new ControlFlowEdge(exit.Id, [duplicate]);
        var noEdge = new ControlFlowEdge(exit.Id, [other]);
        yes.Terminator = new ControlFlowTerminator.Branch(yesEdge);
        no.Terminator = new ControlFlowTerminator.Branch(noEdge);
        var parameter = graph.Value(ShaderType.U32);
        exit.Parameters.Add(parameter);
        exit.Terminator = new ControlFlowTerminator.Return(parameter);

        LocalValueNumbering.Run(graph);

        Assert.Equal(first, Assert.Single(yes.Instructions).Result);
        Assert.Equal(other, Assert.Single(no.Instructions).Result);
        Assert.Equal(first, Assert.Single(yesEdge.Arguments));
        Assert.Equal(other, Assert.Single(noEdge.Arguments));
    }

    [Fact]
    public void KeepsOperandOrder()
    {
        var graph = new ControlFlowFunction(new ShaderFunction("order"));
        var block = graph.Block(); graph.Entry = block.Id;
        var x = graph.Value(ShaderType.F32); var y = graph.Value(ShaderType.F32);
        block.Instructions.Add(new(x, new ValueOperation.Symbol("x")));
        block.Instructions.Add(new(y, new ValueOperation.Symbol("y")));
        block.Instructions.Add(new(graph.Value(ShaderType.F32), new ValueOperation.Binary("+", x, y)));
        block.Instructions.Add(new(graph.Value(ShaderType.F32), new ValueOperation.Binary("+", y, x)));
        block.Terminator = new ControlFlowTerminator.Return();

        LocalValueNumbering.Run(graph);

        Assert.Equal(2, block.Instructions.Count(i => i.Operation is ValueOperation.Binary));
    }

    [Fact]
    public void DoesNotMergeLoadsCallsAtomicsConvergentQueriesOrAllocations()
    {
        var graph = new ControlFlowFunction(new ShaderFunction("effects"));
        var block = graph.Block(); graph.Entry = block.Id;
        var pointer = graph.Value(new ShaderType.Pointer(ShaderType.U32, AddressSpace.Storage));
        block.Instructions.Add(new(pointer, new ValueOperation.Symbol("data")));
        for (int i = 0; i < 2; i++) {
            block.Instructions.Add(new(graph.Value(ShaderType.U32), new ValueOperation.Load(pointer)));
            block.Instructions.Add(new(graph.Value(ShaderType.U32), new ValueOperation.Call("read", [], ShaderType.U32)));
            block.Instructions.Add(new(graph.Value(ShaderType.U32), new ValueOperation.Builtin("atomicLoad", [pointer], ShaderType.U32)));
            block.Instructions.Add(new(graph.Value(ShaderType.Bool), new ValueOperation.HelperInvocation()));
            block.Instructions.Add(new(graph.Value(new ShaderType.Pointer(ShaderType.U32, AddressSpace.Function)), new ValueOperation.Local("scratch", false)));
            block.Instructions.Add(new(null, new ValueOperation.Barrier(true, false, true, false, false)));
        }
        block.Terminator = new ControlFlowTerminator.Return();
        var results = block.Instructions.Select(i => i.Result).ToArray();

        LocalValueNumbering.Run(graph);

        Assert.Equal(results, block.Instructions.Select(i => i.Result));
    }
}
