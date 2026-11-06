using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Materialize SPIR-V integer safety and signed remainder before serialization.</summary>
internal static partial class SpirvIntegerArithmeticLowering
{
    public static Module Run(Module input, bool divisionChecks)
        => ShaderTargetLowering.ForStructured(Run(SpirvControlFlowLowering.Capture(input), divisionChecks));

    internal static CanonicalModule Run(CanonicalModule canonical, bool divisionChecks)
    {
        ModuleValidator.Validate(canonical);
        var input = canonical.Declarations;
        var graphs = canonical.Functions.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal);
        var output = new Module { VulkanMemoryModel = input.VulkanMemoryModel, WorkgroupInitializationRequired = input.WorkgroupInitializationRequired };
        output.Structures.AddRange(input.Structures); output.Globals.AddRange(input.Globals); output.Constants.AddRange(input.Constants);
        output.Enables.UnionWith(input.Enables); output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        var names = input.Functions.Select(f => f.Name).Concat(input.Globals.Select(g => g.Name))
            .Concat(input.Constants.Select(c => c.Name)).Concat(input.Structures.Select(s => s.Name)).ToHashSet(StringComparer.Ordinal);
        void Names(Block block) {
            foreach (var statement in block.Statements) {
                if (statement is Statement.Declare declaration) names.Add(declaration.Name);
                foreach (var child in statement switch {
                    Statement.Nested n => new[] { n.Body }, Statement.If i => [i.Accept, i.Reject],
                    Statement.Loop l => [l.Body, l.Continuing], Statement.Switch s => s.Cases.Select(c => c.Body).ToArray(), _ => []
                }) Names(child);
            }
        }
        foreach (var function in input.Functions) {
            names.UnionWith(function.Arguments.Select(a => a.Name));
            if (canonical.DeferredFunctions.ContainsKey(function.Name)) Names(function.Body);
        }
        foreach (var instruction in graphs.Values.SelectMany(g => g.Blocks).SelectMany(b => b.Instructions))
            if (instruction.Operation is ValueOperation.Local local) names.Add(local.Name);
            else if (instruction.Operation is ValueOperation.Symbol symbol) names.Add(symbol.Name);
        var helpers = new Dictionary<(ShaderType Type, string Operator), ShaderFunction>();
        ShaderType.Scalar? Scalar(ShaderType type) => type switch { ShaderType.Scalar s => s, ShaderType.Vector v => v.Component, _ => null };
        ShaderFunction Helper(ShaderType type, string op)
        {
            if (helpers.TryGetValue((type, op), out var existing)) return existing;
            string prefix = "sia_spv_integer_" + (op == "/" ? "divide_" : "remainder_") + helpers.Count;
            string name = prefix; for (int suffix = 0; !names.Add(name); suffix++) name = prefix + "_" + suffix;
            var function = new ShaderFunction(name) { ReturnType = type };
            function.Arguments.AddRange([new("numerator", type), new("divisor", type)]);
            var scalar = Scalar(type)!;
            var graph = new ControlFlowFunction(function); var block = graph.Block(); graph.Entry = block.Id;
            SsaValue Emit(ShaderType valueType, ValueOperation operation) {
                var value = graph.Value(valueType); block.Instructions.Add(new(value, operation)); return value;
            }
            SsaValue Literal(long value) {
                object payload = (scalar.Kind, scalar.Width) switch {
                    (ScalarKind.Sint, 2) => (short)value, (ScalarKind.Sint, 4) => (int)value, (ScalarKind.Sint, 8) => value,
                    (ScalarKind.Uint, 2) => (ushort)value, (ScalarKind.Uint, 4) => (uint)value, (ScalarKind.Uint, 8) => (ulong)value,
                    _ => throw new ShaderException(DiagnosticStage.SpirvWrite, "Unsupported integer arithmetic type.")
                };
                var literal = Emit(scalar, new ValueOperation.Literal(payload));
                return type is ShaderType.Vector vector ? Emit(type, new ValueOperation.Construct(Enumerable.Repeat(literal, vector.Size).ToArray())) : literal;
            }
            var numerator = Emit(type, new ValueOperation.Symbol("numerator"));
            var divisor = Emit(type, new ValueOperation.Symbol("divisor"));
            if (divisionChecks) {
                ShaderType boolean = type is ShaderType.Vector vector ? new ShaderType.Vector(vector.Size, ShaderType.Bool) : ShaderType.Bool;
                SsaValue Bool(bool value) {
                    var literal = Emit(ShaderType.Bool, new ValueOperation.Literal(value));
                    return boolean is ShaderType.Vector v ? Emit(boolean, new ValueOperation.Construct(Enumerable.Repeat(literal, v.Size).ToArray())) : literal;
                }
                var invalid = Emit(boolean, new ValueOperation.Binary("==", divisor, Literal(0)));
                if (scalar.Kind == ScalarKind.Sint) {
                    var minimum = Literal(scalar.Width switch { 2 => short.MinValue, 8 => long.MinValue, _ => int.MinValue });
                    var isMinimum = Emit(boolean, new ValueOperation.Binary("==", numerator, minimum));
                    var isMinusOne = Emit(boolean, new ValueOperation.Binary("==", divisor, Literal(-1)));
                    // These comparisons are pure SSA values; selects preserve the
                    // scalar/vector truth table without introducing a short-circuit CFG.
                    var overflow = Emit(boolean, new ValueOperation.Select(isMinimum, isMinusOne, Bool(false)));
                    invalid = Emit(boolean, new ValueOperation.Select(invalid, Bool(true), overflow));
                }
                divisor = Emit(type, new ValueOperation.Select(invalid, Literal(1), divisor));
            }
            var quotient = Emit(type, new ValueOperation.Binary(op == "%" && scalar.Kind == ScalarKind.Sint ? "/" : op, numerator, divisor));
            var result = op == "%" && scalar.Kind == ScalarKind.Sint
                ? Emit(type, new ValueOperation.Binary("-", numerator, Emit(type, new ValueOperation.Binary("*", divisor, quotient)))) : quotient;
            block.Terminator = new ControlFlowTerminator.Return(result);
            graphs.Add(name, graph); helpers.Add((type, op), function); return function;
        }
        bool RequiresHelper(ShaderType type, string op) => op is "/" or "%"
            && Scalar(type) is { Kind: ScalarKind.Sint or ScalarKind.Uint } scalar
            && (divisionChecks || op == "%" && scalar.Kind == ScalarKind.Sint);
        foreach (var graph in graphs.Values.ToArray()) {
            var facts = new IntegerFacts(graph, input);
            foreach (var block in graph.Blocks) {
                var instructions = block.Instructions.ToArray(); block.Instructions.Clear();
                SsaValue Define(Expression expression, ControlFlowInstruction origin) {
                    var value = graph.Value(expression.Type);
                    ValueOperation operation = expression switch {
                        Expression.Literal literal => new ValueOperation.Literal(literal.Value),
                        Expression.Construct construct => new ValueOperation.Construct(construct.Components.Select(c => Define(c, origin)).ToArray()),
                        _ => throw new InvalidOperationException("Expected an integer constant.")
                    };
                    block.Instructions.Add(origin with { Result = value, Operation = operation }); return value;
                }
                foreach (var instruction in instructions) {
                    if (instruction.Result is not { } result || instruction.Operation is not ValueOperation.Binary binary
                        || Scalar(result.Type) is not { Kind: ScalarKind.Sint or ScalarKind.Uint } scalar) {
                        block.Instructions.Add(instruction); continue;
                    }
                    if (binary.Operator == "&" && (facts.RedundantMask(binary.Left, binary.Right) || facts.RedundantMask(binary.Right, binary.Left))) {
                        var value = facts.RedundantMask(binary.Left, binary.Right) ? binary.Left : binary.Right;
                        block.Instructions.Add(instruction with { Operation = new ValueOperation.Let("sia_spv_masked", value) }); continue;
                    }
                    if (binary.Operator is "<<" or ">>") {
                        if (facts.BoundedShift(binary.Right, scalar.Width * 8)) { block.Instructions.Add(instruction); continue; }
                        var mask = Define(Splat(Expression.U32((uint)(scalar.Width * 8 - 1)), binary.Right.Type), instruction);
                        var count = graph.Value(binary.Right.Type);
                        block.Instructions.Add(instruction with { Result = count, Operation = new ValueOperation.Binary("&", binary.Right, mask) });
                        block.Instructions.Add(instruction with { Operation = binary with { Right = count } }); continue;
                    }
                    if (binary.Operator is not ("/" or "%")) { block.Instructions.Add(instruction); continue; }
                    SsaValue Broadcast(SsaValue value) {
                        if (value.Type == result.Type) return value;
                        var broadcast = graph.Value(result.Type);
                        block.Instructions.Add(new(broadcast, new ValueOperation.Construct([value]), instruction.Span) { DiagnosticFilters = instruction.DiagnosticFilters });
                        return broadcast;
                    }
                    var divisor = facts.Constant(binary.Right);
                    if (SafeDivisor(divisor, scalar) || scalar.Kind == ScalarKind.Uint && facts.Nonzero(binary.Right)) {
                        var left = Broadcast(binary.Left); var right = Broadcast(binary.Right);
                        if (divisor is not null && UnsignedReduction(divisor, result.Type, binary.Operator) is { } reduction)
                            block.Instructions.Add(instruction with { Operation = new ValueOperation.Binary(reduction.Operator, left, Define(reduction.Operand, instruction)) });
                        else if (binary.Operator == "%" && scalar.Kind == ScalarKind.Sint) {
                            // Reuse the evaluated operands. OpSRem is undefined for
                            // negative operands in the baseline Vulkan environment.
                            var quotient = graph.Value(result.Type); var product = graph.Value(result.Type);
                            block.Instructions.Add(instruction with { Result = quotient, Operation = new ValueOperation.Binary("/", left, right) });
                            block.Instructions.Add(instruction with { Result = product, Operation = new ValueOperation.Binary("*", right, quotient) });
                            block.Instructions.Add(instruction with { Operation = new ValueOperation.Binary("-", left, product) });
                        }
                        else block.Instructions.Add(instruction with { Operation = binary with { Left = left, Right = right } });
                        continue;
                    }
                    if (!RequiresHelper(result.Type, binary.Operator)) { block.Instructions.Add(instruction); continue; }
                    block.Instructions.Add(instruction with { Operation = new ValueOperation.Call(Helper(result.Type, binary.Operator).Name, [Broadcast(binary.Left), Broadcast(binary.Right)], result.Type) });
                }
            }
        }
        Expression Expr(Expression expression) {
            var mapped = expression switch {
                Expression.Load l => l with { Pointer = Expr(l.Pointer) }, Expression.Unary u => u with { Operand = Expr(u.Operand) },
                Expression.Binary b => b with { Left = Expr(b.Left), Right = Expr(b.Right) },
                Expression.Call c => c with { Arguments = c.Arguments.Select(Expr).ToArray() },
                Expression.Construct c => c with { Components = c.Components.Select(Expr).ToArray() },
                Expression.Convert c => c with { Operand = Expr(c.Operand) }, Expression.Access a => a with { Base = Expr(a.Base), Index = Expr(a.Index) },
                Expression.Member m => m with { Base = Expr(m.Base) }, Expression.Swizzle s => s with { Vector = Expr(s.Vector) },
                Expression.Select s => s with { Condition = Expr(s.Condition), Accept = Expr(s.Accept), Reject = Expr(s.Reject) }, _ => expression
            };
            if (mapped is not Expression.Binary binary || Scalar(binary.Type) is not { Kind: ScalarKind.Sint or ScalarKind.Uint } scalar) return mapped;
            if (binary.Operator is "<<" or ">>") {
                if (BoundedShift(binary.Right, scalar.Width * 8)) return binary;
                var mask = Splat(Expression.U32((uint)(scalar.Width * 8 - 1)), binary.Right.Type);
                return binary with { Right = new Expression.Binary("&", binary.Right, mask, binary.Right.Type) };
            }
            if (binary.Operator is not ("/" or "%")) return mapped;
            if (ConstantEvaluator.TryEvaluateRuntime(binary.Right, out var divisor) && SafeDivisor(divisor, scalar)) {
                if (UnsignedReduction(divisor, binary.Type, binary.Operator) is { } reduction)
                    return new Expression.Binary(reduction.Operator, binary.Left.Type == binary.Type ? binary.Left : new Expression.Construct(binary.Type, [binary.Left]), reduction.Operand, binary.Type) { Span = binary.Span };
                // The structured adapter cannot duplicate effectful operands for
                // signed remainder; the helper snapshots them in source order.
                if (binary.Operator != "%" || scalar.Kind != ScalarKind.Sint) return binary;
            }
            if (scalar.Kind == ScalarKind.Uint && Nonzero(binary.Right)) return binary;
            if (!RequiresHelper(binary.Type, binary.Operator)) return mapped;
            Expression Broadcast(Expression value) => value.Type == binary.Type ? value : new Expression.Construct(binary.Type, [value]) { Span = value.Span };
            // Function arguments snapshot the two evaluated operands once, in source order.
            return new Expression.Call(Helper(binary.Type, binary.Operator).Name, [Broadcast(binary.Left), Broadcast(binary.Right)], binary.Type, CallBinding.Function) { Span = binary.Span };
        }
        Block Body(Block block) {
            var copy = new Block(); copy.DiagnosticFilters.AddRange(block.DiagnosticFilters);
            foreach (var statement in block.Statements) copy.Statements.Add(statement switch {
                Statement.Declare d => d with { Initializer = d.Initializer is null ? null : Expr(d.Initializer) },
                Statement.Store s => s with { Target = Expr(s.Target), Value = Expr(s.Value) }, Statement.Evaluate e => e with { Value = Expr(e.Value) },
                Statement.Return r => r with { Value = r.Value is null ? null : Expr(r.Value) }, Statement.Nested n => n with { Body = Body(n.Body) },
                Statement.If i => i with { Condition = Expr(i.Condition), Accept = Body(i.Accept), Reject = Body(i.Reject) },
                Statement.Loop l => l with { Body = Body(l.Body), Continuing = Body(l.Continuing), BreakIf = l.BreakIf is null ? null : Expr(l.BreakIf) },
                Statement.Switch s => s with { Selector = Expr(s.Selector), Cases = s.Cases.Select(c => c with { Body = Body(c.Body) }).ToArray() }, _ => statement
            });
            return copy;
        }
        foreach (var function in input.Functions) {
            if (graphs.ContainsKey(function.Name)) { output.Functions.Add(function); continue; }
            var copy = new ShaderFunction(function.Name) { Stage = function.Stage, ReturnType = function.ReturnType, ReturnBinding = function.ReturnBinding,
                TaskPayload = function.TaskPayload, MeshOutput = function.MeshOutput, WorkgroupSize = function.WorkgroupSize.ToArray(),
                EarlyDepthTest = function.EarlyDepthTest, ConservativeDepth = function.ConservativeDepth, Body = Body(function.Body) };
            copy.Arguments.AddRange(function.Arguments); copy.DiagnosticFilters.AddRange(function.DiagnosticFilters); output.Functions.Add(copy);
        }
        output.Functions.AddRange(helpers.Values);
        var lowered = new CanonicalModule(output, graphs, canonical.DeferredFunctions, canonical.EntryFunctions);
        ModuleValidator.Validate(lowered);
        return lowered;
    }
}
