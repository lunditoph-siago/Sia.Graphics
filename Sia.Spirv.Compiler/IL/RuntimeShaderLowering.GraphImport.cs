using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;

namespace Sia.Spirv.Compiler.IL;

internal sealed partial class RuntimeShaderLowering
{
    private Dictionary<string, ControlFlowFunction> ReadGraphs()
    {
        var graphs = new Dictionary<string, ControlFlowFunction>(StringComparer.Ordinal);
        foreach (var function in cilFunctions) {
            var returnOverride = ReferenceEquals(function.Signature, entry) ? entryResult : null;
            var graph = ReadGraph(function, module, returnOverride);
            function.Signature.Body = new();
            graphs.Add(function.Signature.Name, graph);
        }
        return graphs;
    }

    private static ControlFlowFunction ReadGraph(CilFunctionBody input, Module module, Expression? returnOverride)
    {
        var reader = StructuredControlFlowReader.Native(input.Signature, module);
        var graph = reader.NativeGraph;
        reader.NativeBody(input.Signature.Body);
        var prologue = reader.NativeCurrent;
        var blocks = input.Blocks.ToDictionary(static block => block.Id, _ => graph.Block());
        foreach (var block in input.Blocks) {
            foreach (var parameter in input.Incoming[block.Id]) {
                var value = graph.Value(parameter.Expression.Type);
                blocks[block.Id].Parameters.Add(value);
                reader.NativeBind(((Expression.Reference)parameter.Expression).Name, value);
            }
        }

        ControlFlowEdge Resolve(CilEdge edge)
            => new(blocks[edge.Target].Id, edge.Arguments.Select(reader.NativeValue));

        prologue.Terminator = new ControlFlowTerminator.Branch(new(blocks[0].Id));
        foreach (var block in input.Blocks) {
            reader.NativeBegin(blocks[block.Id]);
            reader.NativeBody(block.Body);
            if (reader.NativeCurrent.Terminator is null) {
                var terminator = ReadTerminator(block.Terminator, reader, Resolve, returnOverride);
                reader.NativeCurrent.Terminator = terminator;
            }
        }

        // Resource locations are CIL bookkeeping. Promote them to SSA before handing off the graph.
        LocalValuePromotion.Run(graph);
        if (graph.Blocks.SelectMany(static block => block.Instructions).Any(static instruction =>
            instruction.Operation is ValueOperation.Local
                && instruction.Result?.Type is ShaderType.Pointer pointer
                && CanonicalTypes.Resource(pointer.Base))) {
            throw Error(0, "Resource local has an uninitialized or escaping address; resource SSA requires an assigned value.");
        }
        return graph;
    }

    private static ControlFlowTerminator ReadTerminator(
        CilTerminator terminator,
        StructuredControlFlowReader reader,
        Func<CilEdge, ControlFlowEdge> resolve,
        Expression? returnOverride)
        => terminator switch {
            CilTerminator.Jump jump => new ControlFlowTerminator.Branch(resolve(jump.Edge)),
            CilTerminator.Conditional branch => new ControlFlowTerminator.Conditional(
                reader.NativeValue(branch.Condition), resolve(branch.True), resolve(branch.False)),
            CilTerminator.Switch branch => new ControlFlowTerminator.Switch(
                reader.NativeValue(branch.Selector),
                branch.Cases.Select((edge, index) => new ControlFlowCase([Expression.I32(index)], resolve(edge))).ToArray(),
                resolve(branch.Default)),
            CilTerminator.Return result => new ControlFlowTerminator.Return(
                (returnOverride ?? result.Value) is { } value ? reader.NativeValue(value) : null),
            _ => throw new ArgumentOutOfRangeException(nameof(terminator))
        };
}
