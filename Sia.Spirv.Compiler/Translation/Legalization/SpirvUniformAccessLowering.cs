using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Materialize uniform layout reads and captured paths before serialization.</summary>
internal sealed partial class SpirvUniformAccessLowering(SpirvPhysicalLayout layout)
{
    private sealed record Step(string? Member, Expression? Index, uint? Constant);
    private sealed record Location(GlobalVariable Root, IReadOnlyList<Step> Steps);
    private abstract record Read;
    private sealed record ValueRead(Expression Value) : Read;
    private sealed record SelectedRead(Expression Index, IReadOnlyList<Read> Arms, ShaderType Type) : Read;
    private readonly Module output = new();
    private readonly List<ShaderFunction> helpers = [];
    private readonly Stack<Dictionary<string, Location?>> scopes = [];
    private readonly HashSet<string> names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GlobalVariable> converted = layout.Module.Globals
        .Where(g => g.Space == AddressSpace.Uniform && g.Type != layout.Globals[g.Name].PhysicalType).ToDictionary(g => g.Name, StringComparer.Ordinal);
    private int next;
    private bool changed;
    private bool VulkanMemory => layout.Module.VulkanMemoryModel || ShaderMemoryRequirements.UsesCooperativeMemoryModel(layout.Module);
    private static ShaderType Data(ShaderType type) => type is ShaderType.Pointer p ? p.Base : type;
    private static ShaderType.Pointer Pointer(ShaderType type) => new(type, AddressSpace.Uniform, StorageAccess.Read);
    private static ShaderException Error(string message, SourceSpan span = default) => new(DiagnosticStage.SpirvWrite, message, span);
    private string Name(string stem) { string name; do name = stem + next++; while (!names.Add(name)); return name; }

    internal static SpirvPhysicalLayout Run(SpirvPhysicalLayout input, IDictionary<string, ControlFlowFunction>? ownedGraphs = null,
        IReadOnlyDictionary<string, string>? deferred = null)
    {
        var pass = new SpirvUniformAccessLowering(input);
        return pass.converted.Count == 0 ? input : pass.Run(ownedGraphs, deferred);
    }

