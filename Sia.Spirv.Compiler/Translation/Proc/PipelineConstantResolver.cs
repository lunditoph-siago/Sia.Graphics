using System.Globalization;
using System.Numerics;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Proc;

/// <summary>Resolve pipeline-overridable constants into an independent, validated module.</summary>
public static class PipelineConstantResolver
{
    internal static Func<string, Expression> DefaultValues(Module module)
    {
        var resolver = new Resolver(module, new Dictionary<string, double>());
        return resolver.Resolve;
    }

    internal static Func<Expression, Expression> DefaultExpressions(Module module)
    {
        var resolver = new Resolver(module, new Dictionary<string, double>());
        return resolver.ResolveExpression;
    }

    public static Module Resolve(Module module, IReadOnlyDictionary<string, double> values)
    {
        ArgumentNullException.ThrowIfNull(module); ArgumentNullException.ThrowIfNull(values);
        Valid.ModuleValidator.Validate(module);
        var result = new Resolver(module, values).Run();
        Valid.ModuleValidator.Validate(result); return result;
    }

    internal static CanonicalModule Resolve(CanonicalModule canonical, IReadOnlyDictionary<string, double> values)
    {
        ArgumentNullException.ThrowIfNull(canonical); ArgumentNullException.ThrowIfNull(values);
        Valid.ModuleValidator.Validate(canonical, native: true);
        var resolver = new Resolver(canonical.Declarations, values);
        var output = resolver.Run(canonical);
        var declarations = output.Functions.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var graphs = canonical.Functions.ToDictionary(p => p.Key, p => resolver.Graph(p.Value, declarations[p.Key]), StringComparer.Ordinal);
        var result = new CanonicalModule(output, graphs, canonical.DeferredFunctions, canonical.EntryFunctions);
        Valid.ModuleValidator.Validate(result, native: true);
        return result;
    }

    private sealed class Resolver(Module input, IReadOnlyDictionary<string, double> values)
    {
        private readonly Dictionary<string, ShaderConstant> constants = input.Constants.ToDictionary(c => c.Name, StringComparer.Ordinal);
        private readonly Dictionary<string, Expression> resolved = new(StringComparer.Ordinal);
        private readonly HashSet<string> active = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double> supplied = new(StringComparer.Ordinal);
        private readonly Dictionary<ShaderType, ShaderType> types = [];
        private HashSet<string> locals = new(StringComparer.Ordinal);
        private static ShaderException Error(string message, SourceSpan span = default) => new(DiagnosticStage.Validation, message, span);

        public Module Run(CanonicalModule? canonical = null)
        {
            // An explicit @id replaces the name as the pipeline API identifier.
            foreach (var pair in values)
            {
                var constant = input.Constants.FirstOrDefault(c => c.IsOverride && (c.OverrideId is uint id
                    ? uint.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out uint key) && key == id
                    : pair.Key == c.Name));
                if (constant is null) throw Error($"Pipeline constant '{pair.Key}' was not found.");
                if (!supplied.TryAdd(constant.Name, pair.Value)) throw Error($"Pipeline constant '{constant.Name}' was specified more than once.");
            }
            foreach (var constant in input.Constants) Resolve(constant.Name);
            var output = new Module { VulkanMemoryModel = input.VulkanMemoryModel, WorkgroupInitializationRequired = input.WorkgroupInitializationRequired }; output.Enables.UnionWith(input.Enables);
            output.Structures.AddRange(input.Structures.Select(s => (ShaderType.Structure)Type(s)));
            output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
            output.Constants.AddRange(input.Constants.Select(c => c with { Type = Type(c.Type), Value = resolved[c.Name], IsOverride = false, OverrideId = null, IsSpecialization = false }));
            foreach (var global in input.Globals)
                output.Globals.Add(global with { Type = Type(global.Type), Initializer = global.Initializer is null ? null : ConstantEvaluator.Evaluate(Expr(global.Initializer).Value) });
            foreach (var function in input.Functions)
            {
                locals = new(StringComparer.Ordinal);
                var dimensions = function.WorkgroupSize.Select(e => ConstantEvaluator.Evaluate(Expr(e).Value)).ToArray();
                locals = function.Arguments.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
                var copy = new ShaderFunction(function.Name)
                {
                    ReturnType = Type(function.ReturnType), ReturnBinding = function.ReturnBinding, Stage = function.Stage,
                    TaskPayload = function.TaskPayload, MeshOutput = function.MeshOutput,
                    EarlyDepthTest = function.EarlyDepthTest, ConservativeDepth = function.ConservativeDepth,
                    WorkgroupSize = dimensions,
                    Body = canonical?.Functions.ContainsKey(function.Name) == true ? new Block() : Body(function.Body)
                };
                copy.Arguments.AddRange(function.Arguments.Select(a => a with { Type = Type(a.Type) })); output.Functions.Add(copy);
                copy.DiagnosticFilters.AddRange(function.DiagnosticFilters);
                if (canonical?.Functions.ContainsKey(function.Name) == true)
                    copy.Body.DiagnosticFilters.AddRange(function.Body.DiagnosticFilters);
            }
            return output;
        }

