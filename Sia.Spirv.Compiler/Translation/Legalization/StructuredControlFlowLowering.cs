using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Temporary target adapter: lower verified SSA CFG to the existing structured writer representation.</summary>
internal static class StructuredControlFlowLowering
{
    /// <summary>Explicit compatibility boundary for consumers not yet migrated to graphs.</summary>
    public static Module Run(CanonicalModule canonical, bool nativeValidation = false)
    {
        var input = canonical.Declarations;
        var output = new Module { VulkanMemoryModel = input.VulkanMemoryModel, WorkgroupInitializationRequired = input.WorkgroupInitializationRequired };
        output.Structures.AddRange(input.Structures); output.Constants.AddRange(input.Constants); output.Globals.AddRange(input.Globals);
        output.Enables.UnionWith(input.Enables); output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        foreach (var function in input.Functions)
            output.Functions.Add(canonical.Functions.TryGetValue(function.Name, out var graph) ? Run(graph, input) : function);
        var remainingCalls = output.Functions.SelectMany(f => ControlFlowAnalysis.Calls(f.Body)).ToHashSet(StringComparer.Ordinal);
        output.Functions.RemoveAll(f => f.Stage is null && f.ReturnType is ShaderType.Pointer
            && canonical.EntryFunctions.Contains(f.Name) && !remainingCalls.Contains(f.Name));
        if (canonical.Functions.Values.Any(g => g.Blocks.Any(b => b.Parameters.Any(p => p.Type is ShaderType.Pointer)))) {
            // Until target address dispatch consumes graphs, preserve its existing
            // expansion-before-dispatch order at this explicit adapter boundary.
            output = HelperInliner.RunPointers(output);
            output = new PointerSelectionLowering(output).Run();
        }
        if (nativeValidation) ModuleValidator.ValidateNative(output); else ModuleValidator.Validate(output);
        return output;
    }