    private SpirvPhysicalLayout Run(IDictionary<string, ControlFlowFunction>? ownedGraphs, IReadOnlyDictionary<string, string>? deferred)
    {
        var input = layout.Module;
        names.UnionWith(input.Globals.Select(g => g.Name).Concat(input.Constants.Select(c => c.Name))
            .Concat(input.Structures.Select(s => s.Name)).Concat(input.Functions.Select(f => f.Name)));
        void Reserve(Block body) {
            foreach (var statement in body.Statements) switch (statement) {
                case Statement.Declare d: names.Add(d.Name); break;
                case Statement.Nested n: Reserve(n.Body); break;
                case Statement.If i: Reserve(i.Accept); Reserve(i.Reject); break;
                case Statement.Loop l: Reserve(l.Body); Reserve(l.Continuing); break;
                case Statement.Switch s: foreach (var c in s.Cases) Reserve(c.Body); break;
            }
        }
        foreach (var f in input.Functions) {
            names.UnionWith(f.Arguments.Select(a => a.Name));
            if (ownedGraphs?.TryGetValue(f.Name, out var graph) == true) {
                foreach (var instruction in graph.Blocks.SelectMany(b => b.Instructions))
                    if (instruction.Operation is ValueOperation.Local local) names.Add(local.Name);
                    else if (instruction.Operation is ValueOperation.Let let) names.Add(let.Name);
            }
            else Reserve(f.Body);
        }
        output.VulkanMemoryModel = input.VulkanMemoryModel; output.WorkgroupInitializationRequired = input.WorkgroupInitializationRequired;
        output.Structures.AddRange(input.Structures); output.Constants.AddRange(input.Constants);
        output.Globals.AddRange(input.Globals.Select(g => converted.ContainsKey(g.Name) ? g with { Type = layout.Globals[g.Name].PhysicalType } : g));
        output.Enables.UnionWith(input.Enables); output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        foreach (var function in input.Functions) {
            if (ownedGraphs?.TryGetValue(function.Name, out var graph) == true) {
                RunGraph(graph, ownedGraphs); output.Functions.Add(function); continue;
            }
            scopes.Push(function.Arguments.ToDictionary(a => a.Name, _ => (Location?)null, StringComparer.Ordinal)); changed = false;
            var body = Body(function.Body);
            if (!changed) output.Functions.Add(function);
            else {
                var copy = new ShaderFunction(function.Name) { Body = body, Stage = function.Stage, ReturnType = function.ReturnType,
                    ReturnBinding = function.ReturnBinding, WorkgroupSize = function.WorkgroupSize.ToArray(), TaskPayload = function.TaskPayload,
                    MeshOutput = function.MeshOutput, EarlyDepthTest = function.EarlyDepthTest, ConservativeDepth = function.ConservativeDepth };
                copy.Arguments.AddRange(function.Arguments); copy.DiagnosticFilters.AddRange(function.DiagnosticFilters); output.Functions.Add(copy);
            }
            scopes.Pop();
        }
        output.Functions.AddRange(helpers);
        foreach (var helper in helpers) {
            if (ownedGraphs?.ContainsKey(helper.Name) == true) continue;
            if (!StructuredControlFlowReader.TryRead(helper, output, out var graph, out var helperDeferred))
                throw Error("Uniform access legalization requires canonical verification: " + helperDeferred);
            ControlFlowAnalysis.RemoveUnreachable(graph!);
            ControlFlowVerifier.Validate(graph!, output); LocalValuePromotion.Run(graph!); ControlFlowVerifier.Validate(graph!, output);
            ownedGraphs?.Add(helper.Name, graph!);
        }
        if (ownedGraphs is not null) {
            var canonical = new CanonicalModule(output, ownedGraphs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
                deferred ?? output.Functions.Where(f => !ownedGraphs.ContainsKey(f.Name)).ToDictionary(f => f.Name, _ => "uniform target structured adapter", StringComparer.Ordinal),
                output.Functions.Where(f => f.Stage is not null).Select(f => f.Name).ToHashSet(StringComparer.Ordinal));
            var effects = ShaderEffectAnalysis.Compute(canonical);
            foreach (var graph in ownedGraphs.Values)
                foreach (var block in graph.Blocks)
                    for (int i = 0; i < block.Instructions.Count; i++)
                        if (block.Instructions[i].Operation is ValueOperation.Call call)
                            block.Instructions[i] = block.Instructions[i] with { Operation = call with { CalleeEffects = call.CalleeEffects | effects[call.Function] } };
            ControlFlowVerifier.Validate(canonical);
        }
        return layout with { Module = output };
    }

    private Location? Place(Expression expression)
    {
        switch (expression) {
            case Expression.Reference r:
                foreach (var scope in scopes) if (scope.TryGetValue(r.Name, out var local)) return local;
                return converted.TryGetValue(r.Name, out var global) ? new(global, []) : null;
            case Expression.Unary { Operator: "&" or "*" } u: return Place(u.Operand);
            case Expression.Member m when Place(m.Base) is { } parent:
                return parent with { Steps = parent.Steps.Append(new(m.Name, null, null)).ToArray() };
            case Expression.Access a when Place(a.Base) is { } parent:
                uint? constant = ConstantEvaluator.TryEvaluateRuntime(a.Index, out var value) && value is Expression.Literal literal
                    && literal.Value is uint or int && System.Convert.ToInt64(literal.Value) >= 0 ? System.Convert.ToUInt32(literal.Value) : null;
                return parent with { Steps = parent.Steps.Append(new(null, a.Index, constant)).ToArray() };
            default: return null;
        }
    }