        public ControlFlowFunction Graph(ControlFlowFunction input, ShaderFunction signature)
        {
            var graph = input.Copy(signature);
            var arguments = signature.Arguments.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
            SsaValue Value(SsaValue value) => new(value.Id, Type(value.Type));
            ValueOperation Operation(ValueOperation operation) => operation.Map(Value) switch {
                ValueOperation.Call c => c with { ReturnType = Type(c.ReturnType) },
                ValueOperation.Builtin b => b with { ReturnType = Type(b.ReturnType) },
                ValueOperation.MeshStore m => m with { Field = m.Field with { Type = Type(m.Field.Type) } },
                ValueOperation.InterfaceLoad i => i with { Field = i.Field with { Type = Type(i.Field.Type) } },
                ValueOperation.InterfaceStore i => i with { Field = i.Field with { Type = Type(i.Field.Type) } },
                var mapped => mapped
            };
            foreach (var block in graph.Blocks) {
                for (int i = 0; i < block.Parameters.Count; i++) block.Parameters[i] = Value(block.Parameters[i]);
                var instructions = block.Instructions.ToArray(); block.Instructions.Clear();
                void Define(Expression expression, SsaValue result, ControlFlowInstruction origin) {
                    SsaValue Child(Expression child) {
                        var value = graph.Value(Type(child.Type)); Define(child, value, origin); return value;
                    }
                    var operation = expression switch {
                        Expression.Literal literal => (ValueOperation)new ValueOperation.Literal(literal.Value),
                        Expression.Construct construct => new ValueOperation.Construct(construct.Components.Select(Child).ToArray()),
                        _ => throw Error("Resolved pipeline constant is not a concrete value.", origin.Span)
                    };
                    block.Instructions.Add(origin with { Result = result, Operation = operation });
                }
                foreach (var instruction in instructions) {
                    var mapped = instruction with { Result = instruction.Result is { } result ? Value(result) : null,
                        Operation = Operation(instruction.Operation) };
                    if (instruction.Result is { } oldValue && instruction.Operation is ValueOperation.Symbol symbol
                        && !arguments.Contains(symbol.Name) && constants.ContainsKey(symbol.Name))
                        Define(resolved[symbol.Name], Value(oldValue), mapped);
                    else block.Instructions.Add(mapped);
                }
                block.Terminator = block.Terminator!.Map(Value);
                foreach (var edge in block.Terminator.Edges)
                    for (int i = 0; i < edge.Arguments.Count; i++) edge.Arguments[i] = Value(edge.Arguments[i]);
            }
            return graph;
        }

