using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

// Opaque joins carry scalar identity tags and captured descriptor indices.
// Resource operations execute in the chosen arm; their data arguments are SSA
// snapshots evaluated once before dispatch. No mutable resource reaches a writer.
internal static class CanonicalResourceLowering
{
    private sealed record Shape(string Symbol, ShaderType RootType, ShaderType[] IndexTypes, uint Tag, string Key);
    private sealed record Choice(Shape Shape, SsaValue Condition, SsaValue[] Indices);
    private sealed record Encoded(SsaValue Tag, Dictionary<(string Shape, int Index), SsaValue> Indices);

    internal static CanonicalModule Run(CanonicalModule input)
    {
        var functions = input.Functions.ToDictionary(p => p.Key, p => Run(p.Value), StringComparer.Ordinal);
        var result = input with { Functions = functions };
        ModuleValidator.Validate(result, native: true); return result;
    }

    private static ControlFlowFunction Run(ControlFlowFunction input)
    {
        if (!input.Blocks.Any(b => b.Parameters.Any(p => CanonicalTypes.Resource(p.Type)))) return input;
        var graph = input.Copy();
        ShaderException Error(string message) => new(DiagnosticStage.Validation, "Canonical resource target in " + input.Signature.Name + ": " + message);
        if (CanonicalTypes.Resource(graph.Signature.ReturnType)) throw Error("resource-return helpers must be expanded before target dispatch");
        var definitions = graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null).ToDictionary(i => i.Result!.Value.Id);
        var values = graph.Blocks.SelectMany(b => b.Parameters.Concat(b.Instructions.Where(i => i.Result is not null).Select(i => i.Result!.Value)))
            .Where(v => CanonicalTypes.Resource(v.Type)).ToArray();
        var origins = values.ToDictionary(v => v.Id, _ => new Dictionary<string, Shape>(StringComparer.Ordinal));
        var shapes = new Dictionary<string, Shape>(StringComparer.Ordinal);
        var originalParameters = graph.Blocks.ToDictionary(b => b.Id, b => b.Parameters.ToArray());
        Shape ShapeFor(string symbol, ShaderType type, ShaderType[] indices) {
            string key = symbol + ":" + type + ":" + string.Join('/', indices.Select(t => t.ToString()));
            if (!shapes.TryGetValue(key, out var shape)) {
                shape = new(symbol, type, indices, checked((uint)shapes.Count + 1), key); shapes.Add(key, shape);
            }
            return shape;
        }
        bool changed;
        do {
            changed = false;
            foreach (var instruction in definitions.Values) {
                if (instruction.Result is not { } result || !origins.ContainsKey(result.Id)) continue;
                IEnumerable<Shape> incoming = instruction.Operation switch {
                    ValueOperation.Symbol symbol => [ShapeFor(symbol.Name, result.Type, [])],
                    ValueOperation.Let let => origins[let.Value.Id].Values.ToArray(),
                    ValueOperation.Access access => origins[access.Base.Id].Values.Select(s => ShapeFor(s.Symbol, s.RootType, [.. s.IndexTypes, access.Index.Type])).ToArray(),
                    _ => throw Error("unsupported resource producer " + instruction.Operation.GetType().Name)
                };
                foreach (var shape in incoming) {
                    if (shape.IndexTypes.Length > definitions.Count) throw Error("recursive descriptor projection");
                    changed |= origins[result.Id].TryAdd(shape.Key, shape);
                }
            }
            foreach (var edge in graph.Blocks.SelectMany(b => b.Terminator!.Edges))
                for (int i = 0; i < edge.Arguments.Count; i++) {
                    var parameter = originalParameters[edge.Target][i];
                    if (!origins.ContainsKey(parameter.Id)) continue;
                    foreach (var shape in origins[edge.Arguments[i].Id].Values.ToArray()) changed |= origins[parameter.Id].TryAdd(shape.Key, shape);
                }
        } while (changed);
        var encoded = new Dictionary<int, Encoded>();
        foreach (var block in graph.Blocks) {
            block.Parameters.Clear();
            foreach (var parameter in originalParameters[block.Id]) {
                if (!origins.TryGetValue(parameter.Id, out var candidates)) { block.Parameters.Add(parameter); continue; }
                if (candidates.Count == 0) throw Error("resource block parameter has no finite origin");
                var tag = graph.Value(ShaderType.U32); block.Parameters.Add(tag);
                var indices = new Dictionary<(string, int), SsaValue>();
                foreach (var shape in candidates.Values.OrderBy(s => s.Tag))
                    for (int i = 0; i < shape.IndexTypes.Length; i++) {
                        var value = graph.Value(shape.IndexTypes[i]); block.Parameters.Add(value); indices.Add((shape.Key, i), value);
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
                    s.IndexTypes.Select((_, i) => parameter.Indices[(s.Key, i)]).ToArray())).ToArray();
            var instruction = definitions[value.Id];
            return instruction.Operation switch {
                ValueOperation.Symbol => [new(origins[value.Id].Values.Single(), Emit(block, ShaderType.Bool, new ValueOperation.Literal(true)), [])],
                ValueOperation.Let let => Choices(let.Value, block),
                ValueOperation.Access access => Choices(access.Base, block).Select(c => new Choice(
                    origins[value.Id].Values.Single(s => s.Symbol == c.Shape.Symbol && s.RootType == c.Shape.RootType
                        && s.IndexTypes.SequenceEqual(c.Shape.IndexTypes.Append(access.Index.Type))), c.Condition, [.. c.Indices, access.Index])).ToArray(),
                _ => throw Error("unsupported resource identity")
            };
        }
        SsaValue Select(ControlFlowBlock block, Choice[] choices, Func<Choice, SsaValue> value) {
            var selected = value(choices[^1]);
            foreach (var choice in choices.Reverse().Skip(1)) {
                var accept = value(choice); selected = Emit(block, selected.Type, new ValueOperation.Select(choice.Condition, accept, selected));
            }
            return selected;
        }
        // All source arguments are captured before any destination parameter changes.
        foreach (var block in graph.Blocks.ToArray()) foreach (var edge in block.Terminator!.Edges) {
            var arguments = edge.Arguments.ToArray(); edge.Arguments.Clear();
            for (int i = 0; i < arguments.Length; i++) {
                var parameter = originalParameters[edge.Target][i];
                if (!encoded.ContainsKey(parameter.Id)) { edge.Arguments.Add(arguments[i]); continue; }
                var choices = Choices(arguments[i], block);
                edge.Arguments.Add(Select(block, choices, c => Literal(block, c.Shape.Tag)));
                foreach (var shape in origins[parameter.Id].Values.OrderBy(s => s.Tag))
                    for (int index = 0; index < shape.IndexTypes.Length; index++) {
                        int part = index;
                        edge.Arguments.Add(Select(block, choices, c => c.Shape.Key == shape.Key ? c.Indices[part]
                            : Emit(block, shape.IndexTypes[part], new ValueOperation.Construct([]))));
                    }
            }
        }
        SsaValue Resource(ControlFlowBlock block, Choice choice) {
            var value = Emit(block, choice.Shape.RootType, new ValueOperation.Symbol(choice.Shape.Symbol));
            foreach (var index in choice.Indices) {
                var type = value.Type is ShaderType.BindingArray array ? array.Element : throw Error("descriptor projection needs a binding array");
                value = Emit(block, type, new ValueOperation.Access(value, index));
            }
            return value;
        }
        while (graph.Blocks.SelectMany(b => b.Instructions.Select((i, index) => (Block: b, Instruction: i, Index: index)))
            .FirstOrDefault(p => p.Instruction.Operation is ValueOperation.Builtin or ValueOperation.Call
                && p.Instruction.Operation.Operands.Any(v => origins.ContainsKey(v.Id))) is { Instruction: not null } site) {
            var resources = site.Instruction.Operation.Operands.Where(v => origins.ContainsKey(v.Id)).Distinct().ToArray();
            var tail = site.Block.Instructions.Skip(site.Index + 1).ToArray();
            site.Block.Instructions.RemoveRange(site.Index, site.Block.Instructions.Count - site.Index);
            var options = resources.ToDictionary(v => v.Id, v => Choices(v, site.Block));
            var combinations = new List<Dictionary<int, Choice>> { new() };
            foreach (var value in resources) combinations = combinations.SelectMany(c => options[value.Id].Select(choice => {
                var result = new Dictionary<int, Choice>(c) { [value.Id] = choice }; return result;
            })).ToList();
            ControlFlowInstruction Instruction(ControlFlowBlock block, Dictionary<int, Choice> combination, SsaValue? result) {
                var operands = combination.ToDictionary(p => p.Key, p => Resource(block, p.Value));
                return site.Instruction with { Result = result, Operation = site.Instruction.Operation.Map(v => operands.GetValueOrDefault(v.Id, v)) };
            }
            if (combinations.Count == 1) {
                site.Block.Instructions.Add(Instruction(site.Block, combinations[0], site.Instruction.Result));
                site.Block.Instructions.AddRange(tail); continue;
            }
            var continuation = graph.Block(); continuation.Instructions.AddRange(tail); continuation.Terminator = site.Block.Terminator;
            if (site.Instruction.Result is { } originalResult) continuation.Parameters.Add(originalResult);
            var selector = site.Block;
            for (int i = 0; i < combinations.Count; i++) {
                var combination = combinations[i]; var arm = graph.Block();
                SsaValue? result = site.Instruction.Result is { } value ? graph.Value(value.Type) : null;
                arm.Instructions.Add(Instruction(arm, combination, result));
                arm.Terminator = new ControlFlowTerminator.Branch(new(continuation.Id, result is { } returned ? [returned] : []));
                if (i == combinations.Count - 1) selector.Terminator = new ControlFlowTerminator.Branch(new(arm.Id));
                else {
                    var condition = combination.Values.First().Condition;
                    foreach (var choice in combination.Values.Skip(1)) condition = Emit(selector, ShaderType.Bool, new ValueOperation.Binary("&&", condition, choice.Condition));
                    var next = graph.Block();
                    selector.Terminator = new ControlFlowTerminator.Conditional(condition, new(arm.Id), new(next.Id)); selector = next;
                }
            }
        }
        foreach (var block in graph.Blocks) {
            block.Instructions.RemoveAll(i => i.Result is { } result && origins.ContainsKey(result.Id));
            if (block.Terminator!.Operands.Any(v => origins.ContainsKey(v.Id))) throw Error("resource control operand requires further target legalization");
        }
        // Dispatch introduces selections and splits old headers. Rebuild their
        // target regions from the resulting graph rather than borrowing Body facts.
        return CanonicalControlFlowRegions.Run(graph);
    }
}