    private Expression Expr(Expression expression)
    {
        if (expression is Expression.Unary { Operator: "&" } address && Place(address) is not null)
            throw Error("Passing a uniform pointer requiring matrix layout conversion is not supported yet.", address.Span);
        if (expression is Expression.Load load && Place(load.Pointer) is { } explicitRead)
            return ReadValue(explicitRead, load.Type, load.MemoryAccess, load.Span);
        if (expression is Expression.Reference r && Alias(r.Name))
            throw Error("Converted uniform pointer requires a direct dereference or alias.", r.Span);
        if (expression is Expression.Reference or Expression.Member or Expression.Access or Expression.Unary { Operator: "*" }
            && Place(expression) is { } implicitRead) return ReadValue(implicitRead, Data(expression.Type), null, expression.Span);
        return expression switch {
            Expression.Load l => l with { Pointer = Address(l.Pointer) }, Expression.Unary u => u with { Operand = Expr(u.Operand) },
            Expression.Binary b => b with { Left = Expr(b.Left), Right = Expr(b.Right) },
            Expression.Call c => c with { Arguments = c.Arguments.Select(Expr).ToArray() },
            Expression.Construct c => c with { Components = c.Components.Select(Expr).ToArray() }, Expression.Convert c => c with { Operand = Expr(c.Operand) },
            Expression.Access a => a with { Base = Expr(a.Base), Index = Expr(a.Index) }, Expression.Member m => m with { Base = Expr(m.Base) },
            Expression.Swizzle s => s with { Vector = Expr(s.Vector) },
            Expression.Select s => s with { Condition = Expr(s.Condition), Accept = Expr(s.Accept), Reject = Expr(s.Reject) }, _ => expression
        };
    }

    private bool Alias(string name)
    {
        foreach (var scope in scopes) if (scope.TryGetValue(name, out var local)) return local is not null;
        return false;
    }

    private Expression Address(Expression expression) => expression switch {
        Expression.Access a => a with { Base = Address(a.Base), Index = Expr(a.Index) }, Expression.Member m => m with { Base = Address(m.Base) },
        Expression.Unary { Operator: "*" } u => u with { Operand = Expr(u.Operand) }, _ => expression
    };

    private Block Body(Block input, bool scope = true)
    {
        if (scope) scopes.Push(new(StringComparer.Ordinal));
        var outputBody = new Block(); outputBody.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        foreach (var statement in input.Statements) {
            if (statement is Statement.Declare d) {
                if (d.Type is ShaderType.Pointer { Space: AddressSpace.Uniform } && d.Initializer is { } alias && Place(alias) is { } location) {
                    var steps = new List<Step>();
                    foreach (var step in location.Steps) {
                        if (step.Index is null) { steps.Add(step); continue; }
                        Expression value = Expr(step.Index); string name = Name("sia_spv_uniform_index_");
                        outputBody.Statements.Add(new Statement.Declare(name, value.Type, value, false) { Span = step.Index.Span });
                        steps.Add(step with { Index = new Expression.Reference(name, value.Type) { Span = step.Index.Span } });
                    }
                    scopes.Peek().Add(d.Name, location with { Steps = steps }); changed = true; continue;
                }
                outputBody.Statements.Add(d with { Initializer = d.Initializer is null ? null : Expr(d.Initializer) });
                scopes.Peek().Add(d.Name, null); continue;
            }
            outputBody.Statements.Add(statement switch {
                Statement.Store s => s with { Target = Address(s.Target), Value = Expr(s.Value) }, Statement.Evaluate e => e with { Value = Expr(e.Value) },
                Statement.Return r => r with { Value = r.Value is null ? null : Expr(r.Value) }, Statement.Nested n => n with { Body = Body(n.Body) },
                Statement.If i => i with { Condition = Expr(i.Condition), Accept = Body(i.Accept), Reject = Body(i.Reject) },
                Statement.Loop l => l with { Body = LoopBody(l.Body, l.Continuing, l.BreakIf, out var continuing, out var breakIf), Continuing = continuing, BreakIf = breakIf },
                Statement.Switch s => s with { Selector = Expr(s.Selector), Cases = s.Cases.Select(c => c with { Body = Body(c.Body) }).ToArray() }, _ => statement
            });
        }
        if (scope) scopes.Pop(); return outputBody;
    }

    private Block LoopBody(Block body, Block continuing, Expression? breakIf, out Block mappedContinuing, out Expression? mappedBreakIf)
    {
        scopes.Push(new(StringComparer.Ordinal));
        var mapped = Body(body, scope: false);
        scopes.Push(new(StringComparer.Ordinal));
        mappedContinuing = Body(continuing, scope: false); mappedBreakIf = breakIf is null ? null : Expr(breakIf);
        scopes.Pop(); scopes.Pop(); return mapped;
    }

