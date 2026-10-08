using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Proc;

/// <summary>Recover query descriptor fields that WGSL cannot read back from a handle.</summary>
internal sealed class QueryStateLowering
{
    private readonly HashSet<string> names = new(StringComparer.Ordinal);
    private readonly Stack<Dictionary<string, Query?>> scopes = new();
    private int next;
    private sealed record Query(Expression.Reference Min, Expression.Reference Flags);
    private static bool StateGetter(Expression e) => e is Expression.Call { Function: "spirvRayQueryGetRayTMinKHR" or "spirvRayQueryGetRayFlagsKHR" };
    public static Module Run(Module input)
    {
        var pass = new QueryStateLowering(); var output = new Module { VulkanMemoryModel = input.VulkanMemoryModel, WorkgroupInitializationRequired = input.WorkgroupInitializationRequired };
        output.Enables.UnionWith(input.Enables); output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        output.Structures.AddRange(input.Structures); output.Constants.AddRange(input.Constants); output.Globals.AddRange(input.Globals);
        foreach (var name in input.Constants.Select(c => c.Name).Concat(input.Globals.Select(g => g.Name))
            .Concat(input.Structures.Select(s => s.Name)).Concat(input.Functions.Select(f => f.Name))) pass.names.Add(name);
        foreach (var f in input.Functions) { foreach (var a in f.Arguments) pass.names.Add(a.Name); pass.Visit(f.Body, e => false, s => { if (s is Statement.Declare d) pass.names.Add(d.Name); }); }
        foreach (var f in input.Functions)
        {
            if (!pass.Visit(f.Body, StateGetter)) { output.Functions.Add(f); continue; }
            // A high-level initialization can be rejected by WGSL's robustness
            // guards, leaving the previous native query state intact. Recording
            // its descriptor unconditionally would therefore be incorrect.
            if (pass.Visit(f.Body, e => e is Expression.Call { Function: "rayQueryInitialize" }))
                throw new ShaderException(DiagnosticStage.WgslWrite,
                    "Mixed high-level initialization and raw query state getters require guarded state tracking.");
            pass.scopes.Push(new(StringComparer.Ordinal));
            var copy = new ShaderFunction(f.Name) { Stage = f.Stage, ReturnType = f.ReturnType, ReturnBinding = f.ReturnBinding,
                TaskPayload = f.TaskPayload, MeshOutput = f.MeshOutput,
                WorkgroupSize = f.WorkgroupSize.ToArray(), EarlyDepthTest = f.EarlyDepthTest, ConservativeDepth = f.ConservativeDepth, Body = pass.Body(f.Body) };
            copy.Arguments.AddRange(f.Arguments); copy.DiagnosticFilters.AddRange(f.DiagnosticFilters); output.Functions.Add(copy);
            pass.scopes.Pop();
        }
        return output;
    }
    private string Fresh() { string name; do name = "naga_query_state_" + next++; while (!names.Add(name)); return name; }
    private static Expression.Reference Place(string name, ShaderType type) => new(name, new ShaderType.Pointer(type, AddressSpace.Function));
    private Query? Root(Expression e) => e switch
    {
        Expression.Unary { Operator: "&" or "*" } u => Root(u.Operand),
        Expression.Reference r => scopes.Select(s => s.TryGetValue(r.Name, out var q) ? (Found: true, Query: q) : (false, null)).FirstOrDefault(q => q.Found).Query,
        _ => null
    };
    private Expression Expr(Expression e)
    {
        if (StateGetter(e))
        {
            var call = (Expression.Call)e;
            var query = Root(call.Arguments[0]) ?? throw new ShaderException(DiagnosticStage.WgslWrite, "Query state getter requires a locally tracked query after helper inlining.");
            return new Expression.Load(call.Function == "spirvRayQueryGetRayTMinKHR" ? query.Min : query.Flags) { Span = e.Span };
        }
        return e switch
        {
            Expression.Load l => l with { Pointer = Expr(l.Pointer) }, Expression.Unary u => u with { Operand = Expr(u.Operand) },
            Expression.Binary b => b with { Left = Expr(b.Left), Right = Expr(b.Right) }, Expression.Convert c => c with { Operand = Expr(c.Operand) },
            Expression.Construct c => c with { Components = c.Components.Select(Expr).ToArray() }, Expression.Call c => c with { Arguments = c.Arguments.Select(Expr).ToArray() },
            Expression.Access a => a with { Base = Expr(a.Base), Index = Expr(a.Index) }, Expression.Member m => m with { Base = Expr(m.Base) },
            Expression.Swizzle s => s with { Vector = Expr(s.Vector) }, Expression.Select s => new Expression.Select(Expr(s.Condition), Expr(s.Accept), Expr(s.Reject)), _ => e
        };
    }
    private Block Body(Block input, bool scope = true)
    {
        if (scope) scopes.Push(new(StringComparer.Ordinal)); var output = new Block();
        foreach (var s in input.Statements)
        {
            if (s is Statement.Declare d)
            {
                var initializer = d.Initializer is null ? null : Expr(d.Initializer);
                Query? alias = d.Type is ShaderType.Pointer { Base: ShaderType.RayQuery } && d.Initializer is not null ? Root(d.Initializer) : null;
                output.Statements.Add(d with { Initializer = initializer });
                if (d.Type is ShaderType.RayQuery)
                {
                    string min = Fresh(), flags = Fresh(); alias = new(Place(min, ShaderType.F32), Place(flags, ShaderType.U32));
                    output.Statements.Add(new Statement.Declare(min, ShaderType.F32, new Expression.Literal(0f, ShaderType.F32)));
                    output.Statements.Add(new Statement.Declare(flags, ShaderType.U32, Expression.U32(0)));
                }
                scopes.Peek()[d.Name] = alias; continue;
            }
            if (s is Statement.Evaluate { Value: Expression.Call { Function: "spirvRayQueryInitializeKHR" } initialize })
            {
                var query = Root(initialize.Arguments[0]) ?? throw new ShaderException(DiagnosticStage.WgslWrite, "Raw query initialization requires a tracked local query.");
                var args = new List<Expression>();
                foreach (var argument in initialize.Arguments)
                {
                    string name = Fresh(); var value = Expr(argument);
                    output.Statements.Add(new Statement.Declare(name, value.Type, value, false)); args.Add(new Expression.Reference(name, value.Type));
                }
                output.Statements.Add(new Statement.Evaluate(initialize with { Arguments = args }));
                output.Statements.Add(new Statement.Store(query.Min, args[5])); output.Statements.Add(new Statement.Store(query.Flags, args[2])); continue;
            }
            Statement mapped;
            if (s is Statement.Loop l)
            {
                scopes.Push(new(StringComparer.Ordinal)); var body = Body(l.Body, false); scopes.Push(new(StringComparer.Ordinal)); var tail = Body(l.Continuing, false);
                var breakIf = l.BreakIf is null ? null : Expr(l.BreakIf); scopes.Pop(); scopes.Pop(); mapped = new Statement.Loop(body, tail, breakIf);
            }
            else mapped = s switch
            {
                Statement.Nested n => new Statement.Nested(Body(n.Body)), Statement.If i => new Statement.If(Expr(i.Condition), Body(i.Accept), Body(i.Reject)),
                Statement.Switch sw => new Statement.Switch(Expr(sw.Selector), sw.Cases.Select(c => c with { Body = Body(c.Body) }).ToArray()),
                Statement.Store st => st with { Target = Expr(st.Target), Value = Expr(st.Value) }, Statement.Evaluate ev => new Statement.Evaluate(Expr(ev.Value)),
                Statement.Return r => new Statement.Return(r.Value is null ? null : Expr(r.Value)), _ => s
            };
            output.Statements.Add(mapped with { Span = s.Span });
        }
        if (scope) scopes.Pop(); return output;
    }
    private bool Visit(Block body, Func<Expression, bool> match, Action<Statement>? statement = null)
    {
        bool Expr(Expression e) => match(e) || e switch
        {
            Expression.Load l => Expr(l.Pointer), Expression.Unary u => Expr(u.Operand), Expression.Binary b => Expr(b.Left) || Expr(b.Right),
            Expression.Convert c => Expr(c.Operand), Expression.Call c => c.Arguments.Any(Expr), Expression.Construct c => c.Components.Any(Expr),
            Expression.Access a => Expr(a.Base) || Expr(a.Index), Expression.Member m => Expr(m.Base), Expression.Swizzle s => Expr(s.Vector),
            Expression.Select s => Expr(s.Condition) || Expr(s.Accept) || Expr(s.Reject), _ => false
        };
        bool found = false;
        foreach (var s in body.Statements)
        {
            statement?.Invoke(s);
            found |= s switch
            {
                Statement.Declare d => d.Initializer is not null && Expr(d.Initializer), Statement.Store st => Expr(st.Target) || Expr(st.Value),
                Statement.Evaluate ev => Expr(ev.Value), Statement.Return r => r.Value is not null && Expr(r.Value),
                Statement.Nested n => Visit(n.Body, match, statement), Statement.If i => Expr(i.Condition) | Visit(i.Accept, match, statement) | Visit(i.Reject, match, statement),
                Statement.Loop l => Visit(l.Body, match, statement) | Visit(l.Continuing, match, statement) | (l.BreakIf is not null && Expr(l.BreakIf)),
                Statement.Switch sw => Expr(sw.Selector) | sw.Cases.Aggregate(false, (v, c) => Visit(c.Body, match, statement) | v), _ => false
            };
        }
        return found;
    }
}
