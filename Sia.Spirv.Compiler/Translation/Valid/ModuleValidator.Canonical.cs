using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Valid;

public static partial class ModuleValidator
{
    // Reuse the same expression/memory signature rules for structured and SSA IR.
    // SSA ownership, dominance and symbol identity are checked by ControlFlowVerifier.
    internal static Action<ControlFlowInstruction> CanonicalInstructions(Module module, ControlFlowFunction graph)
        => new Validator(module).Canonical(graph);

    private sealed partial class Validator
    {
        private void CanonicalBody(ControlFlowFunction graph)
        {
            // Declaration/signature/entry checks remain in Run. Executable facts
            // come from the graph, including transitive stage and payload rules.
            DiagnosticFilters(graph.Signature.Body.DiagnosticFilters);
            var checker = new Validator(module, allowNativePointerParameters);
            var instruction = checker.Canonical(graph);
            foreach (var block in graph.Blocks) {
                foreach (var value in block.Instructions) instruction(value);
                if (block.Terminator is ControlFlowTerminator.InvocationKill) checker.Restrict(Fragment);
                if (block.Terminator is ControlFlowTerminator.TaskDispatch dispatch) {
                    checker.Restrict(Task); checker.payloadUses[graph.Signature.Name].Add(dispatch.Payload);
                }
            }
            stages[graph.Signature.Name] &= checker.stages[graph.Signature.Name];
            calls[graph.Signature.Name].UnionWith(checker.calls[graph.Signature.Name]);
            payloadUses[graph.Signature.Name].UnionWith(checker.payloadUses[graph.Signature.Name]);
            arrayLengthOverrides.UnionWith(checker.arrayLengthOverrides);
        }