    private Expression ReadValue(Location location, ShaderType type, SpirvMemoryAccess? memory, SourceSpan span)
    {
        changed = true;
        var helper = new ShaderFunction(Name("sia_spv_uniform_read_")) { ReturnType = type };
        var arguments = new List<Expression>(); var steps = new List<Step>();
        foreach (var step in location.Steps) {
            if (step.Index is null || step.Constant is not null) { steps.Add(step); continue; }
            Expression argument = Expr(step.Index); string name = Name("sia_spv_uniform_arg_");
            helper.Arguments.Add(new(name, argument.Type)); arguments.Add(argument);
            steps.Add(step with { Index = new Expression.Reference(name, argument.Type) { Span = step.Index.Span } });
        }
        ShaderType physical = layout.Globals[location.Root.Name].PhysicalType;
        Expression root = new Expression.Reference(location.Root.Name, Pointer(physical)) { Span = span };
        Read path = Path(root, location.Root.Type, physical, steps, 0, memory, location.Root.MemoryDecorations, type);
        helper.Body.Statements.Add(new Statement.Return(EmitRead(path, helper.Body)) { Span = span });
        helpers.Add(helper);
        return new Expression.Call(helper.Name, arguments, type) { Binding = CallBinding.Function, Span = span };
    }

    private static Expression Child(Expression pointer, ShaderType type, Expression index) => new Expression.Access(pointer, index, Pointer(type)) { Span = pointer.Span };
    private static Expression Field(Expression pointer, StructMember member) => new Expression.Member(pointer, member.Name, Pointer(member.Type)) { Span = pointer.Span };
    private bool Flatten(ShaderType.Matrix matrix) => layout.FlattenedUniformMatrices.Contains(matrix);
    private Read Select(Step step, int count, Func<int, Read> read, ShaderType type)
        => step.Constant is uint index && index < count ? read((int)index)
            : new SelectedRead(step.Index!, Enumerable.Range(0, count).Select(read).ToArray(), type);

    private Read Path(Expression pointer, ShaderType logical, ShaderType physical, IReadOnlyList<Step> steps, int depth,
        SpirvMemoryAccess? memory, MemoryDecorations inherited, ShaderType result)
    {
        if (depth == steps.Count) return new ValueRead(Load(pointer, logical, physical, memory, inherited));
        var step = steps[depth];
        if (step.Member is string name && logical is ShaderType.Structure structure && physical is ShaderType.Structure mapped) {
            int index = structure.Members.ToList().FindIndex(m => m.Name == name), field = layout.UniformFields[structure][index];
            var member = structure.Members[index]; inherited |= member.MemoryDecorations;
            if (member.Type is ShaderType.Matrix matrix && Flatten(matrix)) {
                if (depth + 1 == steps.Count) return new ValueRead(new Expression.Construct(matrix, Enumerable.Range(0, matrix.Columns)
                    .Select(c => Load(Field(pointer, mapped.Members[field + c]), mapped.Members[field + c].Type,
                        mapped.Members[field + c].Type, memory?.Leaf(), inherited)).ToArray()));
                return Select(steps[depth + 1], matrix.Columns, c => Path(Field(pointer, mapped.Members[field + c]), mapped.Members[field + c].Type,
                    mapped.Members[field + c].Type, steps, depth + 2, memory, inherited, result), result);
            }
            return Path(Field(pointer, mapped.Members[field]), member.Type, mapped.Members[field].Type, steps, depth + 1, memory, inherited, result);
        }
        if (logical is ShaderType.Matrix matrixType && physical is ShaderType.Structure columns)
            return Select(step, matrixType.Columns, c => Path(Field(pointer, columns.Members[c]), columns.Members[c].Type,
                columns.Members[c].Type, steps, depth + 1, memory, inherited, result), result);
        (ShaderType element, ShaderType mappedElement) = (logical, physical) switch {
            (ShaderType.Array a, ShaderType.Array b) => (a.Element, b.Element),
            (ShaderType.BindingArray a, ShaderType.BindingArray b) => (a.Element, b.Element),
            (ShaderType.Matrix a, ShaderType.Matrix b) => (new ShaderType.Vector(a.Rows, a.Component), new ShaderType.Vector(b.Rows, b.Component)),
            (ShaderType.Vector a, ShaderType.Vector b) => (a.Component, b.Component), _ => throw Error("Uniform access path has incompatible types.", pointer.Span)
        };
        Expression indexValue = step.Constant is uint fixedIndex ? new Expression.Literal(step.Index!.Type == ShaderType.I32 ? (object)(int)fixedIndex : fixedIndex, step.Index.Type) : step.Index!;
        return Path(Child(pointer, mappedElement, indexValue), element, mappedElement, steps, depth + 1, memory, inherited, result);
    }