        public Expression ResolveExpression(Expression expression) => ConstantEvaluator.Evaluate(Expr(expression).Value);

        private uint Length(string name)
        {
            if (Resolve(name) is not Expression.Literal literal) throw Error("Array length override did not resolve to an integer.");
            try
            {
                uint count = System.Convert.ToUInt32(literal.Value, CultureInfo.InvariantCulture);
                return count != 0 ? count : throw Error("Resolved array length must be positive.");
            }
            catch (OverflowException) { throw Error("Resolved array length must be positive and fit in u32."); }
        }

        private ShaderType Type(ShaderType input)
        {
            if (types.TryGetValue(input, out var mapped)) return mapped;
            mapped = input switch
            {
                ShaderType.Array a => a with { Element = Type(a.Element), Length = a.OverrideLength is string n ? Length(n) : a.Length, OverrideLength = null },
                ShaderType.BindingArray a => a with { Element = Type(a.Element), Length = a.OverrideLength is string n ? Length(n) : a.Length, OverrideLength = null },
                ShaderType.Pointer p => p with { Base = Type(p.Base) },
                ShaderType.Structure s => s with { Members = s.Members.Select(m => m with { Type = Type(m.Type) }).ToArray() },
                _ => input
            };
            if (mapped is ShaderType.Array array)
            {
                try { _ = TypeLayout.Of(array); }
                catch (OverflowException) { throw Error("Resolved array layout is too large."); }
            }
            types.Add(input, mapped); return mapped;
        }

        public Expression Resolve(string name)
        {
            if (resolved.TryGetValue(name, out var known)) return known;
            if (!active.Add(name)) throw Error($"Cyclic pipeline constant dependency '{name}'.");
            var constant = constants[name];
            Expression value;
            if (constant.IsOverride && supplied.TryGetValue(name, out double number)) value = Number(number, (ShaderType.Scalar)constant.Type);
            else if (constant.Value is not null) value = ConstantEvaluator.Evaluate(Expr(constant.Value).Value);
            else throw Error($"Missing value for pipeline constant '{constant.OverrideId?.ToString(CultureInfo.InvariantCulture) ?? name}'.");
            active.Remove(name); resolved.Add(name, value); return value;
        }

        private static Expression Number(double value, ShaderType.Scalar type)
        {
            if (type.Kind == ScalarKind.Bool) return Expression.Bool(value != 0 && !double.IsNaN(value));
            if (!double.IsFinite(value)) throw Error("Pipeline constant must be finite for a numeric destination.");
            object converted;
            if (type.Kind is ScalarKind.Sint or ScalarKind.Uint)
            {
                var integer = new BigInteger(Math.Truncate(value)); int bits = type.Width * 8;
                BigInteger min = type.Kind == ScalarKind.Sint ? -(BigInteger.One << (bits - 1)) : BigInteger.Zero;
                BigInteger max = (BigInteger.One << (type.Kind == ScalarKind.Sint ? bits - 1 : bits)) - 1;
                if (integer < min || integer > max) throw Error("Pipeline constant is outside its destination type range.");
                converted = (type.Kind, type.Width) switch
                {
                    (ScalarKind.Sint, 2) => (object)(short)integer, (ScalarKind.Uint, 2) => (ushort)integer,
                    (ScalarKind.Sint, 4) => (int)integer, (ScalarKind.Uint, 4) => (uint)integer,
                    (ScalarKind.Sint, 8) => (long)integer, (ScalarKind.Uint, 8) => (ulong)integer,
                    _ => throw Error("Unsupported pipeline constant integer type.")
                };
            }
            else
            {
                converted = type.Width switch { 2 => (object)(Half)value, 4 => (float)value, 8 => value, _ => throw Error("Unsupported pipeline constant float type.") };
                if (converted is Half h && !Half.IsFinite(h) || converted is float f && !float.IsFinite(f)) throw Error("Pipeline constant is outside its destination type range.");
            }
            return new Expression.Literal(converted, type);
        }