        public Action<ControlFlowInstruction> Canonical(ControlFlowFunction graph)
        {
            function = graph.Signature;
            foreach (var global in module.Globals)
                globals.Add(global.Name, new(global.Type, global.Space != AddressSpace.Handle,
                    global.Space != AddressSpace.Uniform && (global.Access & StorageAccess.Write) != 0, global.Space, global.Space == AddressSpace.Handle));
            foreach (var constant in module.Constants) globals.Add(constant.Name, new(constant.Type, false, false));
            foreach (var f in module.Functions.Append(function).DistinctBy(f => f.Name)) {
                functions.Add(f.Name, f); calls.Add(f.Name, new(StringComparer.Ordinal)); stages.Add(f.Name, AllStages);
                payloadUses.Add(f.Name, new(StringComparer.Ordinal));
            }
            var values = new Dictionary<string, Variable>(StringComparer.Ordinal); scopes.Push(values);
            var definitions = graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null).ToDictionary(i => i.Result!.Value.Id);
            foreach (var value in graph.Blocks.SelectMany(b => b.Parameters.Concat(b.Instructions.Where(i => i.Result is not null).Select(i => i.Result!.Value)))) Type(value.Type);
            Expression Operand(SsaValue value) {
                if (definitions.TryGetValue(value.Id, out var definition)) {
                    if (definition.Operation is ValueOperation.Literal literal) return new Expression.Literal(literal.Value, value.Type);
                    if (definition.Operation is ValueOperation.Symbol symbol && globals.ContainsKey(symbol.Name)
                        && !graph.Signature.Arguments.Any(a => a.Name == symbol.Name))
                        return new Expression.Reference(symbol.Name, value.Type);
                    if (value.Type is ShaderType.Image or ShaderType.Sampler or ShaderType.BindingArray) {
                        if (definition.Operation is ValueOperation.Access access) return new Expression.Access(Operand(access.Base), Operand(access.Index), value.Type);
                        if (definition.Operation is ValueOperation.Let alias) return Operand(alias.Value);
                    }
                }
                string name = "sia_ssa_" + value.Id;
                values[name] = value.Type is ShaderType.Pointer p ? new(p.Base, true, (p.Access & StorageAccess.Write) != 0, p.Space) : new(value.Type, false, false);
                if (value.Type is ShaderType.Pointer { Space: AddressSpace.Workgroup }) Restrict(WorkgroupStages);
                return new Expression.Reference(name, value.Type);
            }
            void Stage() {
                if (function.Stage is ShaderStage stage)
                    Require((stages[function.Name] & (stage switch { ShaderStage.Vertex => Vertex, ShaderStage.Fragment => Fragment, ShaderStage.Task => Task, ShaderStage.Mesh => Mesh, _ => Compute })) != 0,
                        "Canonical operation is unavailable in its stage.");
            }
            return instruction => {
                DiagnosticFilters(instruction.DiagnosticFilters);
                values.Clear(); var type = instruction.Result?.Type ?? new ShaderType.Void();
                if (instruction.Operation is ValueOperation.Local or ValueOperation.Symbol or ValueOperation.Let) return;
                if (instruction.Operation is ValueOperation.InterfaceLoad or ValueOperation.InterfaceStore) {
                    Type(instruction.Operation is ValueOperation.InterfaceLoad load ? load.Field.Type : ((ValueOperation.InterfaceStore)instruction.Operation).Field.Type);
                    return; // Direction, exact type and def/use are checked by the CFG verifier.
                }
                if (instruction.Operation is ValueOperation.Demote) {
                    Restrict(Fragment); Stage(); return;
                }
                if (instruction.Operation is ValueOperation.Store store) {
                    var body = new Block(); body.Statements.Add(new Statement.Store(Operand(store.Pointer), Operand(store.Value)) { MemoryAccess = store.MemoryAccess, Span = instruction.Span });
                    Body(body, scope: false); Stage(); return;
                }
                if (instruction.Operation is ValueOperation.Barrier barrier) {
                    Statement statement = barrier.Control
                        ? new Statement.Barrier(barrier.Storage, barrier.Workgroup, barrier.Texture, barrier.Subgroup) { NativeMemory = barrier.NativeMemory }
                        : new Statement.MemoryBarrier(barrier.Storage, barrier.Workgroup, barrier.Texture, barrier.Subgroup) { NativeMemory = barrier.NativeMemory };
                    var body = new Block(); body.Statements.Add(statement with { Span = instruction.Span });
                    Body(body, scope: false); Stage(); return;
                }
                if (instruction.Operation is ValueOperation.MeshStore or ValueOperation.MeshSetOutputs) {
                    Statement statement = instruction.Operation switch {
                        ValueOperation.MeshStore meshStore => new Statement.MeshStore(meshStore.Field, Operand(meshStore.Index), Operand(meshStore.Value)),
                        ValueOperation.MeshSetOutputs counts => new Statement.MeshSetOutputs(Operand(counts.Vertices), Operand(counts.Primitives)),
                        _ => throw Error("Invalid mesh effect.", instruction.Span)
                    };
                    var body = new Block(); body.Statements.Add(statement with { Span = instruction.Span });
                    Body(body, scope: false); Stage(); return;
                }
                Expression expression = instruction.Operation switch {
                    ValueOperation.Literal l => new Expression.Literal(l.Value, type),
                    ValueOperation.HelperInvocation => new Expression.HelperInvocation(),
                    ValueOperation.Unary u => new Expression.Unary(u.Operator, Operand(u.Operand), type),
                    ValueOperation.Binary b => new Expression.Binary(b.Operator, Operand(b.Left), Operand(b.Right), type),
                    ValueOperation.Convert c => new Expression.Convert(type, Operand(c.Operand), c.Bitcast),
                    ValueOperation.Construct c => new Expression.Construct(type, c.Components.Select(Operand).ToArray()),
                    ValueOperation.Select s => new Expression.Select(Operand(s.Condition), Operand(s.Accept), Operand(s.Reject)),
                    ValueOperation.Access a => new Expression.Access(Operand(a.Base), Operand(a.Index), type),
                    ValueOperation.Member m => new Expression.Member(Operand(m.Base), m.Name, type),
                    ValueOperation.Swizzle s => new Expression.Swizzle(Operand(s.Vector), s.Components, type),
                    ValueOperation.Load l => new Expression.Load(Operand(l.Pointer)) { MemoryAccess = l.MemoryAccess },
                    ValueOperation.Call c => new Expression.Call(c.Function, c.Arguments.Select(Operand).ToArray(), c.ReturnType, CallBinding.Function) { AtomicMemory = c.AtomicMemory,MemoryAccess = c.MemoryAccess },
                    ValueOperation.Builtin b => new Expression.Call(b.Function, b.Arguments.Select(Operand).ToArray(), b.ReturnType, CallBinding.Builtin) { AtomicMemory = b.AtomicMemory,MemoryAccess = b.MemoryAccess },
                    _ => throw Error("Unsupported canonical instruction.", instruction.Span)
                };
                Same(Expr(expression with { Span = instruction.Span }), type, "Canonical result type mismatch.", instruction.Span);
                Stage();
            };
        }
    }
}
