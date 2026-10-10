using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Materialize SPIR-V integer safety and signed remainder before serialization.</summary>
internal static class SpirvIntegerArithmeticLowering
{
    public static Module Run(Module input, bool divisionChecks)
    {
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
        foreach (var function in input.Functions) { names.UnionWith(function.Arguments.Select(a => a.Name)); Names(function.Body); }
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
            Expression Literal(long value) {
                object payload = (scalar.Kind, scalar.Width) switch {
                    (ScalarKind.Sint, 2) => (short)value, (ScalarKind.Sint, 4) => (int)value, (ScalarKind.Sint, 8) => value,
                    (ScalarKind.Uint, 2) => (ushort)value, (ScalarKind.Uint, 4) => (uint)value, (ScalarKind.Uint, 8) => (ulong)value,
                    _ => throw new ShaderException(DiagnosticStage.SpirvWrite, "Unsupported integer arithmetic type.")
                };
                var literal = new Expression.Literal(payload, scalar);
                return type is ShaderType.Vector vector ? new Expression.Construct(type, Enumerable.Repeat<Expression>(literal, vector.Size).ToArray()) : literal;
            }
            Expression numerator = new Expression.Reference("numerator", type), divisor = new Expression.Reference("divisor", type);
            if (divisionChecks) {
                ShaderType boolean = type is ShaderType.Vector vector ? new ShaderType.Vector(vector.Size, ShaderType.Bool) : ShaderType.Bool;
                Expression invalid = new Expression.Binary("==", divisor, Literal(0), boolean);
                if (scalar.Kind == ScalarKind.Sint) {
                    var minimum = Literal(scalar.Width switch { 2 => short.MinValue, 8 => long.MinValue, _ => int.MinValue });
                    Expression isMinimum = new Expression.Binary("==", numerator, minimum, boolean),
                        isMinusOne = new Expression.Binary("==", divisor, Literal(-1), boolean);
                    Expression BoolSplat(bool value) => new Expression.Construct(boolean,
                        Enumerable.Repeat<Expression>(Expression.Bool(value), ((ShaderType.Vector)boolean).Size).ToArray());
                    Expression overflow = boolean is ShaderType.Vector ? new Expression.Select(isMinimum, isMinusOne, BoolSplat(false))
                        : new Expression.Binary("&&", isMinimum, isMinusOne, boolean);
                    invalid = boolean is ShaderType.Vector ? new Expression.Select(invalid, BoolSplat(true), overflow)
                        : new Expression.Binary("||", invalid, overflow, boolean);
                }
                function.Body.Statements.Add(new Statement.Declare("safe_divisor", type, new Expression.Select(invalid, Literal(1), divisor), false));
                divisor = new Expression.Reference("safe_divisor", type);
            }
            Expression result = new Expression.Binary(op, numerator, divisor, type);
            if (op == "%" && scalar.Kind == ScalarKind.Sint)
                result = new Expression.Binary("-", numerator, new Expression.Binary("*", divisor,
                    new Expression.Binary("/", numerator, divisor, type), type), type);
            function.Body.Statements.Add(new Statement.Return(result)); helpers.Add((type, op), function); return function;
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
            if (mapped is not Expression.Binary { Operator: "/" or "%" } binary
                || Scalar(binary.Type) is not { Kind: ScalarKind.Sint or ScalarKind.Uint } scalar
                || !divisionChecks && !(binary.Operator == "%" && scalar.Kind == ScalarKind.Sint)) return mapped;
            Expression Broadcast(Expression value) => value.Type == binary.Type ? value : new Expression.Construct(binary.Type, [value]) { Span = value.Span };
            // Function arguments snapshot the two evaluated operands once, in source order.
            return new Expression.Call(Helper(binary.Type, binary.Operator).Name, [Broadcast(binary.Left), Broadcast(binary.Right)], binary.Type) { Span = binary.Span };
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
            var copy = new ShaderFunction(function.Name) { Stage = function.Stage, ReturnType = function.ReturnType, ReturnBinding = function.ReturnBinding,
                TaskPayload = function.TaskPayload, MeshOutput = function.MeshOutput, WorkgroupSize = function.WorkgroupSize.ToArray(),
                EarlyDepthTest = function.EarlyDepthTest, ConservativeDepth = function.ConservativeDepth, Body = Body(function.Body) };
            copy.Arguments.AddRange(function.Arguments); copy.DiagnosticFilters.AddRange(function.DiagnosticFilters); output.Functions.Add(copy);
        }
        output.Functions.AddRange(helpers.Values);
        ModuleValidator.Validate(output);
        foreach (var function in helpers.Values) {
            if (!StructuredControlFlowReader.TryRead(function, output, out var graph, out var deferred))
                throw new ShaderException(DiagnosticStage.SpirvWrite, "Integer legalization requires canonical verification: " + deferred);
            ControlFlowVerifier.Validate(graph!, output); LocalValuePromotion.Run(graph!); ControlFlowVerifier.Validate(graph!, output);
        }
        return output;
    }
}
