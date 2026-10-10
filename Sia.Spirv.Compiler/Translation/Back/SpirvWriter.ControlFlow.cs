using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class SpirvWriter
{
    private sealed partial class Writer
    {
        private sealed partial class FunctionEmitter
        {
            public void EmitCanonical(SpirvFunctionControlFlow prepared)
            {
                var graph = prepared.Graph;
                var labels = prepared.BlockOrder.ToDictionary(id => id, _ => owner.Id());
                var values = new Dictionary<int, uint>();
                var literals = new Dictionary<int, Expression.Literal>();
                var resourcePointers = new Dictionary<int, uint>();
                string Name(SsaValue value) => "$canonical_" + value.Id;
                Expression Ref(SsaValue value) => literals.TryGetValue(value.Id, out var literal) ? literal
                    : new Expression.Reference(Name(value), value.Type);
                Expression Dereference(SsaValue value) => new Expression.Unary("*", Ref(value), value.Type);
                void Bind(SsaValue value, uint id) {
                    values.Add(value.Id, id);
                    scopes.Peek().Add(Name(value), new(id, value.Type, value.Type is ShaderType.BindingArray, value.Type is ShaderType.BindingArray ? 0u : 7u));
                }
                uint Id(SsaValue value) => values[value.Id];
                uint signature = owner.FunctionType(function.ReturnType, function.Arguments.Select(a => a.Type));
                var header = new List<SpirvInstruction> { I(Op.Function, owner.Type(function.ReturnType), functionId, 0, signature) };
                owner.Name(functionId, function.Name); scopes.Push(new(StringComparer.Ordinal));
                foreach (var argument in function.Arguments) {
                    uint id = owner.Id(); header.Add(I(Op.FunctionParameter, owner.Type(argument.Type), id));
                    scopes.Peek().Add(argument.Name, new(id, argument.Type, false)); owner.Name(id, argument.Name);
                }
                foreach (var parameter in graph.Blocks.SelectMany(b => b.Parameters)) Bind(parameter, owner.Id());
                var blocks = graph.Blocks.ToDictionary(b => b.Id);
                var predecessors = ControlFlowAnalysis.Predecessors(graph);
                var phis = new List<(int Index, int Block, int Parameter)>();
                foreach (int blockId in prepared.BlockOrder) {
                    Label(labels[blockId]);
                    if (prepared.StructuralTargets.Contains(blockId)) {
                        if (prepared.StructuralBackedges.TryGetValue(blockId, out int headerBlock)) Add(Op.Branch, labels[headerBlock]);
                        else Add(Op.Unreachable);
                        continue;
                    }
                    var block = blocks[blockId];
                    for (int p = 0; p < block.Parameters.Count; p++) {
                        var parameter = block.Parameters[p];
                        phis.Add((code.Count, blockId, p));
                        Add(Op.Phi, owner.Type(parameter.Type), Id(parameter));
                    }
                    foreach (var instruction in block.Instructions) {
                        var result = instruction.Result;
                        ShaderType type = result?.Type ?? new ShaderType.Void();
                        uint emitted;
                        switch (instruction.Operation) {
                            case ValueOperation.Literal literal:
                                var expression = new Expression.Literal(literal.Value, type);
                                literals.Add(result!.Value.Id, expression); emitted = owner.Constant(expression); break;
                            case ValueOperation.Symbol symbol:
                                var binding = Lookup(symbol.Name);
                                if (binding.Place && type is ShaderType.Image) resourcePointers.Add(result!.Value.Id, binding.Id);
                                emitted = binding.Place ? type is ShaderType.Pointer or ShaderType.BindingArray
                                    ? Place(new Expression.Reference(symbol.Name, binding.Type)) : MemoryLoad(binding.Id, type, null) : binding.Id; break;
                            case ValueOperation.Local local:
                                emitted = Variable(((ShaderType.Pointer)type).Base, local.Name);
                                // The reader represents a requested zero initializer as an
                                // explicit ordered store. Allocation is hoisted, that store is not.
                                break;
                            case ValueOperation.Let let:
                                emitted = Id(let.Value);
                                if (resourcePointers.TryGetValue(let.Value.Id, out uint resourcePointer)) resourcePointers.Add(result!.Value.Id, resourcePointer);
                                break;
                            case ValueOperation.Load load: emitted = MemoryLoad(Id(load.Pointer), type, load.MemoryAccess); break;
                            case ValueOperation.InterfaceLoad load: emitted = MemoryLoad(owner.entryInterfaceVariables[(functionId, load.Field)], type, null); break;
                            case ValueOperation.InterfaceStore store: Add(Op.Store, owner.entryInterfaceVariables[(functionId, store.Field)], Id(store.Value)); continue;
                            case ValueOperation.Store store: MemoryStore(Id(store.Pointer), Id(store.Value), store.MemoryAccess); continue;
                            case ValueOperation.Unary unary: emitted = Value(new Expression.Unary(unary.Operator, Ref(unary.Operand), type)); break;
                            case ValueOperation.Binary binary: emitted = Value(new Expression.Binary(binary.Operator, Ref(binary.Left), Ref(binary.Right), type)); break;
                            case ValueOperation.Convert convert: emitted = Value(new Expression.Convert(type, Ref(convert.Operand), convert.Bitcast)); break;
                            case ValueOperation.Construct construct: emitted = Value(new Expression.Construct(type, construct.Components.Select(Ref).ToArray())); break;
                            case ValueOperation.Select select: emitted = Value(new Expression.Select(Ref(select.Condition), Ref(select.Accept), Ref(select.Reject))); break;
                            case ValueOperation.Access access:
                                if (type is ShaderType.Image && access.Base.Type is ShaderType.BindingArray) {
                                    uint imagePointer = Place(new Expression.Access(Ref(access.Base), Ref(access.Index), type));
                                    resourcePointers.Add(result!.Value.Id, imagePointer);
                                    emitted = MemoryLoad(imagePointer, type, null);
                                    if (owner.nonUniformValues.Contains(imagePointer)) owner.NonUniform(emitted, type);
                                    break;
                                }
                                emitted = type is ShaderType.Pointer
                                    ? Place(new Expression.Access(Dereference(access.Base), Ref(access.Index), type))
                                    : Value(new Expression.Access(Ref(access.Base), Ref(access.Index), type)); break;
                            case ValueOperation.Member member:
                                emitted = type is ShaderType.Pointer
                                    ? Place(new Expression.Member(Dereference(member.Base), member.Name, type))
                                    : Value(new Expression.Member(Ref(member.Base), member.Name, type)); break;
                            case ValueOperation.Swizzle swizzle: emitted = Value(new Expression.Swizzle(Ref(swizzle.Vector), swizzle.Components, type)); break;
                            case ValueOperation.Builtin builtin:
                                if (prepared.RuntimeArrayLengths.TryGetValue(result?.Id ?? -1, out var length)) {
                                    uint structure = length.Structure is { } root ? Id(root) : Lookup(length.Global!).Id;
                                    emitted = Result(Op.ArrayLength, type, structure, length.Member); break;
                                }
                                var arguments = builtin.Arguments.Select(Ref).ToArray();
                                if (builtin.Function.StartsWith("textureAtomic", StringComparison.Ordinal)) {
                                    var image = builtin.Arguments[0]; string name = "$canonical_image_" + image.Id;
                                    if (!scopes.Peek().ContainsKey(name)) scopes.Peek().Add(name, new(resourcePointers[image.Id], image.Type, true, 0));
                                    arguments[0] = new Expression.Reference(name, image.Type);
                                }
                                emitted = Call(new Expression.Call(builtin.Function, arguments, builtin.ReturnType, CallBinding.Builtin)
                                    { AtomicMemory = builtin.AtomicMemory, MemoryAccess = builtin.MemoryAccess }); break;
                            case ValueOperation.Call call:
                                emitted = Call(new Expression.Call(call.Function, call.Arguments.Select(Ref).ToArray(), call.ReturnType, CallBinding.Function)
                                    { AtomicMemory = call.AtomicMemory, MemoryAccess = call.MemoryAccess }); break;
                            case ValueOperation.Barrier barrier:
                                NativeBarrier(barrier.NativeMemory ?? throw owner.Error("Canonical barrier operands were not prepared.", instruction.Span)); continue;
                            case ValueOperation.HelperInvocation: emitted = Value(new Expression.HelperInvocation()); break;
                            case ValueOperation.Demote:
                                owner.capabilities.Add(5379);
                                if (owner.OutputVersion < 0x10600) owner.extensions.Add("SPV_EXT_demote_to_helper_invocation");
                                Add(Op.DemoteToHelperInvocation); continue;
                            case ValueOperation.MeshStore store:
                                uint output = owner.MeshOutput(store.Field); owner.functionGlobalUses[functionId].Add(output);
                                uint pointer = owner.Id();
                                Add(Op.AccessChain, owner.Pointer(owner.Type(store.Field.Type), 3), pointer, output, Id(store.Index));
                                Add(Op.Store, pointer, Id(store.Value)); continue;
                            case ValueOperation.MeshSetOutputs counts: Add(Op.SetMeshOutputsEXT, Id(counts.Vertices), Id(counts.Primitives)); continue;
                            default: throw owner.Error("Unsupported prepared canonical operation.", instruction.Span);
                        }
                        if (result is { } value) Bind(value, emitted);
                    }
                    if (prepared.Loops.TryGetValue(blockId, out var loop)) Add(Op.LoopMerge, labels[loop.Merge], labels[loop.Continuing], 0);
                    else if (prepared.SelectionMerges.TryGetValue(blockId, out int merge)) Add(Op.SelectionMerge, labels[merge], 0);
                    switch (block.Terminator) {
                        case ControlFlowTerminator.Branch branch: Add(Op.Branch, labels[branch.Edge.Target]); break;
                        case ControlFlowTerminator.Conditional branch: Add(Op.BranchConditional, Id(branch.Condition), labels[branch.Accept.Target], labels[branch.Reject.Target]); break;
                        case ControlFlowTerminator.Switch selection:
                            var operands = new List<uint> { Id(selection.Selector), labels[selection.Default.Target] };
                            foreach (var arm in selection.Cases) foreach (var literal in arm.Values) {
                                operands.Add(literal.Value switch { uint u => u, int i => unchecked((uint)i), _ => throw owner.Error("Canonical switch requires a 32-bit selector.") });
                                operands.Add(labels[arm.Edge.Target]);
                            }
                            Add(Op.Switch, operands.ToArray()); break;
                        case ControlFlowTerminator.Return returned:
                            if (returned.Value is { } value) Add(Op.ReturnValue, Id(value)); else Add(Op.Return); break;
                        case ControlFlowTerminator.Unreachable: Add(Op.Unreachable); break;
                        case ControlFlowTerminator.InvocationKill kill:
                            if (kill.ExplicitTermination && owner.OutputVersion < 0x10600) owner.extensions.Add("SPV_KHR_terminate_invocation");
                            Add(kill.ExplicitTermination ? Op.TerminateInvocation : Op.Kill); break;
                        case ControlFlowTerminator.TaskDispatch dispatch:
                            TaskDispatch(new Statement.TaskDispatch(Ref(dispatch.Dimensions), dispatch.Payload) { Span = dispatch.Span }); break;
                        default: throw owner.Error("Prepared CFG has no terminator.");
                    }
                }
                // Backedge definitions can occur after their phi. Resolve incoming
                // IDs after serialization; repeated switch edges share one predecessor.
                foreach (var (index, blockId, parameterIndex) in phis) {
                    var parameter = blocks[blockId].Parameters[parameterIndex];
                    var operands = new List<uint> { owner.Type(parameter.Type), Id(parameter) };
                    foreach (var incoming in predecessors[blockId].DistinctBy(p => p.Block.Id)) {
                        operands.Add(Id(incoming.Edge.Arguments[parameterIndex])); operands.Add(labels[incoming.Block.Id]);
                    }
                    foreach (var incoming in prepared.StructuralBackedges.Where(p => p.Value == blockId)) {
                        operands.Add(owner.Null(parameter.Type)); operands.Add(labels[incoming.Key]);
                    }
                    code[index] = I(Op.Phi, operands.ToArray());
                }
                owner.bodies.AddRange(header);
                owner.bodies.Add(code[0]); owner.bodies.AddRange(variables); owner.bodies.AddRange(code.Skip(1));
                owner.bodies.Add(I(Op.FunctionEnd)); scopes.Pop();
            }
        }
    }
}