        private (Expression Value, bool Override) Expr(Expression input)
        {
            if (input is Expression.Reference r && !locals.Contains(r.Name) && constants.TryGetValue(r.Name, out var constant)) return (Resolve(r.Name), constant.IsOverride || constant.IsSpecialization);
            bool changed = false;
            Expression Child(Expression e) { var result = Expr(e); changed |= result.Override; return result.Value; }
            Expression mapped = input switch
            {
                Expression.Literal or Expression.HelperInvocation => input,
                Expression.Reference reference => new Expression.Reference(reference.Name, Type(reference.Type)),
                Expression.Load l => new Expression.Load(Child(l.Pointer)) { MemoryAccess = l.MemoryAccess },
                Expression.Unary u => new Expression.Unary(u.Operator, Child(u.Operand), Type(u.Type)),
                Expression.Binary b => new Expression.Binary(b.Operator, Child(b.Left), Child(b.Right), Type(b.Type)),
                Expression.Call c => new Expression.Call(c.Function, c.Arguments.Select(Child).ToArray(), Type(c.Type)) { Binding = c.Binding, AtomicMemory = c.AtomicMemory, MemoryAccess = c.MemoryAccess },
                Expression.Construct c => new Expression.Construct(Type(c.Type), c.Components.Select(Child).ToArray()),
                Expression.Convert c => new Expression.Convert(Type(c.Type), Child(c.Operand), c.Bitcast),
                Expression.Access a => new Expression.Access(Child(a.Base), Child(a.Index), Type(a.Type)),
                Expression.Member m => new Expression.Member(Child(m.Base), m.Name, Type(m.Type)),
                Expression.Swizzle s => new Expression.Swizzle(Child(s.Vector), s.Components, Type(s.Type)),
                Expression.Select s => new Expression.Select(Child(s.Condition), Child(s.Accept), Child(s.Reject)),
                _ => throw Error("Unsupported expression during pipeline constant resolution.", input.Span)
            };
            mapped = mapped with { Span = input.Span };
            if (changed && ConstantEvaluator.TryEvaluate(mapped, out var folded)) mapped = folded;
            return (mapped, changed);
        }

        private Block Body(Block input, bool nested = true)
        {
            var outer = locals;
            if (nested) locals = new(locals, StringComparer.Ordinal);
            var output = new Block();
            output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
            Expression E(Expression e) => Expr(e).Value;
            foreach (var statement in input.Statements)
            {
                Statement mapped;
                if (statement is Statement.Declare d)
                {
                    mapped = d with { Type = Type(d.Type), Initializer = d.Initializer is null ? null : E(d.Initializer) };
                    locals.Add(d.Name);
                }
                else if (statement is Statement.Loop l)
                {
                    var loopOuter = locals; locals = new(locals, StringComparer.Ordinal);
                    var body = Body(l.Body, false); var continuing = Body(l.Continuing, false);
                    mapped = new Statement.Loop(body, continuing, l.BreakIf is null ? null : E(l.BreakIf)); locals = loopOuter;
                }
                else mapped = statement switch
                {
                    Statement.Nested n => new Statement.Nested(Body(n.Body)),
                    Statement.Store s => s with { Target = E(s.Target), Value = E(s.Value) },
                    Statement.Evaluate e => new Statement.Evaluate(E(e.Value)),
                    Statement.If i => new Statement.If(E(i.Condition), Body(i.Accept), Body(i.Reject)),
                    Statement.Switch s => new Statement.Switch(E(s.Selector), s.Cases.Select(c => c with { Body = Body(c.Body) }).ToArray()),
                    Statement.Return r => new Statement.Return(r.Value is null ? null : E(r.Value)),
                    _ => statement
                };
                output.Statements.Add(mapped with { Span = statement.Span });
            }
            if (nested) locals = outer;
            return output;
        }
    }
}