    public static ShaderFunction Run(ControlFlowFunction function, Module module)
    {
        var signature = function.Signature;
        var output = new ShaderFunction(signature.Name) {
            Stage = signature.Stage, ReturnType = signature.ReturnType, ReturnBinding = signature.ReturnBinding,
            WorkgroupSize = signature.WorkgroupSize.ToArray(), TaskPayload = signature.TaskPayload, MeshOutput = signature.MeshOutput,
            EarlyDepthTest = signature.EarlyDepthTest, ConservativeDepth = signature.ConservativeDepth
        };
        output.Arguments.AddRange(signature.Arguments); output.DiagnosticFilters.AddRange(signature.DiagnosticFilters);
        output.Body.DiagnosticFilters.AddRange(signature.Body.DiagnosticFilters);
        var names = module.Globals.Select(g => g.Name).Concat(module.Constants.Select(c => c.Name))
            .Concat(module.Structures.Select(s => s.Name)).Concat(module.Functions.Select(f => f.Name))
            .Concat(signature.Arguments.Select(a => a.Name)).ToHashSet(StringComparer.Ordinal);
        int next = 0;
        string Fresh() { string name; do name = "sia_cfg_" + next++; while (!names.Add(name)); return name; }
        Expression.Reference Place(string name, ShaderType type) => new(name, new ShaderType.Pointer(type, AddressSpace.Function));
        var expressions = new Dictionary<int, Expression>();
        var definitions = function.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null).ToDictionary(i => i.Result!.Value.Id);
        var originalNames = definitions.Values.Select(i => i.Operation).OfType<ValueOperation.Let>().GroupBy(l => l.Name).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        names.UnionWith(originalNames.Keys);
        var symbols = definitions.Values.Select(i => i.Operation).OfType<ValueOperation.Symbol>().Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        string ValueName(ControlFlowInstruction? definition) {
            if (definition?.Operation is ValueOperation.Let let && originalNames[let.Name] == 1 && !symbols.Contains(let.Name)
                && !signature.Arguments.Any(a => a.Name == let.Name) && !module.Functions.Any(f => f.Name == let.Name)
                && !ShaderBuiltinEffects.IsKnown(let.Name)) {
                // Flattened scopes may reuse an unused module constant's name, but never shadow a live symbol.
                names.Add(let.Name); return let.Name;
            }
            return Fresh();
        }
        var owners = function.Blocks.SelectMany(b => b.Instructions.Where(i => i.Result is not null).Select(i => (Id: i.Result!.Value.Id, Block: b.Id))).ToDictionary(i => i.Id, i => i.Block);
        var crossBlock = function.Blocks.SelectMany(b => b.Instructions.SelectMany(i => i.Operation.Operands)
            .Concat(b.Terminator!.Operands).Concat(b.Terminator.Edges.SelectMany(e => e.Arguments))
            .Where(v => owners.TryGetValue(v.Id, out int owner) && owner != b.Id)).Select(v => v.Id).ToHashSet();
        var used = function.Blocks.SelectMany(b => b.Instructions.SelectMany(i => i.Operation.Operands)
            .Concat(b.Terminator!.Operands).Concat(b.Terminator.Edges.SelectMany(e => e.Arguments))).Select(v => v.Id).ToHashSet();
        foreach (var value in function.Blocks.SelectMany(b => b.Parameters.Concat(b.Instructions.Where(i => i.Result is not null).Select(i => i.Result!.Value)))) {
            if (value.Type is ShaderType.Pointer && definitions.TryGetValue(value.Id, out var instruction) && instruction.Operation is ValueOperation.Local) {
                var type = ((ShaderType.Pointer)value.Type).Base; string name = Fresh();
                if (type is not ShaderType.RayQuery)
                    output.Body.Statements.Add(new Statement.Declare(name, type, null) { Initialize = false });
                expressions.Add(value.Id, Place(name, type));
            }
            else if (CanonicalTypes.Data(value.Type)) {
                bool parameter = !definitions.TryGetValue(value.Id, out var definition);
                if (!parameter && definition!.Operation is ValueOperation.Literal or ValueOperation.Symbol) continue;
                string name = ValueName(definition);
                if (parameter || crossBlock.Contains(value.Id) || definition!.DiagnosticFilters.Count != 0) {
                    output.Body.Statements.Add(new Statement.Declare(name, value.Type, null));
                    expressions.Add(value.Id, new Expression.Load(Place(name, value.Type)));
                }
                else expressions.Add(value.Id, new Expression.Reference(name, value.Type));
            }
            else if (value.Type is not ShaderType.Pointer && function.Blocks.Any(b => b.Parameters.Contains(value)))
                throw new ShaderException(DiagnosticStage.Validation, "Pointer or opaque block parameters require further target legalization.");
        }
        CanonicalPointerLowering? pointerParameters = null;
        pointerParameters = new CanonicalPointerLowering(function, output.Body, Fresh, Use);
        Expression Use(SsaValue value) {
            if (expressions.TryGetValue(value.Id, out var expression)) return expression;
            if (pointerParameters?.IsParameter(value) == true) return pointerParameters.Address(value);
            var instruction = definitions[value.Id];
            expression = instruction.Operation switch {
                ValueOperation.Symbol symbol when value.Type is ShaderType.Pointer && signature.Arguments.Any(a => a.Name == symbol.Name)
                    => new Expression.Unary("*", new Expression.Reference(symbol.Name, value.Type), value.Type),
                ValueOperation.Symbol symbol => new Expression.Reference(symbol.Name, value.Type),
                ValueOperation.Literal literal => new Expression.Literal(literal.Value, value.Type),
                ValueOperation.Access access => new Expression.Access(Use(access.Base), Use(access.Index), value.Type),
                ValueOperation.Member member => new Expression.Member(Use(member.Base), member.Name, value.Type),
                ValueOperation.Let alias => Use(alias.Value),
                _ => throw new ShaderException(DiagnosticStage.Validation, "Unsupported address value in CFG target adapter.")
            };
            expressions.Add(value.Id, expression); return expression;
        }
        Expression CallArgument(SsaValue value) => value.Type is not ShaderType.Pointer ? Use(value)
            : Use(value) is Expression.Unary { Operator: "*" } dereference ? dereference.Operand
            : new Expression.Unary("&", Use(value), value.Type);
        var blocks = function.Blocks.ToDictionary(b => b.Id);
        Block EdgeCopies(ControlFlowEdge edge) {
            var body = new Block(); var snapshots = new List<(Expression Target, Expression Value)>();
            for (int i = 0; i < edge.Arguments.Count; i++) {
                var argument = edge.Arguments[i]; var parameter = blocks[edge.Target].Parameters[i];
                var copies = parameter.Type is ShaderType.Pointer ? pointerParameters.EdgeValues(argument, parameter)
                    : [(((Expression.Load)Use(parameter)).Pointer as Expression.Reference ?? throw new InvalidOperationException("Expected scalar phi slot."), Use(argument))];
                foreach (var (target, value) in copies) {
                    string name = Fresh(); body.Statements.Add(new Statement.Declare(name, value.Type, value, false));
                    snapshots.Add((target, new Expression.Reference(name, value.Type)));
                }
            }
            foreach (var (target, value) in snapshots) body.Statements.Add(new Statement.Store(target, value));
            return body;
        }
        var instructions = new Dictionary<int, Block>();
        foreach (var block in function.Blocks) {
            var body = new Block();
            for (int instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++) {
                var instruction = block.Instructions[instructionIndex];
                if (instruction.Operation is ValueOperation.Local) {
                    if (instruction.Result is { Type: ShaderType.Pointer { Base: ShaderType.RayQuery query } } handle)
                        // Query state starts fresh at the original allocation,
                        // including each loop iteration. Never hoist that reset.
                        body.Statements.Add(new Statement.Declare(((Expression.Reference)Use(handle)).Name, query, null) { Span = instruction.Span });
                    continue;
                }
                var emission = body;
                if (instruction.DiagnosticFilters.Count != 0) {
                    emission = new Block(); emission.DiagnosticFilters.AddRange(instruction.DiagnosticFilters);
                    body.Statements.Add(new Statement.Nested(emission));
                }
                if (instruction.Operation is ValueOperation.Barrier barrier) {
                    Statement statement = barrier.Control
                        ? new Statement.Barrier(barrier.Storage, barrier.Workgroup, barrier.Texture, barrier.Subgroup) { NativeMemory = barrier.NativeMemory }
                        : new Statement.MemoryBarrier(barrier.Storage, barrier.Workgroup, barrier.Texture, barrier.Subgroup) { NativeMemory = barrier.NativeMemory };
                    emission.Statements.Add(statement with { Span = instruction.Span }); continue;
                }
                if (instruction.Operation is ValueOperation.Literal or ValueOperation.Symbol) continue;
                if (instruction.Operation is ValueOperation.Demote) {
                    emission.Statements.Add(new Statement.Kill { Span = instruction.Span }); continue;
                }
                if (instruction.Operation is ValueOperation.Store store) {
                    emission.Statements.Add(new Statement.Store(Use(store.Pointer), Use(store.Value)) { MemoryAccess = store.MemoryAccess, Span = instruction.Span }); continue;
                }
                if (instruction.Operation is ValueOperation.MeshStore meshStore) {
                    emission.Statements.Add(new Statement.MeshStore(meshStore.Field, Use(meshStore.Index), Use(meshStore.Value)) { Span = instruction.Span }); continue;
                }
                if (instruction.Operation is ValueOperation.MeshSetOutputs meshCounts) {
                    emission.Statements.Add(new Statement.MeshSetOutputs(Use(meshCounts.Vertices), Use(meshCounts.Primitives)) { Span = instruction.Span }); continue;
                }
                if (instruction.Operation is ValueOperation.Call { ReturnType: ShaderType.Void } voidCall) {
                    emission.Statements.Add(new Statement.Evaluate(new Expression.Call(voidCall.Function, voidCall.Arguments.Select(CallArgument).ToArray(), voidCall.ReturnType)
                        { Binding = CallBinding.Function, AtomicMemory = voidCall.AtomicMemory, MemoryAccess = voidCall.MemoryAccess, Span = instruction.Span }) { Span = instruction.Span }); continue;
                }
                if (instruction.Operation is ValueOperation.Builtin { ReturnType: ShaderType.Void } voidBuiltin) {
                    emission.Statements.Add(new Statement.Evaluate(new Expression.Call(voidBuiltin.Function, voidBuiltin.Arguments.Select(CallArgument).ToArray(), voidBuiltin.ReturnType)
                        { Binding = CallBinding.Builtin, AtomicMemory = voidBuiltin.AtomicMemory, MemoryAccess = voidBuiltin.MemoryAccess, Span = instruction.Span }) { Span = instruction.Span }); continue;
                }
                var result = instruction.Result!.Value;
                if (!CanonicalTypes.Data(result.Type)) {
                    var address = Use(result);
                    if (instruction.Operation is ValueOperation.Local { ZeroInitialize: true }
                        && !(block.Instructions.ElementAtOrDefault(instructionIndex + 1)?.Operation is ValueOperation.Store { MemoryAccess: null } initial && initial.Pointer == result))
                        emission.Statements.Add(new Statement.Store(address, new Expression.Construct(((ShaderType.Pointer)result.Type).Base, [])) { Span = instruction.Span });
                    if (result.Type is ShaderType.Pointer && instruction.Operation is ValueOperation.Let) {
                        // Retain explicit address formation for target legality, even when the alias is unused.
                        // Access indices have already been captured as SSA values before this declaration.
                        emission.Statements.Add(new Statement.Declare(Fresh(), result.Type,
                            new Expression.Unary("&", address, result.Type) { Span = instruction.Span }, false) { Span = instruction.Span });
                    }
                    continue;
                }
                Expression value = instruction.Operation switch {
                    ValueOperation.HelperInvocation => new Expression.HelperInvocation(),
                    ValueOperation.Literal literal => new Expression.Literal(literal.Value, result.Type),
                    ValueOperation.Let let => Use(let.Value),
                    ValueOperation.Symbol source => new Expression.Reference(source.Name, result.Type),
                    ValueOperation.Load load => new Expression.Load(Use(load.Pointer)) { MemoryAccess = load.MemoryAccess },
                    ValueOperation.Unary unary => new Expression.Unary(unary.Operator, Use(unary.Operand), result.Type),
                    ValueOperation.Binary binary => new Expression.Binary(binary.Operator, Use(binary.Left), Use(binary.Right), result.Type),
                    ValueOperation.Convert convert => new Expression.Convert(result.Type, Use(convert.Operand), convert.Bitcast),
                    ValueOperation.Construct construct => new Expression.Construct(result.Type, construct.Components.Select(Use).ToArray()),
                    ValueOperation.Select select => new Expression.Select(Use(select.Condition), Use(select.Accept), Use(select.Reject)),
                    ValueOperation.Access access => new Expression.Access(Use(access.Base), Use(access.Index), result.Type),
                    ValueOperation.Member member => new Expression.Member(Use(member.Base), member.Name, result.Type),
                    ValueOperation.Swizzle swizzle => new Expression.Swizzle(Use(swizzle.Vector), swizzle.Components, result.Type),
                    ValueOperation.Call call => new Expression.Call(call.Function, call.Arguments.Select(CallArgument).ToArray(), call.ReturnType)
                        { Binding = CallBinding.Function, AtomicMemory = call.AtomicMemory, MemoryAccess = call.MemoryAccess },
                    ValueOperation.Builtin builtin => new Expression.Call(builtin.Function, builtin.Arguments.Select(CallArgument).ToArray(), result.Type)
                        { Binding = CallBinding.Builtin, AtomicMemory = builtin.AtomicMemory, MemoryAccess = builtin.MemoryAccess },
                    _ => throw new ShaderException(DiagnosticStage.Validation, "Unsupported value in CFG target adapter.")
                };
                var destination = Use(result);
                if (!used.Contains(result.Id) && instruction.Operation is ValueOperation.Builtin or ValueOperation.Call) {
                    emission.Statements.Add(new Statement.Evaluate(value with { Span = instruction.Span }) { Span = instruction.Span }); continue;
                }
                if (destination is Expression.Reference reference)
                    emission.Statements.Add(new Statement.Declare(reference.Name, result.Type, value with { Span = instruction.Span }, false) { Span = instruction.Span });
                else emission.Statements.Add(new Statement.Store(((Expression.Load)destination).Pointer, value with { Span = instruction.Span }) { Span = instruction.Span });
            }
            instructions.Add(block.Id, body);
        }
        var visited = new HashSet<int>(); var breakTargets = new Stack<int?>();
        var nonLocalExits = new Dictionary<int, Expression.Reference>();
        ShaderException Invalid(string message) => new(DiagnosticStage.Validation, "Canonical structured control in " + signature.Name + ": " + message);
        Expression.Reference ExitFlag(int target) {
            if (!nonLocalExits.TryGetValue(target, out var flag)) {
                string name = Fresh(); flag = Place(name, ShaderType.Bool); nonLocalExits.Add(target, flag);
                output.Body.Statements.Add(new Statement.Declare(name, ShaderType.Bool, Expression.Bool(false)));
            }
            return flag;
        }
        void ResetExit(int? target, Block body) {
            if (target is int id && nonLocalExits.TryGetValue(id, out var flag)) body.Statements.Add(new Statement.Store(flag, Expression.Bool(false)));
        }
        void PropagateExit(Block body) {
            foreach (var (target, flag) in nonLocalExits.Where(p => breakTargets.Contains(p.Key))) {
                var exit = new Block(); exit.Statements.Add(new Statement.Break());
                body.Statements.Add(new Statement.If(new Expression.Load(flag), exit, new()));
            }
        }
        int? Transfer(ControlFlowEdge edge, Block body, int? stop, LoopContext? loop, bool continuing) {
            body.Statements.AddRange(EdgeCopies(edge).Statements);
            if (breakTargets.Contains(edge.Target) && breakTargets.Peek() != edge.Target) {
                if (continuing) throw Invalid("nonlocal exit from continuing region");
                // Leave nested breakable regions in order, with the return-edge
                // values already captured. No remaining helper effects may run.
                body.Statements.Add(new Statement.Store(ExitFlag(edge.Target), Expression.Bool(true)));
                body.Statements.Add(new Statement.Break()); return null;
            }
            if (edge.Target == stop) return null;
            if (loop is not null) {
                if (edge.Target == loop.Header) throw Invalid("backedge bypasses the continuing region");
                if (edge.Target == loop.Continuing) {
                    if (continuing) throw Invalid("continuing region has a cyclic entry");
                    body.Statements.Add(new Statement.Continue()); return null;
                }
                if (edge.Target == loop.Merge) {
                    if (continuing) throw Invalid("continuing exit requires a final break-if");
                    if (breakTargets.Count == 0 || breakTargets.Peek() != loop.Merge) throw Invalid("loop exit crosses another breakable region");
                    body.Statements.Add(new Statement.Break()); return null;
                }
            }
            if (breakTargets.Count != 0 && edge.Target == breakTargets.Peek()) {
                body.Statements.Add(new Statement.Break()); return null;
            }
            return edge.Target;
        }
        Block Arm(ControlFlowEdge edge, int? stop, LoopContext? loop, bool continuing) {
            var body = new Block();
            if (Transfer(edge, body, stop, loop, continuing) is int nextBlock)
                body.Statements.AddRange(Sequence(nextBlock, stop, loop, continuing).Statements);
            return body;
        }
        Block Sequence(int start, int? stop, LoopContext? loop = null, bool continuing = false, bool loopEntry = false) {
            var body = new Block(); int? nextBlock = start;
            while (nextBlock is int id && id != stop) {
                if (function.Loops.TryGetValue(id, out var region) && !(loopEntry && loop?.Header == id)) {
                    var nested = new LoopContext(id, region.Continuing, region.Merge);
                    breakTargets.Push(region.Merge);
                    var loopBody = Sequence(id, region.Continuing, nested, loopEntry: true);
                    var tail = region.Continuing is int target ? Sequence(target, id, nested, continuing: true) : new Block();
                    breakTargets.Pop(); ResetExit(region.Merge, body);
                    body.Statements.Add(new Statement.Loop(loopBody, tail, nested.BreakIf));
                    PropagateExit(body);
                    nextBlock = region.Merge; continue;
                }
                loopEntry = false;
                if (!visited.Add(id)) throw Invalid("overlapping or unstructured region at b" + id);
                body.Statements.AddRange(instructions[id].Statements);
                switch (blocks[id].Terminator!) {
                    case ControlFlowTerminator.Branch branch:
                        nextBlock = Transfer(branch.Edge, body, stop, loop, continuing); break;
                    case ControlFlowTerminator.Conditional conditional:
                        if (continuing && loop is not null && loop.Merge is int merge
                            && (conditional.Accept.Target == merge && conditional.Reject.Target == loop.Header
                                || conditional.Reject.Target == merge && conditional.Accept.Target == loop.Header)) {
                            // Edge copies may update the very phi used by the condition. Capture
                            // it before either copy set, then use that immutable value in break-if.
                            string name = Fresh(); var captured = new Expression.Reference(name, ShaderType.Bool);
                            body.Statements.Add(new Statement.Declare(name, ShaderType.Bool, Use(conditional.Condition), false));
                            body.Statements.Add(new Statement.If(captured, EdgeCopies(conditional.Accept), EdgeCopies(conditional.Reject)));
                            loop.BreakIf = conditional.Accept.Target == merge ? captured : new Expression.Unary("!", captured, ShaderType.Bool);
                            nextBlock = null; break;
                        }
                        if (!continuing && loop is not null && id == loop.Header && loop.Merge is int loopMerge
                            && (conditional.Accept.Target == loopMerge || conditional.Reject.Target == loopMerge)) {
                            // A loop header's conditional exit belongs to the loop merge;
                            // it does not require a separate selection merge annotation.
                            body.Statements.Add(new Statement.If(Use(conditional.Condition),
                                Arm(conditional.Accept, stop, loop, false), Arm(conditional.Reject, stop, loop, false)));
                            nextBlock = null; break;
                        }
                        if (!function.SelectionMerges.TryGetValue(id, out var join)) {
                            if (!continuing && stop is int enclosingMerge && function.SelectionMerges.Values.Contains(enclosingMerge)
                                && (conditional.Accept.Target == enclosingMerge || conditional.Reject.Target == enclosingMerge)) {
                                // A native conditional exit can use its enclosing selection's
                                // merge without opening another selection. Keep both edge-copy
                                // sets inside their arms; the caller emits the merge exactly once.
                                body.Statements.Add(new Statement.If(Use(conditional.Condition),
                                    Arm(conditional.Accept, stop, loop, false), Arm(conditional.Reject, stop, loop, false)));
                                nextBlock = null; break;
                            }
                            throw Invalid("missing selection merge at b" + id);
                        }
                        body.Statements.Add(new Statement.If(Use(conditional.Condition), Arm(conditional.Accept, join ?? stop, loop, continuing), Arm(conditional.Reject, join ?? stop, loop, continuing)));
                        nextBlock = join; break;
                    case ControlFlowTerminator.Switch selection:
                        if (!function.SelectionMerges.TryGetValue(id, out var done)) throw Invalid("missing switch merge at b" + id);
                        breakTargets.Push(done);
                        var choices = new List<SwitchCase>();
                        foreach (var group in selection.Cases.GroupBy(c => c.Edge.Target)) {
                            var edge = group.First().Edge;
                            if (group.Any(c => !c.Edge.Arguments.SequenceEqual(edge.Arguments))
                                || edge.Target == selection.Default.Target && !edge.Arguments.SequenceEqual(selection.Default.Arguments))
                                throw Invalid("switch edges from one predecessor disagree on incoming values");
                            // Multiple literals share one executable region. A matching
                            // default already covers all literals mapped to that region.
                            if (edge.Target != selection.Default.Target)
                                choices.Add(new(group.SelectMany(c => c.Values).Distinct().ToArray(), false, Arm(edge, done, loop, continuing)));
                        }
                        choices.Add(new([], true, Arm(selection.Default, done, loop, continuing)));
                        breakTargets.Pop(); ResetExit(done, body);
                        body.Statements.Add(new Statement.Switch(Use(selection.Selector), choices)); PropagateExit(body);
                        nextBlock = done; break;
                    case ControlFlowTerminator.Return returned:
                        if (continuing) throw Invalid("return from continuing region");
                        body.Statements.Add(new Statement.Return(returned.Value is { } value ? CallArgument(value) : null) { Span = returned.Span }); nextBlock = null; break;
                    case ControlFlowTerminator.Unreachable unreachable:
                        body.Statements.Add(new Statement.Unreachable { Span = unreachable.Span }); nextBlock = null; break;
                    case ControlFlowTerminator.InvocationKill kill:
                        body.Statements.Add(new Statement.InvocationKill { Span = kill.Span, ExplicitTermination = kill.ExplicitTermination }); nextBlock = null; break;
                    case ControlFlowTerminator.TaskDispatch dispatch:
                        if (continuing) throw Invalid("task dispatch from continuing region");
                        body.Statements.Add(new Statement.TaskDispatch(Use(dispatch.Dimensions), dispatch.Payload) { Span = dispatch.Span }); nextBlock = null; break;
                }
            }
            return body;
        }
        output.Body.Statements.AddRange(Sequence(function.Entry, null).Statements);
        if (visited.Count != blocks.Count) throw Invalid("structure does not cover every reachable block");
        return output;
    }

    private sealed class LoopContext(int header, int? continuing, int? merge)
    {
        public int Header { get; } = header;
        public int? Continuing { get; } = continuing;
        public int? Merge { get; } = merge;
        public Expression? BreakIf { get; set; }
    }

}
