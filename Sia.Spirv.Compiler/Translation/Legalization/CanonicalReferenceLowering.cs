using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

// Reference joins carry scalar identity tags and captured projection indices.
// Memory/resource operations execute in the chosen arm with their original
// effects and already evaluated SSA arguments. Input graphs are borrowed.
internal static partial class CanonicalReferenceLowering
{
    private sealed record Part(string? Member, ShaderType? IndexType);
    private sealed record Shape(SsaValue Root, Part[] Parts, uint Tag, string Key);
    private sealed record Choice(Shape Shape, SsaValue Condition, SsaValue?[] Indices);
    private sealed record Encoded(SsaValue Tag, Dictionary<(string Shape, int Part), SsaValue> Indices);

    internal static CanonicalModule Run(CanonicalModule input, bool lowerPointers = true)
    {
        input = LocalizePrivateSlots(input);
        var result = input with { Functions = input.Functions.ToDictionary(p => p.Key, p => Run(p.Value, lowerPointers), StringComparer.Ordinal) };
        ModuleValidator.Validate(result, native: true); return result;
    }

    internal static ControlFlowFunction Run(ControlFlowFunction input, bool lowerPointers = true)
    {
        bool memory = input.Blocks.SelectMany(b => b.Instructions).Any(i => i.Operation is ValueOperation.Local
            && i.Result?.Type is ShaderType.Pointer { Base: ShaderType.Pointer });
        lowerPointers |= memory;
        bool Reference(ShaderType type) => CanonicalTypes.Resource(type) || lowerPointers && type is ShaderType.Pointer;
        if (!memory && !input.Blocks.Any(b => b.Parameters.Any(p => Reference(p.Type)))) return input;
        var graph = input.Copy();
        ShaderException Error(string message) => new(DiagnosticStage.Validation, "Canonical reference target in " + input.Signature.Name + ": " + message);
        var definitions = graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null).ToDictionary(i => i.Result!.Value.Id);
        var values = graph.Blocks.SelectMany(b => b.Parameters.Concat(b.Instructions.Where(i => i.Result is not null).Select(i => i.Result!.Value)))
            .Where(v => Reference(v.Type)).ToArray();
        var origins = values.ToDictionary(v => v.Id, _ => new Dictionary<string, Shape>(StringComparer.Ordinal));
        var shapes = new Dictionary<string, Shape>(StringComparer.Ordinal);
        var originalParameters = graph.Blocks.ToDictionary(b => b.Id, b => b.Parameters.ToArray());
        var dynamic = graph.Blocks.SelectMany(b => b.Parameters).Where(p => Reference(p.Type)).Select(p => p.Id).ToHashSet();
        var slots = definitions.Values.Where(i => i.Operation is ValueOperation.Local && i.Result?.Type is ShaderType.Pointer { Base: ShaderType.Pointer })
            .ToDictionary(i => i.Result!.Value.Id, _ => new Dictionary<string, Shape>(StringComparer.Ordinal));
        foreach (int slot in slots.Keys)
            if (!LocalValuePromotion.IsDefinitelyAssigned(graph, definitions[slot].Result!.Value))
                throw Error("pointer slot has no initialized finite provenance on every incoming path");
        int? Slot(SsaValue value) {
            if (slots.ContainsKey(value.Id)) return value.Id;
            return definitions.GetValueOrDefault(value.Id)?.Operation is ValueOperation.Let alias ? Slot(alias.Value) : null;
        }
        foreach (var instruction in definitions.Values)
            if (instruction is { Operation: ValueOperation.Load load, Result: { } result } && Slot(load.Pointer) is not null) dynamic.Add(result.Id);
        Shape ShapeFor(SsaValue root, Part[] parts) {
            string identity = definitions[root.Id].Operation is ValueOperation.Symbol symbol ? symbol.Name + ":" + root.Type : "local:" + root.Id;
            string key = identity + ":" + string.Join('/', parts.Select(p => p.Member is { } member ? "m:" + member : "i:" + p.IndexType));
            if (!shapes.TryGetValue(key, out var shape)) {
                shape = new(root, parts, checked((uint)shapes.Count + 1), key); shapes.Add(key, shape);
            }
            return shape;
        }
        bool changed;
        do {
            changed = false;
            foreach (var instruction in definitions.Values) {
                if (instruction.Result is not { } result || !origins.ContainsKey(result.Id)) continue;
                IEnumerable<Shape> incoming = instruction.Operation switch {
                    ValueOperation.Symbol or ValueOperation.Local => [ShapeFor(result, [])],
                    ValueOperation.Let let => origins[let.Value.Id].Values.ToArray(),
                    ValueOperation.Access access => origins[access.Base.Id].Values.Select(s => ShapeFor(s.Root, [.. s.Parts, new(null, access.Index.Type)])).ToArray(),
                    ValueOperation.Member member => origins[member.Base.Id].Values.Select(s => ShapeFor(s.Root, [.. s.Parts, new(member.Name, null)])).ToArray(),
                    ValueOperation.Load load when Slot(load.Pointer) is int slot => slots[slot].Values.ToArray(),
                    _ => []
                };
                if (instruction.Operation is ValueOperation.Let or ValueOperation.Access or ValueOperation.Member
                    && dynamic.Contains(instruction.Operation.Operands.First().Id)) changed |= dynamic.Add(result.Id);
                foreach (var shape in incoming) {
                    if (shape.Parts.Length > definitions.Count) throw Error("recursive reference projection");
                    changed |= origins[result.Id].TryAdd(shape.Key, shape);
                }
            }
            foreach (var store in graph.Blocks.SelectMany(b => b.Instructions).Select(i => i.Operation).OfType<ValueOperation.Store>())
                if (Slot(store.Pointer) is int slot)
                    foreach (var shape in origins[store.Value.Id].Values.ToArray()) changed |= slots[slot].TryAdd(shape.Key, shape);
            foreach (var edge in graph.Blocks.SelectMany(b => b.Terminator!.Edges))
                for (int i = 0; i < edge.Arguments.Count; i++) {
                    var parameter = originalParameters[edge.Target][i];
                    if (!origins.ContainsKey(parameter.Id)) continue;
                    foreach (var shape in origins[edge.Arguments[i].Id].Values.ToArray()) changed |= origins[parameter.Id].TryAdd(shape.Key, shape);
                }
        } while (changed);

