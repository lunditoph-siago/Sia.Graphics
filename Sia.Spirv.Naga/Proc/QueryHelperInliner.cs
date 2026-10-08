using Sia.Spirv.Naga.IR;

namespace Sia.Spirv.Naga.Proc;

/// <summary>Expand helpers while retaining the identity of their pointer arguments.</summary>
internal sealed class QueryHelperInliner
{
    private readonly Dictionary<string, ShaderFunction> helpers;
    private readonly HashSet<string> names = new(StringComparer.Ordinal);
    private readonly HashSet<string> expanding = new(StringComparer.Ordinal);
    private readonly string kind;
    private int next;
    private QueryHelperInliner(Module module, Func<ShaderFunction, bool> select, string kind)
    {
        this.kind = kind;
        helpers = module.Functions.Where(select).ToDictionary(f => f.Name, StringComparer.Ordinal);
        foreach (var name in module.Constants.Select(c => c.Name).Concat(module.Globals.Select(g => g.Name))
            .Concat(module.Structures.Select(s => s.Name)).Concat(module.Functions.Select(f => f.Name))) names.Add(name);
        foreach (var f in module.Functions)
        {
            foreach (var a in f.Arguments) names.Add(a.Name);
            Reserve(f.Body);
        }
    }
    public static Module Run(Module module) => Run(module,
        f => f.Arguments.Any(a => a.Type is ShaderType.Pointer { Base: ShaderType.RayQuery }), "query");

    // Native derived Storage/Workgroup pointers may require capabilities that
    // WGSL cannot express. Expanding calls also keeps member layout context and
    // aliases intact, without a copy-in/copy-out calling convention.
    public static Module RunNonFunctionPointers(Module module, IReadOnlySet<string>? specialized = null) => Run(module,
        f => f.Stage is null && (f.Arguments.Any(a => a.Type is ShaderType.Pointer { Space: not AddressSpace.Function }) || specialized?.Contains(f.Name) == true), "pointer");

