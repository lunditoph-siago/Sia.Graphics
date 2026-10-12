using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;

namespace Sia.Spirv.Compiler.Translation.Legalization;

internal sealed partial class SpirvUniformAccessLowering
{
    private sealed record GraphStep(string? Member, SsaValue? Index, uint? Constant);
    private sealed record GraphLocation(GlobalVariable Root, IReadOnlyList<GraphStep> Steps);

    // Locations describe logical address paths. Caller indices are already SSA
    // values, including aliases captured before a later mutation of their source.
    private void RunGraph(ControlFlowFunction graph, IDictionary<string, ControlFlowFunction> ownedGraphs)
    {
        var definitions = graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null)
            .ToDictionary(i => i.Result!.Value.Id, i => i.Operation);
        var locations = new Dictionary<int, GraphLocation?>();
        var parameters = graph.Signature.Arguments.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        uint? Constant(SsaValue value) {
            var operation = definitions.GetValueOrDefault(value.Id);
            if (operation is ValueOperation.Let let) return Constant(let.Value);
            return operation is ValueOperation.Literal { Value: uint number } ? number
                : operation is ValueOperation.Literal { Value: int signed } && signed >= 0 ? (uint)signed : null;
        }
        GraphLocation? Place(SsaValue value) {
            if (value.Type is not ShaderType.Pointer) return null;
            if (locations.TryGetValue(value.Id, out var known)) return known;
            var operation = definitions.GetValueOrDefault(value.Id);
            var result = operation switch {
                ValueOperation.Symbol symbol when !parameters.Contains(symbol.Name) && converted.TryGetValue(symbol.Name, out var global) => new GraphLocation(global, []),
                ValueOperation.Let let => Place(let.Value),
                ValueOperation.Unary { Operator: "&" or "*" } unary => Place(unary.Operand),
                ValueOperation.Member member when Place(member.Base) is { } parent
                    => parent with { Steps = parent.Steps.Append(new GraphStep(member.Name, null, null)).ToArray() },
                ValueOperation.Access access when Place(access.Base) is { } parent
                    => parent with { Steps = parent.Steps.Append(new GraphStep(null, access.Index, Constant(access.Index))).ToArray() },
                _ => null
            };
            locations.Add(value.Id, result); return result;
        }
        foreach (var block in graph.Blocks) {
            for (int i = 0; i < block.Instructions.Count; i++) {
                var instruction = block.Instructions[i];
                if (instruction.Operation is ValueOperation.Load load && Place(load.Pointer) is { } location && instruction.Result is { } result) {
                    var call = GraphRead(location, result.Type, load.MemoryAccess, instruction, ownedGraphs);
                    block.Instructions[i] = instruction with { Operation = call }; continue;
                }
                if (instruction.Result is { } address && Place(address) is not null) continue;
                if (instruction.Operation.Operands.Any(v => Place(v) is not null))
                    throw Error("Converted uniform pointer requires a direct dereference or alias.", instruction.Span);
            }
            if (block.Terminator!.Operands.Concat(block.Terminator.Edges.SelectMany(e => e.Arguments)).Any(v => Place(v) is not null))
                throw Error("Converted uniform pointer requires a direct dereference or alias.");
        }
        // Only consumed logical address definitions disappear. Loads, calls and
        // captured data indices remain at their original ordered positions.
        foreach (var block in graph.Blocks)
            block.Instructions.RemoveAll(i => i.Result is { } value && Place(value) is not null);
    }

    private ValueOperation.Call GraphRead(GraphLocation location, ShaderType result, SpirvMemoryAccess? memory,
        ControlFlowInstruction origin, IDictionary<string, ControlFlowFunction> ownedGraphs)
    {
        var signature = new ShaderFunction(Name("sia_spv_uniform_read_")) { ReturnType = result };
        var helper = new ControlFlowFunction(signature); var entry = helper.Block(); helper.Entry = entry.Id;
        var arguments = new List<SsaValue>(); var steps = new List<GraphStep>();
        SsaValue Emit(ControlFlowBlock block, ShaderType type, ValueOperation operation) {
            var value = helper.Value(type); block.Instructions.Add(origin with { Result = value, Operation = operation }); return value;
        }
        foreach (var step in location.Steps) {
            if (step.Index is not { } index) { steps.Add(step); continue; }
            SsaValue captured;
            if (step.Constant is { } constant)
                captured = Emit(entry, index.Type, new ValueOperation.Literal(index.Type == ShaderType.I32 ? (object)(int)constant : constant));
            else {
                string name = Name("sia_spv_uniform_arg_"); signature.Arguments.Add(new(name, index.Type)); arguments.Add(index);
                captured = Emit(entry, index.Type, new ValueOperation.Symbol(name));
            }
            steps.Add(step with { Index = captured });
        }
        ShaderType physical = layout.Globals[location.Root.Name].PhysicalType;
        var root = Emit(entry, Pointer(physical), new ValueOperation.Symbol(location.Root.Name));
        SsaValue Field(ControlFlowBlock block, SsaValue pointer, StructMember member)
            => Emit(block, Pointer(member.Type), new ValueOperation.Member(pointer, member.Name));
        SsaValue Child(ControlFlowBlock block, SsaValue pointer, ShaderType type, SsaValue index)
            => Emit(block, Pointer(type), new ValueOperation.Access(pointer, index));
        SsaValue FixedChild(ControlFlowBlock block, SsaValue pointer, ShaderType type, int index)
            => Child(block, pointer, type, Emit(block, ShaderType.U32, new ValueOperation.Literal((uint)index)));
        SsaValue Load(ControlFlowBlock block, SsaValue pointer, ShaderType logical, ShaderType mapped,
            SpirvMemoryAccess? access, MemoryDecorations inherited) {
            if (logical == mapped) return Emit(block, logical, new ValueOperation.Load(pointer,
                ShaderMemoryRequirements.Decorate(access, inherited | ShaderMemoryRequirements.TypeMemory(logical), AddressSpace.Uniform, VulkanMemory)));
            access = access?.Leaf();
            if (logical is ShaderType.Matrix matrix && mapped is ShaderType.Structure columns)
                return Emit(block, logical, new ValueOperation.Construct(columns.Members.Select(m => Load(block, Field(block, pointer, m), m.Type, m.Type, access, inherited)).ToArray()));
            if (logical is ShaderType.Array array && mapped is ShaderType.Array mappedArray && array.Length is uint length)
                return Emit(block, logical, new ValueOperation.Construct(Enumerable.Range(0, checked((int)length))
                    .Select(i => Load(block, FixedChild(block, pointer, mappedArray.Element, i), array.Element, mappedArray.Element, access, inherited)).ToArray()));
            if (logical is ShaderType.Structure structure && mapped is ShaderType.Structure mappedStructure) {
                var values = new List<SsaValue>(); int field = 0;
                foreach (var member in structure.Members) {
                    if (member.Type is ShaderType.Matrix m && Flatten(m)) {
                        var columnValues = new List<SsaValue>();
                        for (int c = 0; c < m.Columns; c++) {
                            var selected = mappedStructure.Members[field++];
                            columnValues.Add(Load(block, Field(block, pointer, selected), selected.Type, selected.Type, access, inherited | member.MemoryDecorations));
                        }
                        values.Add(Emit(block, m, new ValueOperation.Construct(columnValues)));
                    }
                    else {
                        var selected = mappedStructure.Members[field++];
                        values.Add(Load(block, Field(block, pointer, selected), member.Type, selected.Type, access, inherited | member.MemoryDecorations));
                    }
                }
                return Emit(block, logical, new ValueOperation.Construct(values));
            }
            throw Error("Uniform layout conversion has incompatible types.", origin.Span);
        }
        (SsaValue Value, ControlFlowBlock Exit) Select(ControlFlowBlock block, GraphStep step, int count,
            Func<ControlFlowBlock, int, (SsaValue Value, ControlFlowBlock Exit)> read) {
            if (step.Constant is { } fixedIndex && fixedIndex < count) return read(block, (int)fixedIndex);
            var selector = step.Index!.Value; var merge = helper.Block(); var joined = helper.Value(result); merge.Parameters.Add(joined);
            var cases = new List<ControlFlowCase>();
            for (int c = 0; c < count; c++) {
                var arm = helper.Block(); var value = read(arm, c);
                value.Exit.Terminator = new ControlFlowTerminator.Branch(new(merge.Id, [value.Value]));
                cases.Add(new([new Expression.Literal(selector.Type == ShaderType.I32 ? (object)c : (uint)c, selector.Type)], new(arm.Id)));
            }
            var fallback = helper.Block(); var zero = Emit(fallback, result, new ValueOperation.Construct([]));
            fallback.Terminator = new ControlFlowTerminator.Branch(new(merge.Id, [zero]));
            block.Terminator = new ControlFlowTerminator.Switch(selector, cases, new(fallback.Id)); helper.SelectionMerges.Add(block.Id, merge.Id);
            return (joined, merge);
        }
        (SsaValue Value, ControlFlowBlock Exit) Path(ControlFlowBlock block, SsaValue pointer, ShaderType logical, ShaderType mapped,
            int depth, SpirvMemoryAccess? access, MemoryDecorations inherited) {
            if (depth == steps.Count) return (Load(block, pointer, logical, mapped, access, inherited), block);
            var step = steps[depth];
            if (step.Member is { } name && logical is ShaderType.Structure structure && mapped is ShaderType.Structure mappedStructure) {
                int index = structure.Members.ToList().FindIndex(m => m.Name == name), field = layout.UniformFields[structure][index];
                var member = structure.Members[index]; inherited |= member.MemoryDecorations;
                if (member.Type is ShaderType.Matrix matrix && Flatten(matrix)) {
                    if (depth + 1 == steps.Count) {
                        var values = Enumerable.Range(0, matrix.Columns).Select(c => {
                            var column = mappedStructure.Members[field + c];
                            return Load(block, Field(block, pointer, column), column.Type, column.Type, access?.Leaf(), inherited);
                        }).ToArray();
                        return (Emit(block, matrix, new ValueOperation.Construct(values)), block);
                    }
                    return Select(block, steps[depth + 1], matrix.Columns, (arm, c) => {
                        var column = mappedStructure.Members[field + c];
                        return Path(arm, Field(arm, pointer, column), column.Type, column.Type, depth + 2, access, inherited);
                    });
                }
                var selected = mappedStructure.Members[field];
                return Path(block, Field(block, pointer, selected), member.Type, selected.Type, depth + 1, access, inherited);
            }
            if (logical is ShaderType.Matrix matrixType && mapped is ShaderType.Structure columns)
                return Select(block, step, matrixType.Columns, (arm, c) => {
                    var column = columns.Members[c];
                    return Path(arm, Field(arm, pointer, column), column.Type, column.Type, depth + 1, access, inherited);
                });
            (ShaderType element, ShaderType mappedElement) = (logical, mapped) switch {
                (ShaderType.Array a, ShaderType.Array b) => (a.Element, b.Element),
                (ShaderType.BindingArray a, ShaderType.BindingArray b) => (a.Element, b.Element),
                (ShaderType.Matrix a, ShaderType.Matrix b) => (new ShaderType.Vector(a.Rows, a.Component), new ShaderType.Vector(b.Rows, b.Component)),
                (ShaderType.Vector a, ShaderType.Vector b) => (a.Component, b.Component),
                _ => throw Error("Uniform access path has incompatible types.", origin.Span)
            };
            return Path(block, Child(block, pointer, mappedElement, step.Index!.Value), element, mappedElement, depth + 1, access, inherited);
        }
        var read = Path(entry, root, location.Root.Type, physical, 0, memory, location.Root.MemoryDecorations);
        read.Exit.Terminator = new ControlFlowTerminator.Return(read.Value);
        helpers.Add(signature); ownedGraphs.Add(signature.Name, helper);
        return new(signature.Name, arguments, result);
    }
}
