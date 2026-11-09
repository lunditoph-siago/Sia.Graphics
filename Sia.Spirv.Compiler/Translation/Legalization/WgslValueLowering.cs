using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Remove redundant SSA state while retaining WGSL runtime evaluation boundaries.</summary>
internal static class WgslValueLowering
{
    internal static CanonicalModule Run(CanonicalModule input)
    {
        var graphs = input.Functions.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal);
        foreach (var graph in graphs.Values) {
            AggregateReadPromotion.Run(graph, dynamicCompositeIndexing: true);
            LocalValuePromotion.Run(graph);
            Simplify(graph);
            PreserveRuntimeEvaluation(graph, input.Declarations);
        }
        var result = input with { Functions = graphs };
        ModuleValidator.Validate(result, native: true); return result;
    }

    private static void Simplify(ControlFlowFunction graph)
    {
        var replacements = new Dictionary<int, SsaValue>();
        SsaValue Resolve(SsaValue value) {
            while (replacements.TryGetValue(value.Id, out var next)) value = next;
            return value;
        }
        foreach (var instruction in graph.Blocks.SelectMany(b => b.Instructions)) {
            if (instruction.Result is not { } value || !CanonicalTypes.Data(value.Type) || instruction.DiagnosticFilters.Count != 0) continue;
            SsaValue? source = instruction.Operation switch {
                ValueOperation.Let alias => alias.Value,
                ValueOperation.Convert conversion when conversion.Operand.Type == value.Type => conversion.Operand,
                ValueOperation.Construct { Components.Count: 1 } construct when construct.Components[0].Type == value.Type => construct.Components[0],
                _ => null
            };
            if (source is { } original) replacements.Add(value.Id, Resolve(original));
        }
        var predecessors = ControlFlowAnalysis.Predecessors(graph);
        bool changed;
        do {
            changed = false;
            foreach (var block in graph.Blocks)
                for (int p = block.Parameters.Count - 1; p >= 0; p--) {
                    var parameter = block.Parameters[p];
                    if (!CanonicalTypes.Data(parameter.Type)) continue;
                    var incoming = predecessors[block.Id].Select(e => Resolve(e.Edge.Arguments[p]))
                        .Where(v => v.Id != parameter.Id).Distinct().Take(2).ToArray();
                    if (incoming.Length != 1) continue;
                    replacements.Add(parameter.Id, incoming[0]); block.Parameters.RemoveAt(p);
                    foreach (var edge in predecessors[block.Id]) edge.Edge.Arguments.RemoveAt(p);
                    changed = true;
                }
        } while (changed);
        foreach (var block in graph.Blocks) {
            for (int i = 0; i < block.Instructions.Count; i++)
                block.Instructions[i] = block.Instructions[i] with { Operation = block.Instructions[i].Operation.Map(Resolve) };
            block.Instructions.RemoveAll(i => i.Result is { } value && replacements.ContainsKey(value.Id));
            block.Terminator = block.Terminator!.Map(Resolve);
            foreach (var edge in block.Terminator.Edges)
                for (int p = 0; p < edge.Arguments.Count; p++) edge.Arguments[p] = Resolve(edge.Arguments[p]);
        }
        var definitions = graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null).ToDictionary(i => i.Result!.Value.Id);
        var known = new Dictionary<int, Expression?>();
        Expression? Constant(SsaValue value) {
            value = Resolve(value);
            if (known.TryGetValue(value.Id, out var found)) return found;
            known.Add(value.Id, null);
            if (!definitions.TryGetValue(value.Id, out var definition)) return null;
            var expression = definition.Operation is ValueOperation.Literal literal ? new Expression.Literal(literal.Value, value.Type)
                : ConstantExpression(definition, Constant);
            try { return known[value.Id] = expression is not null && ConstantEvaluator.TryEvaluate(expression, out var folded) ? folded : null; }
            catch (ShaderException) { return null; }
        }
        foreach (var block in graph.Blocks)
            for (int index = 0; index < block.Instructions.Count; index++) {
                var instruction = block.Instructions[index] with { Operation = block.Instructions[index].Operation.Map(Resolve) };
                if (instruction.Result is not { } result || !CanonicalTypes.Data(result.Type) || instruction.Effects != ShaderEffects.None || instruction.DiagnosticFilters.Count != 0) {
                    block.Instructions[index] = instruction; continue;
                }
                SsaValue? replacement = null;
                if (instruction.Operation is ValueOperation.Builtin { Function: "select", Arguments.Count: 3 } builtin)
                    instruction = instruction with { Operation = new ValueOperation.Select(builtin.Arguments[2], builtin.Arguments[1], builtin.Arguments[0]) };
                if (instruction.Operation is ValueOperation.Select select) {
                    if (select.Accept == select.Reject) replacement = select.Accept;
                    else if (Constant(select.Condition) is Expression.Literal { Value: bool condition }) replacement = condition ? select.Accept : select.Reject;
                }
                if (result.Type == ShaderType.Bool) {
                    if (Constant(result) is Expression.Literal { Value: bool value }) instruction = instruction with { Operation = new ValueOperation.Literal(value) };
                    else if (instruction.Operation is ValueOperation.Binary { Operator: "&&" or "||" or "&" or "|" } binary) {
                        bool and = binary.Operator is "&&" or "&";
                        var left = Constant(binary.Left) as Expression.Literal; var right = Constant(binary.Right) as Expression.Literal;
                        if (left?.Value is bool a) {
                            if (a == and) replacement = binary.Right;
                            else instruction = instruction with { Operation = new ValueOperation.Literal(a) };
                        }
                        else if (right?.Value is bool b) {
                            if (b == and) replacement = binary.Left;
                            else instruction = instruction with { Operation = new ValueOperation.Literal(b) };
                        }
                    }
                }
                if (replacement is { } alias) replacements.Add(result.Id, Resolve(alias));
                block.Instructions[index] = instruction; definitions[result.Id] = instruction; known.Clear();
            }
        foreach (var block in graph.Blocks) {
            for (int i = 0; i < block.Instructions.Count; i++) block.Instructions[i] = block.Instructions[i] with { Operation = block.Instructions[i].Operation.Map(Resolve) };
            block.Instructions.RemoveAll(i => i.Result is { } value && replacements.ContainsKey(value.Id));
            block.Terminator = block.Terminator!.Map(Resolve);
            foreach (var edge in block.Terminator.Edges)
                for (int p = 0; p < edge.Arguments.Count; p++) edge.Arguments[p] = Resolve(edge.Arguments[p]);
        }
        DeadValueElimination.Run(graph);
    }

    private static void PreserveRuntimeEvaluation(ControlFlowFunction graph, Module module)
    {
        var definitions = graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null).ToDictionary(i => i.Result!.Value.Id);
        var constants = module.Constants.Where(c => !c.IsOverride && !c.IsSpecialization && !graph.Signature.Arguments.Any(a => a.Name == c.Name))
            .ToDictionary(c => c.Name, StringComparer.Ordinal);
        var known = new Dictionary<int, Expression?>();
        Expression? Constant(SsaValue value) {
            if (known.TryGetValue(value.Id, out var cached)) return cached;
            known.Add(value.Id, null);
            if (!definitions.TryGetValue(value.Id, out var definition)) return null;
            Expression? expression = definition.Operation switch {
                ValueOperation.Literal literal => new Expression.Literal(literal.Value, value.Type),
                ValueOperation.Symbol symbol when constants.TryGetValue(symbol.Name, out var constant) => constant.Value,
                ValueOperation.Let => null, // An explicit target binding forces runtime evaluation.
                _ => ConstantExpression(definition, Constant)
            };
            try { return known[value.Id] = expression is not null && ConstantEvaluator.TryEvaluate(expression, out var folded) ? folded : null; }
            catch (ShaderException) { return null; }
        }
        var names = module.Functions.Select(f => f.Name).Concat(module.Constants.Select(c => c.Name)).Concat(module.Globals.Select(g => g.Name))
            .Concat(module.Structures.Select(s => s.Name)).Concat(graph.Signature.Arguments.Select(a => a.Name))
            .Concat(definitions.Values.Select(i => i.Operation).OfType<ValueOperation.Let>().Select(l => l.Name))
            .Concat(definitions.Values.Select(i => i.Operation).OfType<ValueOperation.Local>().Select(l => l.Name)).ToHashSet(StringComparer.Ordinal);
        int next = 0;
        string Fresh() { string name; do name = "sia_runtime_" + next++; while (!names.Add(name)); return name; }
        foreach (var block in graph.Blocks) {
            var instructions = block.Instructions.ToArray(); block.Instructions.Clear();
            foreach (var instruction in instructions) {
                SsaValue? capture = null;
                if (instruction.Operation is ValueOperation.Builtin builtin && instruction.Result is { } builtinResult
                    && WgslBuiltinConstantRules.InvalidArgument(builtin.Function, builtinResult.Type,
                        index => Constant(builtin.Arguments[index])) is int argument) capture = builtin.Arguments[argument];
                if (instruction.Operation is ValueOperation.Binary binary) {
                    if (binary.Operator is "/" or "%" && Integer(binary.Right.Type) && Constant(binary.Right) is { } divisor && Any(divisor, n => n == 0))
                        capture = binary.Right;
                    if (binary.Operator is "<<" or ">>" && Constant(binary.Right) is { } count
                        && Any(count, n => n >= Width(binary.Left.Type) * 8)) capture = binary.Right;
                }
                if (instruction.Operation is ValueOperation.Access access && Constant(access.Index) is { } index) {
                    ShaderType type = access.Base.Type is ShaderType.Pointer pointer ? pointer.Base : access.Base.Type;
                    uint? length = type switch { ShaderType.Array array => array.Length, ShaderType.Vector vector => (uint)vector.Size,
                        ShaderType.Matrix matrix => (uint)matrix.Columns, _ => null };
                    if (Any(index, n => n < 0 || length is uint limit && n >= limit)) capture = access.Index;
                }
                var constantExpression = ConstantExpression(instruction, Constant);
                if (capture is null && constantExpression is not null) {
                    bool supported;
                    try { supported = ConstantEvaluator.TryEvaluate(constantExpression, out _); }
                    catch (ShaderException) { supported = false; }
                    if (!supported) capture = instruction.Operation.Operands.FirstOrDefault();
                }
                if (capture is { } operand && instruction.Operation.Operands.Contains(operand)) {
                    var binding = graph.Value(operand.Type);
                    block.Instructions.Add(instruction with { Result = binding, Operation = new ValueOperation.Let(Fresh(), operand) });
                    block.Instructions.Add(instruction with { Operation = instruction.Operation.Map(v => v == operand ? binding : v) });
                }
                else block.Instructions.Add(instruction);
            }
        }
    }

    private static bool Integer(ShaderType type) => type is ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint }
        or ShaderType.Vector { Component.Kind: ScalarKind.Sint or ScalarKind.Uint };
    private static int Width(ShaderType type) => type is ShaderType.Vector v ? v.Component.Width : ((ShaderType.Scalar)type).Width;
    private static bool Any(Expression value, Func<double, bool> predicate) => value switch {
        Expression.Literal literal => predicate(System.Convert.ToDouble(literal.Value)),
        Expression.Construct construct => construct.Components.Any(c => Any(c, predicate)), _ => false
    };

    private static Expression? ConstantExpression(ControlFlowInstruction instruction, Func<SsaValue, Expression?> constant)
    {
        if (instruction.Result is not { } result || !CanonicalTypes.Data(result.Type)) return null;
        var operands = instruction.Operation.Operands.Select(constant).ToArray();
        if (operands.Any(v => v is null)) return null;
        Expression E(int index) => operands[index]!;
        return instruction.Operation switch {
            ValueOperation.Unary unary => new Expression.Unary(unary.Operator, E(0), result.Type),
            ValueOperation.Binary binary => new Expression.Binary(binary.Operator, E(0), E(1), result.Type),
            ValueOperation.Convert convert => new Expression.Convert(result.Type, E(0), convert.Bitcast),
            ValueOperation.Construct => new Expression.Construct(result.Type, operands.Select(v => v!).ToArray()),
            ValueOperation.Select => new Expression.Select(E(0), E(1), E(2)),
            ValueOperation.Access => new Expression.Access(E(0), E(1), result.Type),
            ValueOperation.Member member => new Expression.Member(E(0), member.Name, result.Type),
            ValueOperation.Swizzle swizzle => new Expression.Swizzle(E(0), swizzle.Components, result.Type),
            ValueOperation.Builtin builtin when ShaderBuiltinEffects.IsPureNumeric(builtin.Function)
                => new Expression.Call(builtin.Function, operands.Select(v => v!).ToArray(), result.Type, CallBinding.Builtin),
            _ => null
        };
    }
}
