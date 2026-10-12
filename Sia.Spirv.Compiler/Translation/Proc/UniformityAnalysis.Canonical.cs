using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Root = Sia.Spirv.Compiler.Translation.Proc.PointerAliasAnalysis.Root;

namespace Sia.Spirv.Compiler.Translation.Proc;

internal static partial class UniformityAnalysis
{
    /// <summary>Dependency graph over SSA values, block predicates and function-memory contents.
    /// Cyclic edges carry loop dependencies without replaying source statements.</summary>
    private sealed class CanonicalAnalysis(Module module, ControlFlowFunction graph, Func<string, Summary> callee,
        ISet<(string Function, int Value)>? divergentCollectives)
    {
        private readonly Node start = new(new(Source.Control));
        private readonly Node nonUniform = new(new(Source.NonUniform));
        private readonly Node returns = new();
        private readonly Node nonReturningControl = new();
        private readonly Dictionary<int, Node> callExits = [];
        private readonly Dictionary<int, Node> values = [];
        private readonly Dictionary<int, Node> controls = [];
        private readonly Dictionary<int, HashSet<Root>> origins = PointerAliasAnalysis.Origins(module, graph);
        private readonly HashSet<int> partial = [];
        private readonly Dictionary<int, Node> pointerOutputs = [];
        private readonly List<(Node Node, DiagnosticSeverity Severity, string? Rule, string Cause, SourceSpan Span, (string Function, int Value)? Collective)> requirements = [];
        private readonly Dictionary<string, GlobalVariable> globals = module.Globals.ToDictionary(g => g.Name, StringComparer.Ordinal);

        private Node Value(SsaValue value) => values[value.Id];
        private IEnumerable<Root> Roots(SsaValue value) => origins.GetValueOrDefault(value.Id) ?? [];
        private DiagnosticSeverity Severity(string? rule, ControlFlowInstruction instruction) => rule is null ? DiagnosticSeverity.Error
            : instruction.DiagnosticFilters.Concat(graph.Signature.DiagnosticFilters).Concat(module.DiagnosticFilters)
                .FirstOrDefault(f => f.Namespace is null && f.Rule == rule)?.Severity ?? DiagnosticSeverity.Error;
        private void Require(Node value, string? rule, string cause, ControlFlowInstruction instruction, DiagnosticSeverity? severity = null,
            (string Function, int Value)? collective = null)
        {
            var selected = severity ?? Severity(rule, instruction);
            if (selected != DiagnosticSeverity.Off) requirements.Add((value, selected, rule, cause, instruction.Span, collective));
        }

        public Summary Run(List<UniformityDiagnostic> diagnostics)
        {
            var arguments = graph.Signature.Arguments.Select((a, i) => (a, i)).ToDictionary(p => p.a.Name, StringComparer.Ordinal);
            foreach (var block in graph.Blocks) {
                controls.Add(block.Id, Join(start));
                callExits.Add(block.Id, new());
                foreach (var value in block.Parameters.Concat(block.Instructions.Where(i => i.Result is not null).Select(i => i.Result!.Value)))
                    values.Add(value.Id, new());
            }
            var blocks = graph.Blocks.ToDictionary(b => b.Id);
            foreach (var pair in ControlFlowAnalysis.ControlDependencies(graph))
                foreach (int branch in pair.Value) controls[pair.Key].Edges.UnionWith(blocks[branch].Terminator!.Operands.Select(Value));
            var predecessors = ControlFlowAnalysis.Predecessors(graph);
            var canLeave = graph.Blocks.ToDictionary(b => b.Id,
                b => !b.Instructions.Any(i => i.Operation is ValueOperation.Call call && !callee(call.Function).MayReturn));
            var live = new HashSet<int>(); var pending = new Stack<int>(); pending.Push(graph.Entry);
            while (pending.TryPop(out int id)) {
                if (!live.Add(id)) continue;
                if (!canLeave[id]) continue;
                foreach (var edge in blocks[id].Terminator!.Edges) pending.Push(edge.Target);
            }
            // A non-returning call never contributes a phi value or memory state
            // to the syntactic successor. Keep control dependence from conditional
            // termination in callExits, without inventing returned pointer contents.
            foreach (var incoming in predecessors.Values)
                incoming.RemoveAll(p => !live.Contains(p.Block.Id) || !canLeave[p.Block.Id]);
            foreach (var block in graph.Blocks) foreach (var predecessor in predecessors[block.Id]) {
                controls[block.Id].Edges.Add(callExits[predecessor.Block.Id]);
                callExits[block.Id].Edges.Add(callExits[predecessor.Block.Id]);
            }
            bool mayReturn = false;
            foreach (var block in graph.Blocks)
                for (int i = 0; i < block.Parameters.Count; i++) {
                    var incoming = predecessors[block.Id].Select(p => (p.Block, Value: p.Edge.Arguments[i])).ToArray();
                    var node = Value(block.Parameters[i]); node.Edges.UnionWith(incoming.Select(p => Value(p.Value)));
                    if (incoming.Select(p => p.Value.Id).Distinct().Skip(1).Any()) node.Edges.UnionWith(incoming.Select(p => controls[p.Block.Id]));
                }
            ComputePartialPointers(arguments);
            var roots = origins.Values.SelectMany(r => r).Where(r => r.Local != 0 || r.Parameter >= 0
                && graph.Signature.Arguments[r.Parameter].Type is ShaderType.Pointer { Space: AddressSpace.Function }).Distinct().ToArray();
            var entries = graph.Blocks.ToDictionary(b => b.Id, _ => roots.ToDictionary(r => r, _ => new Node()));
            var exits = graph.Blocks.ToDictionary(b => b.Id, _ => roots.ToDictionary(r => r, _ => new Node()));
            foreach (var root in roots) {
                if (root.Parameter >= 0) {
                    entries[graph.Entry][root].Edges.Add(new(new(Source.Contents, root.Parameter)));
                    pointerOutputs.Add(root.Parameter, new());
                }
                else entries[graph.Entry][root].Edges.Add(start);
            }
            foreach (var block in graph.Blocks)
                foreach (var predecessor in predecessors[block.Id])
                    foreach (var root in roots) entries[block.Id][root].Edges.Add(exits[predecessor.Block.Id][root]);
            foreach (var block in graph.Blocks) {
                if (!live.Contains(block.Id)) continue;
                var control = controls[block.Id]; var memory = new Dictionary<Root, Node>(entries[block.Id]);
                bool aborted = false;
                foreach (var instruction in block.Instructions) {
                    Node result;
                    switch (instruction.Operation) {
                        case ValueOperation.Symbol symbol:
                            result = arguments.TryGetValue(symbol.Name, out var argument)
                                ? Join(control, graph.Signature.Stage is null ? new(new(Source.Argument, argument.i))
                                    : UniformInput(argument.a.Type, argument.a.Binding, graph.Signature.Stage) ? start : nonUniform)
                                : control;
                            break;
                        case ValueOperation.Local allocation:
                            if (instruction.Result is { } local) foreach (var root in Roots(local)) memory[root] = allocation.ZeroInitialize ? control : Join(control, nonUniform);
                            result = control; break;
                        case ValueOperation.Load load: result = Read(load.Pointer, control, memory); break;
                        case ValueOperation.HelperInvocation: result = Join(control, nonUniform); break;
                        case ValueOperation.Store store: Store(store.Pointer, Value(store.Value), control, memory); result = control; break;
                        case ValueOperation.Call call:
                            result = Call(call, instruction, ref control, memory, callExits[block.Id]);
                            aborted = !callee(call.Function).MayReturn; break;
                        case ValueOperation.Builtin builtin: result = Builtin(builtin, instruction, control); break;
                        case ValueOperation.Barrier barrier:
                            if (barrier.Control) Require(control, barrier.Subgroup ? "subgroup_uniformity" : null, "barrier", instruction);
                            result = control; break;
                        case ValueOperation.MeshSetOutputs:
                            Require(control, null, "mesh output publication", instruction);
                            result = control; break;
                        default: result = Join(instruction.Operation.Operands.Select(Value).Prepend(control).ToArray()); break;
                    }
                    if (instruction.Result is { } value) Value(value).Edges.Add(result);
                    if (aborted) break;
                }
                foreach (var root in roots) exits[block.Id][root].Edges.Add(memory[root]);
                if (!aborted && block.Terminator is ControlFlowTerminator.Return ret) {
                    mayReturn = true;
                    if (ret.Value is { } value) returns.Edges.Add(Join(control, Value(value)));
                    foreach (var pair in pointerOutputs) pair.Value.Edges.Add(Join(control, memory[new Root(pair.Key)]));
                }
                if (!aborted && block.Terminator is ControlFlowTerminator.Unreachable or ControlFlowTerminator.InvocationKill or ControlFlowTerminator.TaskDispatch)
                    nonReturningControl.Edges.Add(control);
                if (!aborted && block.Terminator is ControlFlowTerminator.TaskDispatch dispatch)
                    requirements.Add((control, DiagnosticSeverity.Error, null, "task dispatch", dispatch.Span, null));
            }
            return Finish(graph.Signature.Name, returns, pointerOutputs, requirements, diagnostics, nonReturningControl, mayReturn, divergentCollectives);
        }

        private void ComputePartialPointers(Dictionary<string, (FunctionArgument a, int i)> arguments)
        {
            bool changed;
            do {
                changed = false;
                foreach (var instruction in graph.Blocks.SelectMany(b => b.Instructions)) {
                    if (instruction.Result is not { Type: ShaderType.Pointer } result) continue;
                    bool projected = instruction.Operation switch {
                        ValueOperation.Access or ValueOperation.Member or ValueOperation.Swizzle => true,
                        ValueOperation.Symbol symbol when arguments.TryGetValue(symbol.Name, out var argument)
                            => argument.a.Type is ShaderType.Pointer { Base: not ShaderType.Scalar },
                        ValueOperation.Let let => partial.Contains(let.Value.Id),
                        ValueOperation.Select select => partial.Contains(select.Accept.Id) || partial.Contains(select.Reject.Id),
                        _ => false
                    };
                    if (projected) changed |= partial.Add(result.Id);
                }
                var blocks = graph.Blocks.ToDictionary(b => b.Id);
                foreach (var edge in graph.Blocks.SelectMany(b => b.Terminator!.Edges))
                    for (int i = 0; i < edge.Arguments.Count; i++)
                        if (partial.Contains(edge.Arguments[i].Id)) changed |= partial.Add(blocks[edge.Target].Parameters[i].Id);
            } while (changed);
        }

        private Node Read(SsaValue pointer, Node control, Dictionary<Root, Node> memory)
        {
            var contents = Roots(pointer).Select(root => memory.TryGetValue(root, out var value) ? value
                : root.Global is { } name && globals.TryGetValue(name, out var global)
                    && (global.Space is AddressSpace.Uniform or AddressSpace.Immediate or AddressSpace.Handle || (global.Access & StorageAccess.Write) == 0)
                    ? control : nonUniform).ToArray();
            return Join(contents.Prepend(Value(pointer)).Prepend(control).Concat(contents.Length == 0 ? [nonUniform] : []).ToArray());
        }
        private void Store(SsaValue pointer, Node value, Node control, Dictionary<Root, Node> memory)
        {
            var roots = Roots(pointer).ToArray();
            foreach (var root in roots) if (memory.TryGetValue(root, out var previous))
                memory[root] = partial.Contains(pointer.Id) || roots.Length > 1 ? Join(control, Value(pointer), value, previous) : Join(control, Value(pointer), value);
        }
        private Node Call(ValueOperation.Call call, ControlFlowInstruction instruction, ref Node control, Dictionary<Root, Node> memory, Node exit)
        {
            var inputControl = control;
            var arguments = call.Arguments.Select(Value).ToArray();
            var contents = call.Arguments.Select(a => a.Type is ShaderType.Pointer ? Read(a, inputControl, memory) : inputControl).ToArray();
            Node Bind(IEnumerable<Dependency> dependencies) => Join(dependencies.Select(d => d.Source switch {
                Source.Control => inputControl, Source.NonUniform => nonUniform,
                Source.Argument => d.Index < arguments.Length ? arguments[d.Index] : nonUniform,
                Source.Contents => d.Index < contents.Length ? contents[d.Index] : nonUniform, _ => nonUniform
            }).ToArray());
            var summary = callee(call.Function);
            foreach (var requirement in summary.Requirements)
                Require(Bind(requirement.Dependencies), requirement.Rule, call.Function + " -> " + requirement.Cause, instruction, requirement.Severity, requirement.Collective);
            var writes = summary.Contents.Select(p => (p.Key, Value: Bind(p.Value))).ToArray();
            foreach (var write in writes) if (write.Key < call.Arguments.Count) Store(call.Arguments[write.Key], write.Value, control, memory);
            var result = Join(control, Bind(summary.Result));
            if (summary.NonReturningControl.Count != 0 || !summary.MayReturn) {
                var abortControl = Join(control, Bind(summary.NonReturningControl));
                exit.Edges.Add(abortControl); nonReturningControl.Edges.Add(abortControl);
                control = abortControl;
            }
            return result;
        }
        private Node Builtin(ValueOperation.Builtin builtin, ControlFlowInstruction instruction, Node control)
            => BuiltinValue(builtin.Function, builtin.Arguments.Select(Value).ToArray(), builtin.Arguments.FirstOrDefault().Type, control, nonUniform,
                (value, rule, cause) => Require(value, rule, cause, instruction, collective:
                    builtin.Function == "workgroupUniformLoad" && instruction.Result is { } result ? (graph.Signature.Name, result.Id) : null));
    }
}