    private Expression Load(Expression pointer, ShaderType logical, ShaderType physical, SpirvMemoryAccess? memory, MemoryDecorations inherited)
    {
        if (logical == physical) return new Expression.Load(pointer) { MemoryAccess = ShaderMemoryRequirements.Decorate(memory,
            inherited | ShaderMemoryRequirements.TypeMemory(logical), AddressSpace.Uniform, VulkanMemory), Span = pointer.Span };
        memory = memory?.Leaf();
        if (logical is ShaderType.Matrix matrix && physical is ShaderType.Structure columns)
            return new Expression.Construct(matrix, columns.Members.Select(m => Load(Field(pointer, m), m.Type, m.Type, memory, inherited)).ToArray());
        if (logical is ShaderType.Array array && physical is ShaderType.Array mapped) {
            if (array.Length is not uint length) throw Error("Uniform arrays must be statically sized.", pointer.Span);
            return new Expression.Construct(logical, Enumerable.Range(0, checked((int)length))
                .Select(i => Load(Child(pointer, mapped.Element, Expression.U32((uint)i)), array.Element, mapped.Element, memory, inherited)).ToArray());
        }
        if (logical is ShaderType.Structure structure && physical is ShaderType.Structure mappedStructure) {
            var values = new List<Expression>(); int field = 0;
            foreach (var member in structure.Members) {
                if (member.Type is ShaderType.Matrix m && Flatten(m)) {
                    var columnValues = new List<Expression>();
                    for (int c = 0; c < m.Columns; c++) {
                        var column = mappedStructure.Members[field++]; columnValues.Add(Load(Field(pointer, column), column.Type, column.Type, memory, inherited | member.MemoryDecorations));
                    }
                    values.Add(new Expression.Construct(m, columnValues));
                }
                else {
                    var selected = mappedStructure.Members[field++];
                    values.Add(Load(Field(pointer, selected), member.Type, selected.Type, memory, inherited | member.MemoryDecorations));
                }
            }
            return new Expression.Construct(logical, values);
        }
        throw Error("Uniform layout conversion has incompatible types.", pointer.Span);
    }

    private Expression EmitRead(Read read, Block body)
    {
        if (read is ValueRead direct) return direct.Value;
        var selected = (SelectedRead)read; string name = Name("sia_spv_uniform_result_");
        var result = new Expression.Reference(name, selected.Type);
        body.Statements.Add(new Statement.Declare(name, selected.Type, null) { Initialize = false });
        var cases = new List<SwitchCase>();
        for (int i = 0; i < selected.Arms.Count; i++) {
            var arm = new Block(); Expression value = EmitRead(selected.Arms[i], arm);
            arm.Statements.Add(new Statement.Store(result, value)); arm.Statements.Add(new Statement.Break());
            var literal = selected.Index.Type == ShaderType.I32 ? Expression.I32(i) : Expression.U32((uint)i);
            cases.Add(new([literal], false, arm));
        }
        var fallback = new Block(); fallback.Statements.Add(new Statement.Store(result, new Expression.Construct(selected.Type, []))); fallback.Statements.Add(new Statement.Break());
        cases.Add(new([], true, fallback)); body.Statements.Add(new Statement.Switch(selected.Index, cases)); return result;
    }
}