    private static Module Run(Module module, Func<ShaderFunction, bool> select, string kind)
    {
        var pass = new QueryHelperInliner(module, select, kind);
        if (pass.helpers.Count == 0) return module;
        var output = new Module { VulkanMemoryModel = module.VulkanMemoryModel, WorkgroupInitializationRequired = module.WorkgroupInitializationRequired }; output.Enables.UnionWith(module.Enables); output.DiagnosticFilters.AddRange(module.DiagnosticFilters);
        output.Structures.AddRange(module.Structures); output.Constants.AddRange(module.Constants); output.Globals.AddRange(module.Globals);
        foreach (var f in module.Functions.Where(f => !pass.helpers.ContainsKey(f.Name)))
        {
            var copy = new ShaderFunction(f.Name) { Stage = f.Stage, ReturnType = f.ReturnType, ReturnBinding = f.ReturnBinding,
                TaskPayload = f.TaskPayload, MeshOutput = f.MeshOutput,
                WorkgroupSize = f.WorkgroupSize.ToArray(), EarlyDepthTest = f.EarlyDepthTest, ConservativeDepth = f.ConservativeDepth,
                Body = pass.Body(f.Body, new(false)) };
            copy.Arguments.AddRange(f.Arguments); copy.DiagnosticFilters.AddRange(f.DiagnosticFilters); output.Functions.Add(copy);
        }
        return output;
    }
    private ShaderException Error(string message) => new(DiagnosticStage.Validation, message);
    private string Fresh() { string name; do name = "naga_inline_" + next++; while (!names.Add(name)); return name; }
    private void Reserve(Block body)
    {
        foreach (var s in body.Statements)
            switch (s)
            {
                case Statement.Declare d: names.Add(d.Name); break;
                case Statement.Nested n: Reserve(n.Body); break;
                case Statement.If i: Reserve(i.Accept); Reserve(i.Reject); break;
                case Statement.Loop l: Reserve(l.Body); Reserve(l.Continuing); break;
                case Statement.Switch sw: foreach (var c in sw.Cases) Reserve(c.Body); break;
            }
    }
    private sealed class Context(bool rename)
    {
        public bool Rename { get; } = rename;
        public Expression.Reference? ReturnValue, Returned;
        private readonly Stack<Dictionary<string, string>> scopes = new();
        public void Push() => scopes.Push(new(StringComparer.Ordinal));
        public void Pop() => scopes.Pop();
        public void Add(string old, string name) => scopes.Peek().Add(old, name);
        public string Name(string name) { foreach (var scope in scopes) if (scope.TryGetValue(name, out string? result)) return result; return name; }
    }
    private static bool HasReturn(Statement s) => s switch
    {
        Statement.Return => true, Statement.Nested n => n.Body.Statements.Any(HasReturn),
        Statement.If i => i.Accept.Statements.Any(HasReturn) || i.Reject.Statements.Any(HasReturn),
        Statement.Loop l => l.Body.Statements.Any(HasReturn) || l.Continuing.Statements.Any(HasReturn),
        Statement.Switch sw => sw.Cases.Any(c => c.Body.Statements.Any(HasReturn)), _ => false
    };
    private static Expression.Reference Place(string name, ShaderType type) => new(name, new ShaderType.Pointer(type, AddressSpace.Function));
    private Expression Capture(Expression value, Block prelude)
    {
        string name = Fresh(); prelude.Statements.Add(new Statement.Declare(name, value.Type, value, false));
        return new Expression.Reference(name, value.Type);
    }
    private Expression CapturePlace(Expression place, Block prelude)
    {
        if (place is Expression.Access access && access.Base.Type is ShaderType.Pointer { Base: ShaderType.Vector })
        {
            // A vector component is assignable but its address is not a WGSL
            // pointer. Capture the vector address and component index instead.
            Expression vector = CapturePlace(access.Base, prelude);
            Expression index = Capture(access.Index, prelude);
            return access with { Base = vector, Index = index };
        }
        ShaderType pointer = place.Type is ShaderType.Pointer ? place.Type : new ShaderType.Pointer(place.Type, AddressSpace.Function);
        var address = Capture(new Expression.Unary("&", place, pointer), prelude);
        return new Expression.Unary("*", address, pointer);
    }
    private Block Body(Block input, Context context, bool scope = true)
    {
        if (scope) context.Push(); var output = new Block();
        foreach (var statement in input.Statements)
        {
            Statement mapped;
            Expression E(Expression e) => Expr(e, context, output);
            switch (statement)
            {
                case Statement.Declare d:
                    Expression? initializer = d.Initializer is null ? null : E(d.Initializer);
                    string name = context.Rename ? Fresh() : d.Name; context.Add(d.Name, name);
                    mapped = new Statement.Declare(name, d.Type, initializer, d.Mutable); break;
                case Statement.Store s:
                    Expression target = E(s.Target); var valuePrelude = new Block(); Expression value = Expr(s.Value, context, valuePrelude);
                    if (valuePrelude.Statements.Count != 0) target = CapturePlace(target, output);
                    output.Statements.AddRange(valuePrelude.Statements); mapped = s with { Target = target, Value = value }; break;
                case Statement.Evaluate e:
                    var evaluated = E(e.Value); if (evaluated is Expression.Call) mapped = new Statement.Evaluate(evaluated); else continue;
                    break;
                case Statement.Nested n: mapped = new Statement.Nested(Body(n.Body, context)); break;
                case Statement.If i: mapped = new Statement.If(E(i.Condition), Body(i.Accept, context), Body(i.Reject, context)); break;
                case Statement.Switch sw:
                    mapped = new Statement.Switch(E(sw.Selector), sw.Cases.Select(c => c with { Body = Body(c.Body, context) }).ToArray()); break;
                case Statement.Loop l:
                    context.Push(); Block body = Body(l.Body, context, false); context.Push(); Block tail = Body(l.Continuing, context, false);
                    Expression? breakIf = l.BreakIf is null ? null : Expr(l.BreakIf, context, tail);
                    context.Pop(); context.Pop(); mapped = new Statement.Loop(body, tail, breakIf); break;
                case Statement.Return r when context.Returned is not null:
                    if (r.Value is not null) output.Statements.Add(new Statement.Store(context.ReturnValue!, E(r.Value)));
                    output.Statements.Add(new Statement.Store(context.Returned, Expression.Bool(true))); mapped = new Statement.Break(); break;
                case Statement.Return r: mapped = new Statement.Return(r.Value is null ? null : E(r.Value)); break;
                default: mapped = statement; break;
            }
            output.Statements.Add(mapped with { Span = statement.Span });
            if (context.Returned is not null && mapped is Statement.Nested or Statement.If or Statement.Switch or Statement.Loop)
                output.Statements.Add(ReturnBreak(context.Returned));
        }
        if (scope) context.Pop(); return output;
    }
    private static Statement.If ReturnBreak(Expression returned)
    {
        var exit = new Block(); exit.Statements.Add(new Statement.Break());
        return new(new Expression.Load(returned), exit, new());
    }
    private Expression Inline(Expression.Call call, Expression[] args, Block prelude)
    {
        var f = helpers[call.Function];
        if (!expanding.Add(f.Name)) throw Error($"Recursive {kind} helper calls cannot be inlined.");
        if (f.Stage is not null || args.Length != f.Arguments.Count) throw Error($"Invalid {kind} helper call.");
        if (f.DiagnosticFilters.Count != 0) throw Error($"{char.ToUpperInvariant(kind[0]) + kind[1..]} helper diagnostic filters require block diagnostic lowering before inlining.");
        var context = new Context(true); context.Push(); var body = new Block();
        for (int i = 0; i < args.Length; i++)
        {
            string name = Fresh(); context.Add(f.Arguments[i].Name, name);
            body.Statements.Add(new Statement.Declare(name, args[i].Type, args[i], false));
        }
        Expression.Reference? result = null;
        if (f.ReturnType is not ShaderType.Void)
        {
            string name = Fresh(); result = Place(name, f.ReturnType);
            prelude.Statements.Add(new Statement.Declare(name, f.ReturnType, null));
        }
        bool tailOnly = !f.Body.Statements.Take(System.Math.Max(0, f.Body.Statements.Count - 1)).Any(HasReturn)
            && (f.Body.Statements.LastOrDefault() is Statement.Return || !f.Body.Statements.Any(HasReturn));
        if (tailOnly)
        {
            var prefix = new Block(); prefix.Statements.AddRange(f.Body.Statements.Where((s, i) => i != f.Body.Statements.Count - 1 || s is not Statement.Return));
            body.Statements.AddRange(Body(prefix, context, false).Statements);
            if (f.Body.Statements.LastOrDefault() is Statement.Return { Value: not null } ret)
                body.Statements.Add(new Statement.Store(result!, Expr(ret.Value, context, body)));
            prelude.Statements.Add(new Statement.Nested(body));
        }
        else
        {
            string name = Fresh(); context.Returned = Place(name, ShaderType.Bool); context.ReturnValue = result;
            body.Statements.Add(new Statement.Declare(name, ShaderType.Bool, Expression.Bool(false)));
            // A one-arm switch provides the exit target without adding a loop.
            // WGSL permits switches, but not loops, inside continuing blocks.
            var region = Body(f.Body, context, false); region.Statements.Add(new Statement.Break());
            body.Statements.Add(new Statement.Switch(Expression.U32(0), [new([], true, region)]));
            prelude.Statements.Add(new Statement.Nested(body));
        }
        context.Pop(); expanding.Remove(f.Name);
        return result is null ? new Expression.Construct(new ShaderType.Void(), []) : new Expression.Load(result);
    }
    private Expression[] Values(IReadOnlyList<Expression> values, Context context, Block prelude, bool freeze = false)
    {
        var items = values.Select(e => { var p = new Block(); return (Value: Expr(e, context, p), Prelude: p); }).ToArray();
        bool capture = freeze || items.Any(i => i.Prelude.Statements.Count != 0);
        return items.Select(item => { prelude.Statements.AddRange(item.Prelude.Statements); return capture ? Capture(item.Value, prelude) : item.Value; }).ToArray();
    }
    private Expression Expr(Expression input, Context context, Block prelude)
    {
        Expression E(Expression e) => Expr(e, context, prelude);
        Expression result;
        switch (input)
        {
            case Expression.Reference r: result = r with { Name = context.Name(r.Name) }; break;
            case Expression.Literal: result = input; break;
            case Expression.Load l: result = l with { Pointer = E(l.Pointer) }; break;
            case Expression.Unary u: result = u with { Operand = E(u.Operand) }; break;
            case Expression.Convert c: result = c with { Operand = E(c.Operand) }; break;
            case Expression.Member m: result = m with { Base = E(m.Base) }; break;
            case Expression.Swizzle s: result = s with { Vector = E(s.Vector) }; break;
            case Expression.Construct c: result = c with { Components = Values(c.Components, context, prelude) }; break;
            case Expression.Call c:
                var args = Values(c.Arguments, context, prelude, helpers.ContainsKey(c.Function));
                result = helpers.ContainsKey(c.Function) ? Inline(c, args, prelude) : c with { Arguments = args }; break;
            case Expression.Binary b:
            {
                var left = E(b.Left); var rightPrelude = new Block(); var right = Expr(b.Right, context, rightPrelude);
                if (rightPrelude.Statements.Count == 0) { result = b with { Left = left, Right = right }; break; }
                if (b.Operator is "&&" or "||")
                {
                    string name = Fresh(); var place = Place(name, ShaderType.Bool);
                    prelude.Statements.Add(new Statement.Declare(name, ShaderType.Bool, left));
                    rightPrelude.Statements.Add(new Statement.Store(place, right));
                    prelude.Statements.Add(new Statement.If(b.Operator == "&&" ? new Expression.Load(place) : new Expression.Unary("!", new Expression.Load(place), ShaderType.Bool), rightPrelude, new()));
                    result = new Expression.Load(place);
                }
                else { left = Capture(left, prelude); prelude.Statements.AddRange(rightPrelude.Statements); result = b with { Left = left, Right = right }; }
                break;
            }
            case Expression.Access a:
            {
                Expression parent = E(a.Base); var indexPrelude = new Block(); var index = Expr(a.Index, context, indexPrelude);
                if (indexPrelude.Statements.Count != 0) parent = parent.Type is ShaderType.Pointer ? CapturePlace(parent, prelude) : Capture(parent, prelude);
                prelude.Statements.AddRange(indexPrelude.Statements); result = a with { Base = parent, Index = index }; break;
            }
            case Expression.Select s:
                var selected = Values([s.Condition, s.Accept, s.Reject], context, prelude);
                result = new Expression.Select(selected[0], selected[1], selected[2]); break;
            default: throw Error($"Unknown expression in {kind} helper inlining.");
        }
        return result with { Span = input.Span };
    }
}
