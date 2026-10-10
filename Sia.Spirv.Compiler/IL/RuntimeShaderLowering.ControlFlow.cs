using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Sia.Spirv.Compiler.Metadata;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.IL;

internal sealed partial class RuntimeShaderLowering
{
    private sealed record CilEdge(int Target, Expression[] Arguments);
    private sealed record CilBlockBody(int Id, Block Body, Func<StructuredControlFlowReader, Func<CilEdge, ControlFlowEdge>, ControlFlowTerminator> Finish);
    private sealed record CilFunctionBody(ShaderFunction Signature, Dictionary<int, Value[]> Incoming, List<CilBlockBody> Blocks);
    private readonly List<CilFunctionBody> cilFunctions = [];
    private Expression? entryResult;

    private void Function(ShaderFunction function, int token, Value[] arguments, CilControlFlowGraph? inputGraph = null, Expression? returnOverride = null)
    {
        var method = metadata.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.EntityHandle(token));
        var nativeBody = pe.GetMethodBody(method.RelativeVirtualAddress);
        if (nativeBody.ExceptionRegions.Length != 0) throw Error(0, "Exception regions are not supported.");
        var bytes = nativeBody.GetILBytes() ?? throw Error(0, "Method has no CIL.");
        var graph = inputGraph ?? CilControlFlowGraph.Create(CilInstructionDecoder.Decode(bytes), bytes.Length);
        arguments = arguments.Select(argument => {
            if (argument.Place || argument.Expression.Type is ShaderType.Image or ShaderType.Sampler) return argument;
            var slot = Place(Name("argument"), argument.Expression.Type);
            function.Body.Statements.Add(new Statement.Declare(slot.Name, argument.Expression.Type, argument.Expression));
            return new Value(slot, true, true);
        }).ToArray();
        var locals = nativeBody.LocalSignature.IsNil ? [] : metadata.GetStandaloneSignature(nativeBody.LocalSignature).DecodeLocalSignature(new KernelTypeProvider(), null).Select(Type).ToArray();
        var localValues = locals.Select(type => {
            var place = Place(Name("local"), type);
            function.Body.Statements.Add(new Statement.Declare(place.Name, type, null));
            return new Value(place, true);
        }).ToArray();
        var byOffset = graph.Blocks.ToDictionary(b => b.StartOffset);
        var incoming = new Dictionary<int, Value[]> { [0] = [] };
        var queue = new Queue<int>(); queue.Enqueue(0);
        var blocks = new List<CilBlockBody>();
        while (queue.TryDequeue(out int id)) {
            var native = graph.Blocks[id]; var output = new Block(); var stack = new Stack<Value>(incoming[id]);
            Expression[]? edgeValues = null;
            CilEdge Edge(int target) {
                if (edgeValues is null) {
                    var values = stack.Reverse().ToArray();
                    if (values.Any(v => v.Place || v.Expression.Type is ShaderType.Pointer))
                        throw Error(native.StartOffset, "Pointer values across CIL stack edges require address provenance lowering.");
                    edgeValues = values.Select(v => Snapshot(Read(v, output), output).Expression).ToArray();
                }
                if (!incoming.TryGetValue(target, out var parameters)) {
                    parameters = edgeValues.Select(v => new Value(new Expression.Reference(Name("stack"), v.Type))).ToArray();
                    incoming.Add(target, parameters); queue.Enqueue(target);
                }
                if (parameters.Length != edgeValues.Length) throw Error(native.StartOffset, "CIL evaluation stack edge mismatch.");
                return new(target, edgeValues.Select((v, i) => Convert(v, parameters[i].Expression.Type, true)).ToArray());
            }
            Func<StructuredControlFlowReader, Func<CilEdge, ControlFlowEdge>, ControlFlowTerminator>? finish = null;
            foreach (var instruction in native.Instructions) {
                string op = instruction.OpCode.Name!; int at = instruction.Offset;
                if (op is "br" or "br.s") {
                    var edge = Edge(byOffset[instruction.Operand.GetInt32(at)].Id);
                    finish = (_, resolve) => new ControlFlowTerminator.Branch(resolve(edge)); break;
                }
                if (op == "switch") {
                    var selector = Convert(Read(stack.Pop(), output), ShaderType.I32, true);
                    var edges = instruction.Operand.GetSwitchTargets(at).Select(t => Edge(byOffset[t].Id)).ToArray();
                    var fallback = Edge(byOffset[instruction.EndOffset].Id);
                    finish = (reader, resolve) => new ControlFlowTerminator.Switch(reader.NativeValue(selector),
                        edges.Select((edge, i) => new ControlFlowCase([Expression.I32(i)], resolve(edge))).ToArray(), resolve(fallback)); break;
                }
                if (instruction.OpCode.FlowControl == System.Reflection.Emit.FlowControl.Cond_Branch) {
                    var condition = Branch(instruction, stack, output);
                    var yes = Edge(byOffset[instruction.Operand.GetInt32(at)].Id); var no = Edge(byOffset[instruction.EndOffset].Id);
                    finish = (reader, resolve) => new ControlFlowTerminator.Conditional(reader.NativeValue(condition), resolve(yes), resolve(no)); break;
                }
                if (op == "ret") {
                    var value = returnOverride ?? (function.ReturnType is ShaderType.Void ? null : Convert(Read(stack.Pop(), output), function.ReturnType, true));
                    finish = (reader, _) => new ControlFlowTerminator.Return(ReferenceEquals(function, entry) && entryResult is not null
                        ? reader.NativeValue(entryResult) : value is null ? null : reader.NativeValue(value)); break;
                }
                Instruction(instruction, stack, arguments, localValues, output);
            }
            if (finish is null && native.Successors.Count == 1) {
                var edge = Edge(native.Successors[0]); finish = (_, resolve) => new ControlFlowTerminator.Branch(resolve(edge));
            }
            if (finish is null) throw Error(native.StartOffset, "CIL block has no terminator.");
            blocks.Add(new(id, output, finish));
        }
        cilFunctions.Add(new(function, incoming, blocks));
    }

    private Dictionary<string, ControlFlowFunction> ReadGraphs()
    {
        var result = new Dictionary<string, ControlFlowFunction>(StringComparer.Ordinal);
        foreach (var input in cilFunctions) {
            var reader = StructuredControlFlowReader.Native(input.Signature, module); var graph = reader.NativeGraph;
            reader.NativeBody(input.Signature.Body);
            var prologue = reader.NativeCurrent;
            var blocks = input.Blocks.ToDictionary(b => b.Id, _ => graph.Block());
            foreach (var block in input.Blocks) foreach (var parameter in input.Incoming[block.Id]) {
                var value = graph.Value(parameter.Expression.Type); blocks[block.Id].Parameters.Add(value);
                reader.NativeBind(((Expression.Reference)parameter.Expression).Name, value);
            }
            prologue.Terminator = new ControlFlowTerminator.Branch(new(blocks[0].Id));
            foreach (var block in input.Blocks) {
                reader.NativeBegin(blocks[block.Id]); reader.NativeBody(block.Body);
                if (reader.NativeCurrent.Terminator is null) {
                    var terminator = block.Finish(reader, edge => new(blocks[edge.Target].Id, edge.Arguments.Select(reader.NativeValue)));
                    reader.NativeCurrent.Terminator = terminator;
                }
            }
            input.Signature.Body = new(); result.Add(input.Signature.Name, graph);
        }
        return result;
    }
}