        // Function storage may be named by a reference join outside its original
        // branch. Hoist only allocation, retaining every reset at its source site.
        var roots = originalParameters.Values.SelectMany(p => p).Where(p => Reference(p.Type))
            .SelectMany(p => origins[p.Id].Values).Concat(slots.Values.SelectMany(s => s.Values)).Select(s => s.Root.Id).ToHashSet();
        var entry = graph.Blocks.Single(b => b.Id == graph.Entry);
        foreach (var block in graph.Blocks.Where(b => b != entry)) {
            for (int i = 0; i < block.Instructions.Count; i++) {
                var instruction = block.Instructions[i];
                if (instruction.Result is not { } root || !roots.Contains(root.Id) || instruction.Operation is not ValueOperation.Local local) continue;
                if (((ShaderType.Pointer)root.Type).Base is ShaderType.RayQuery) continue;
                entry.Instructions.Insert(0, instruction with { Operation = local with { ZeroInitialize = false } });
                block.Instructions.RemoveAt(i--);
                if (local.ZeroInitialize) {
                    var zero = graph.Value(((ShaderType.Pointer)root.Type).Base);
                    block.Instructions.Insert(++i, new(zero, new ValueOperation.Construct([]), instruction.Span) { DiagnosticFilters = instruction.DiagnosticFilters });
                    block.Instructions.Insert(++i, new(null, new ValueOperation.Store(root, zero), instruction.Span) { DiagnosticFilters = instruction.DiagnosticFilters });
                }
            }
        }
        var encoded = new Dictionary<int, Encoded>();
        foreach (var block in graph.Blocks) {
            block.Parameters.Clear();
            foreach (var parameter in originalParameters[block.Id]) {
                if (!Reference(parameter.Type)) { block.Parameters.Add(parameter); continue; }
                var candidates = origins[parameter.Id];
                if (candidates.Count == 0) throw Error("reference block parameter has no finite origin");
                var tag = graph.Value(ShaderType.U32); block.Parameters.Add(tag);
                var indices = new Dictionary<(string, int), SsaValue>();
                foreach (var shape in candidates.Values.OrderBy(s => s.Tag))
                    for (int i = 0; i < shape.Parts.Length; i++)
                        if (shape.Parts[i].IndexType is { } type) {
                            var value = graph.Value(type); block.Parameters.Add(value); indices.Add((shape.Key, i), value);
                        }
                encoded.Add(parameter.Id, new(tag, indices));
            }
        }
        SsaValue Emit(ControlFlowBlock block, ShaderType type, ValueOperation operation) {
            var value = graph.Value(type); block.Instructions.Add(new(value, operation)); return value;
        }
        SsaValue Literal(ControlFlowBlock block, uint value) => Emit(block, ShaderType.U32, new ValueOperation.Literal(value));
        Choice[] Choices(SsaValue value, ControlFlowBlock block) {
            if (encoded.TryGetValue(value.Id, out var parameter))
                return origins[value.Id].Values.OrderBy(s => s.Tag).Select(s => new Choice(s,
                    Emit(block, ShaderType.Bool, new ValueOperation.Binary("==", parameter.Tag, Literal(block, s.Tag))),
                    s.Parts.Select((part, i) => part.IndexType is null ? (SsaValue?)null : parameter.Indices[(s.Key, i)]).ToArray())).ToArray();
            var instruction = definitions[value.Id];
            Choice[] Project(SsaValue parent, Part part, SsaValue? index) => Choices(parent, block).Select(c => new Choice(
                origins[value.Id].Values.Single(s => s.Root == c.Shape.Root && s.Parts.SequenceEqual(c.Shape.Parts.Append(part))),
                c.Condition, [.. c.Indices, index])).ToArray();
            return instruction.Operation switch {
                ValueOperation.Symbol or ValueOperation.Local => [new(origins[value.Id].Values.Single(), Emit(block, ShaderType.Bool, new ValueOperation.Literal(true)), [])],
                ValueOperation.Let let => Choices(let.Value, block),
                ValueOperation.Access access => Project(access.Base, new(null, access.Index.Type), access.Index),
                ValueOperation.Member member => Project(member.Base, new(member.Name, null), null),
                _ => throw Error("unsupported reference producer " + instruction.Operation.GetType().Name)
            };
        }
        SsaValue Select(ControlFlowBlock block, Choice[] choices, Func<Choice, SsaValue> value) {
            var selected = value(choices[^1]);
            foreach (var choice in choices.Reverse().Skip(1)) {
                var accept = value(choice); selected = Emit(block, selected.Type, new ValueOperation.Select(choice.Condition, accept, selected));
            }
            return selected;
        }
        // Retain qualified slot memory. Tags and every captured index are loaded
        // at the original load, so later slot writes cannot change an old address.
        Encoded Values(IEnumerable<Shape> candidates, Func<ShaderType, SsaValue> allocate) {
            var indices = new Dictionary<(string, int), SsaValue>();
            foreach (var shape in candidates.OrderBy(s => s.Tag))
                for (int i = 0; i < shape.Parts.Length; i++)
                    if (shape.Parts[i].IndexType is { } type) indices.Add((shape.Key, i), allocate(type));
            return new(allocate(ShaderType.U32), indices);
        }
        var locations = slots.ToDictionary(p => p.Key, p => Values(p.Value.Values,
            type => graph.Value(new ShaderType.Pointer(type, AddressSpace.Function))));
        foreach (var instruction in definitions.Values)
            if (instruction is { Operation: ValueOperation.Load load, Result: { } result } && Slot(load.Pointer) is int slot) {
                if (slots[slot].Count == 0) throw Error("pointer slot has no initialized finite provenance");
                encoded.Add(result.Id, Values(slots[slot].Values, graph.Value));
            }
        foreach (var block in graph.Blocks) {
            var original = block.Instructions.ToArray(); block.Instructions.Clear();
            foreach (var instruction in original) {
                void Add(SsaValue? result, ValueOperation operation) => block.Instructions.Add(instruction with { Result = result, Operation = operation });
                if (instruction is { Operation: ValueOperation.Local local, Result: { } root } && locations.TryGetValue(root.Id, out var storage)) {
                    if (local.ZeroInitialize) throw Error("pointer slot initialization requires an explicit stored address");
                    Add(storage.Tag, new ValueOperation.Local(local.Name, false));
                    foreach (var (key, place) in storage.Indices) Add(place, new ValueOperation.Local(local.Name + "_index_" + key.Item2, false));
                }
                else if (instruction.Operation is ValueOperation.Store store && Slot(store.Pointer) is int storeSlot) {
                    var choices = Choices(store.Value, block); var destination = locations[storeSlot];
                    var tag = Select(block, choices, c => Literal(block, c.Shape.Tag));
                    var indices = destination.Indices.Select(p => (Place: p.Value, Snapshot: Select(block, choices, c => c.Shape.Key == p.Key.Shape
                        ? c.Indices[p.Key.Part]!.Value : Emit(block, ((ShaderType.Pointer)p.Value.Type).Base, new ValueOperation.Construct([]))))).ToArray();
                    Add(null, new ValueOperation.Store(destination.Tag, tag, store.MemoryAccess));
                    foreach (var index in indices) Add(null, new ValueOperation.Store(index.Place, index.Snapshot, store.MemoryAccess));
                }
                else if (instruction is { Operation: ValueOperation.Load load, Result: { } result } && Slot(load.Pointer) is int loadSlot) {
                    var snapshot = encoded[result.Id]; var source = locations[loadSlot];
                    Add(snapshot.Tag, new ValueOperation.Load(source.Tag, load.MemoryAccess));
                    foreach (var (key, value) in snapshot.Indices) Add(value, new ValueOperation.Load(source.Indices[key], load.MemoryAccess));
                }
                else if (instruction.Result is { } alias && Slot(alias) is not null) {
                    if (instruction.Operation is not ValueOperation.Let) throw Error("unsupported pointer-slot alias");
                }
                else block.Instructions.Add(instruction);
            }
        }
        foreach (var block in graph.Blocks.ToArray()) foreach (var edge in block.Terminator!.Edges) {
            var arguments = edge.Arguments.ToArray(); edge.Arguments.Clear();
            for (int i = 0; i < arguments.Length; i++) {
                var parameter = originalParameters[edge.Target][i];
                if (!encoded.ContainsKey(parameter.Id)) { edge.Arguments.Add(arguments[i]); continue; }
                var choices = Choices(arguments[i], block);
                edge.Arguments.Add(Select(block, choices, c => Literal(block, c.Shape.Tag)));
                foreach (var shape in origins[parameter.Id].Values.OrderBy(s => s.Tag))
                    for (int part = 0; part < shape.Parts.Length; part++) {
                        if (shape.Parts[part].IndexType is not { } type) continue;
                        int index = part;
                        edge.Arguments.Add(Select(block, choices, c => c.Shape.Key == shape.Key ? c.Indices[index]!.Value
                            : Emit(block, type, new ValueOperation.Construct([]))));
                    }
            }
        }
        SsaValue ReferenceValue(ControlFlowBlock block, Choice choice) {
            var root = choice.Shape.Root;
            var value = definitions[root.Id].Operation is ValueOperation.Symbol symbol ? Emit(block, root.Type, symbol) : root;
            for (int i = 0; i < choice.Shape.Parts.Length; i++) {
                var pointer = value.Type as ShaderType.Pointer; var type = pointer?.Base ?? value.Type;
                if (choice.Shape.Parts[i].Member is { } member) {
                    var field = ((ShaderType.Structure)type).Members.Single(m => m.Name == member).Type;
                    value = Emit(block, pointer is null ? field : pointer with { Base = field }, new ValueOperation.Member(value, member));
                } else {
                    ShaderType element = type switch {
                        ShaderType.Array array => array.Element, ShaderType.BindingArray array => array.Element,
                        ShaderType.Vector vector => vector.Component, ShaderType.Matrix matrix => new ShaderType.Vector(matrix.Rows, matrix.Component),
                        _ => throw Error("invalid reference index projection")
                    };
                    value = Emit(block, pointer is null ? element : pointer with { Base = element }, new ValueOperation.Access(value, choice.Indices[i]!.Value));
                }
            }
            return value;
        }
        List<Dictionary<int, Choice>> Combinations(IEnumerable<SsaValue> references, ControlFlowBlock block) {
            var combinations = new List<Dictionary<int, Choice>> { new() };
            foreach (var value in references.Distinct()) {
                var choices = Choices(value, block);
                combinations = combinations.SelectMany(c => choices.Select(choice => new Dictionary<int, Choice>(c) { [value.Id] = choice })).ToList();
            }
            return combinations;
        }
        void Dispatch(ControlFlowBlock selector, List<Dictionary<int, Choice>> combinations, int? merge, Action<ControlFlowBlock, Dictionary<int, Choice>> emit) {
            for (int i = 0; i < combinations.Count; i++) {
                var combination = combinations[i]; var arm = graph.Block(); emit(arm, combination);
                if (i == combinations.Count - 1) selector.Terminator = new ControlFlowTerminator.Branch(new(arm.Id));
                else {
                    graph.SelectionMerges[selector.Id] = merge;
                    var condition = combination.Values.First().Condition;
                    foreach (var choice in combination.Values.Skip(1)) condition = Emit(selector, ShaderType.Bool, new ValueOperation.Binary("&&", condition, choice.Condition));
                    var next = graph.Block();
                    selector.Terminator = new ControlFlowTerminator.Conditional(condition, new(arm.Id), new(next.Id)); selector = next;
                }
            }
        }
        bool Consumer(ControlFlowInstruction instruction) => instruction.Operation is ValueOperation.Builtin or ValueOperation.Call or ValueOperation.Load or ValueOperation.Store
            && instruction.Operation.Operands.Any(v => dynamic.Contains(v.Id));
        while (graph.Blocks.SelectMany(b => b.Instructions.Select((i, index) => (Block: b, Instruction: i, Index: index)))
            .FirstOrDefault(p => Consumer(p.Instruction)) is { Instruction: not null } site) {
            if (site.Instruction.Result is { } reference && Reference(reference.Type))
                throw Error("reference-valued memory or call result requires prior provenance/helper lowering");
            var tail = site.Block.Instructions.Skip(site.Index + 1).ToArray();
            site.Block.Instructions.RemoveRange(site.Index, site.Block.Instructions.Count - site.Index);
            var combinations = Combinations(site.Instruction.Operation.Operands.Where(v => dynamic.Contains(v.Id)), site.Block);
            ControlFlowInstruction Instruction(ControlFlowBlock block, Dictionary<int, Choice> combination, SsaValue? result) {
                var operands = combination.ToDictionary(p => p.Key, p => ReferenceValue(block, p.Value));
                return site.Instruction with { Result = result, Operation = site.Instruction.Operation.Map(v => operands.GetValueOrDefault(v.Id, v)) };
            }
            if (combinations.Count == 1) {
                site.Block.Instructions.Add(Instruction(site.Block, combinations[0], site.Instruction.Result)); site.Block.Instructions.AddRange(tail); continue;
            }
            if (site.Instruction.Operation is ValueOperation.Call && (site.Instruction.Effects & ShaderEffects.Convergent) != 0)
                throw Error("convergent reference helper must expand before address dispatch");
            var continuation = graph.Block(); continuation.Instructions.AddRange(tail); continuation.Terminator = site.Block.Terminator;
            // The old terminator and its selection move together. A loop header
            // stays the header: address dispatch is a selection inside its body.
            if (graph.SelectionMerges.Remove(site.Block.Id, out var oldMerge)) graph.SelectionMerges[continuation.Id] = oldMerge;
            else if (graph.Loops.ContainsKey(site.Block.Id) && continuation.Terminator is ControlFlowTerminator.Conditional or ControlFlowTerminator.Switch)
                graph.SelectionMerges[continuation.Id] = null;
            if (site.Instruction.Result is { } originalResult) continuation.Parameters.Add(originalResult);
            Dispatch(site.Block, combinations, continuation.Id, (arm, combination) => {
                SsaValue? result = site.Instruction.Result is { } value ? graph.Value(value.Type) : null;
                arm.Instructions.Add(Instruction(arm, combination, result));
                arm.Terminator = new ControlFlowTerminator.Branch(new(continuation.Id, result is { } returned ? [returned] : []));
            });
        }
        foreach (var block in graph.Blocks.ToArray()) {
            if (block.Terminator is not ControlFlowTerminator.Return { Value: { } value } returned || !dynamic.Contains(value.Id)) continue;
            Dispatch(block, Combinations([value], block), null, (arm, combination) => {
                arm.Terminator = returned with { Value = ReferenceValue(arm, combination[value.Id]) };
            });
        }
        foreach (var block in graph.Blocks) {
            // Joined aliases transport identity; their projections are rebuilt
            // only at consumers, with the original SSA indices already captured.
            block.Instructions.RemoveAll(i => i.Result is { } result && dynamic.Contains(result.Id));
            if (block.Instructions.Any(i => i.Operation.Operands.Any(v => dynamic.Contains(v.Id))) || block.Terminator!.Operands.Any(v => dynamic.Contains(v.Id)))
                throw Error("reference operand requires further target legalization");
        }
        return graph;
    }
}
