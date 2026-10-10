using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Keep resolved builtins visible in WGSL's lexical namespace; entry names remain part of the consumer contract.</summary>
internal static class WgslBuiltinNameLowering
{
    public static Module Run(Module input)
    {
        var builtins = new HashSet<string>(StringComparer.Ordinal);
        var names = input.Functions.Select(f => f.Name).Concat(input.Globals.Select(g => g.Name))
            .Concat(input.Constants.Select(c => c.Name)).Concat(input.Structures.Select(s => s.Name)).ToHashSet(StringComparer.Ordinal);
        void Scan(Expression e) {
            if (e is Expression.Call { Binding: CallBinding.Builtin } call) builtins.Add(call.Function);
            foreach (var child in e switch {
                Expression.Call c => c.Arguments, Expression.Construct c => c.Components, Expression.Load l => [l.Pointer],
                Expression.Unary u => [u.Operand], Expression.Binary b => [b.Left, b.Right], Expression.Convert c => [c.Operand],
                Expression.Access a => [a.Base, a.Index], Expression.Member m => [m.Base], Expression.Swizzle s => [s.Vector],
                Expression.Select s => [s.Condition, s.Accept, s.Reject], _ => (IEnumerable<Expression>)[] }) Scan(child);
        }
        void ScanBody(Block body) {
            foreach (var statement in body.Statements) switch (statement) {
                case Statement.Declare d: names.Add(d.Name); if (d.Initializer is { } value) Scan(value); break;
                case Statement.Store s: Scan(s.Target); Scan(s.Value); break;
                case Statement.Evaluate e: Scan(e.Value); break;
                case Statement.Return { Value: { } returned }: Scan(returned); break;
                case Statement.Nested n: ScanBody(n.Body); break;
                case Statement.If i: Scan(i.Condition); ScanBody(i.Accept); ScanBody(i.Reject); break;
                case Statement.Loop l: ScanBody(l.Body); ScanBody(l.Continuing); if (l.BreakIf is { } condition) Scan(condition); break;
                case Statement.Switch s: Scan(s.Selector); foreach (var arm in s.Cases) ScanBody(arm.Body); break;
            }
        }
        foreach (var function in input.Functions) { names.UnionWith(function.Arguments.Select(a => a.Name)); ScanBody(function.Body); }
        foreach (var constant in input.Constants) if (constant.Value is { } value) Scan(value);
        foreach (var global in input.Globals) if (global.Initializer is { } value) Scan(value);
        if (!names.Overlaps(builtins)) return input;
        foreach (var entry in input.Functions.Where(f => f.Stage is not null && builtins.Contains(f.Name)))
            throw new ShaderException(DiagnosticStage.WgslWrite, "Entry name '" + entry.Name + "' shadows a required builtin; WGSL cannot preserve both identities without changing the entry contract.");
        foreach (var constant in input.Constants.Where(c => c.IsOverride && c.OverrideId is null && builtins.Contains(c.Name)))
            throw new ShaderException(DiagnosticStage.WgslWrite, "Name-based override '" + constant.Name + "' shadows a required builtin; WGSL cannot preserve both identities without changing the pipeline constant contract.");
        int next = 0;
        string Fresh(string old) { string name; do name = "sia_wgsl_" + old + "_" + next++; while (!names.Add(name)); return name; }
        var moduleNames = input.Functions.Select(f => f.Name).Concat(input.Globals.Select(g => g.Name))
            .Concat(input.Constants.Select(c => c.Name)).Concat(input.Structures.Select(s => s.Name)).Distinct()
            .Where(builtins.Contains).ToDictionary(n => n, Fresh, StringComparer.Ordinal);
        string ModuleName(string name) => moduleNames.GetValueOrDefault(name, name);
        var types = new Dictionary<ShaderType, ShaderType>();
        ShaderType Type(ShaderType type) {
            if (types.TryGetValue(type, out var known)) return known;
            ShaderType result = type switch {
                ShaderType.Pointer p => p with { Base = Type(p.Base) },
                ShaderType.Array a => a with { Element = Type(a.Element), OverrideLength = a.OverrideLength is { } length ? ModuleName(length) : null },
                ShaderType.BindingArray a => a with { Element = Type(a.Element), OverrideLength = a.OverrideLength is { } length ? ModuleName(length) : null },
                ShaderType.Structure s => s with { Name = ModuleName(s.Name), Members = s.Members.Select(m => m with { Type = Type(m.Type) }).ToArray() }, _ => type
            };
            types.Add(type, result); return result;
        }
        var scopes = new Stack<Dictionary<string, string>>();
        string Reference(string name) { foreach (var scope in scopes) if (scope.TryGetValue(name, out var local)) return local; return ModuleName(name); }
        string Declare(string name) { string renamed = builtins.Contains(name) ? Fresh(name) : name; scopes.Peek()[name] = renamed; return renamed; }
        Expression Expr(Expression e) {
            Expression result = e switch {
                Expression.Reference r => new Expression.Reference(Reference(r.Name), Type(r.Type)),
                Expression.Literal l => new Expression.Literal(l.Value, Type(l.Type)),
                Expression.Call c => new Expression.Call(c.Binding == CallBinding.Builtin ? c.Function : ModuleName(c.Function), c.Arguments.Select(Expr).ToArray(), Type(c.Type), c.Binding)
                                        { AtomicMemory = c.AtomicMemory,MemoryAccess = c.MemoryAccess },
                Expression.Load l => new Expression.Load(Expr(l.Pointer)) { MemoryAccess = l.MemoryAccess },
                Expression.Construct c => new Expression.Construct(Type(c.Type), c.Components.Select(Expr).ToArray()),
                Expression.Convert c => new Expression.Convert(Type(c.Type), Expr(c.Operand), c.Bitcast),
                Expression.Unary u => new Expression.Unary(u.Operator, Expr(u.Operand), Type(u.Type)),
                Expression.Binary b => new Expression.Binary(b.Operator, Expr(b.Left), Expr(b.Right), Type(b.Type)),
                Expression.Access a => new Expression.Access(Expr(a.Base), Expr(a.Index), Type(a.Type)),
                Expression.Member m => new Expression.Member(Expr(m.Base), m.Name, Type(m.Type)),
                Expression.Swizzle s => new Expression.Swizzle(Expr(s.Vector), s.Components, Type(s.Type)),
                Expression.Select s => new Expression.Select(Expr(s.Condition), Expr(s.Accept), Expr(s.Reject)), _ => e
            }; return result with { Span = e.Span };
        }
        Block Body(Block input, bool scoped = true) {
            if (scoped) scopes.Push(new(StringComparer.Ordinal));
            var output = new Block(); output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
            foreach (var s in input.Statements) {
                Statement mapped;
                if (s is Statement.Declare d) {
                    var initializer = d.Initializer is null ? null : Expr(d.Initializer);
                    mapped = d with { Name = Declare(d.Name), Type = Type(d.Type), Initializer = initializer };
                }
                else if (s is Statement.Loop l) {
                    scopes.Push(new(StringComparer.Ordinal)); var body = Body(l.Body, false);
                    scopes.Push(new(StringComparer.Ordinal)); var continuing = Body(l.Continuing, false);
                    mapped = l with { Body = body, Continuing = continuing, BreakIf = l.BreakIf is null ? null : Expr(l.BreakIf) };
                    scopes.Pop(); scopes.Pop();
                }
                else mapped = s switch {
                    Statement.Store store => store with { Target = Expr(store.Target), Value = Expr(store.Value) },
                    Statement.Evaluate e => e with { Value = Expr(e.Value) }, Statement.Return r => r with { Value = r.Value is null ? null : Expr(r.Value) },
                    Statement.Nested n => n with { Body = Body(n.Body) }, Statement.If i => i with { Condition = Expr(i.Condition), Accept = Body(i.Accept), Reject = Body(i.Reject) },
                    Statement.Switch sw => sw with { Selector = Expr(sw.Selector), Cases = sw.Cases.Select(c => c with { Body = Body(c.Body) }).ToArray() }, _ => s
                };
                output.Statements.Add(mapped);
            }
            if (scoped) scopes.Pop(); return output;
        }
        var result = new Module { VulkanMemoryModel = input.VulkanMemoryModel, WorkgroupInitializationRequired = input.WorkgroupInitializationRequired };
        result.Enables.UnionWith(input.Enables); result.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        result.Structures.AddRange(input.Structures.Select(s => (ShaderType.Structure)Type(s)));
        result.Globals.AddRange(input.Globals.Select(g => g with { Name = ModuleName(g.Name), Type = Type(g.Type), Initializer = g.Initializer is null ? null : Expr(g.Initializer) }));
        result.Constants.AddRange(input.Constants.Select(c => c with { Name = ModuleName(c.Name), Type = Type(c.Type), Value = c.Value is null ? null : Expr(c.Value) }));
        foreach (var f in input.Functions) {
            scopes.Push(new(StringComparer.Ordinal));
            var copy = new ShaderFunction(ModuleName(f.Name)) { ReturnType = Type(f.ReturnType), ReturnBinding = f.ReturnBinding, Stage = f.Stage,
                TaskPayload = f.TaskPayload is { } payload ? ModuleName(payload) : null, MeshOutput = f.MeshOutput is { } mesh ? ModuleName(mesh) : null,
                WorkgroupSize = f.WorkgroupSize.Select(Expr).ToArray(), EarlyDepthTest = f.EarlyDepthTest, ConservativeDepth = f.ConservativeDepth };
            copy.Arguments.AddRange(f.Arguments.Select(a => a with { Name = Declare(a.Name), Type = Type(a.Type) }));
            copy.Body = Body(f.Body); copy.DiagnosticFilters.AddRange(f.DiagnosticFilters); scopes.Pop(); result.Functions.Add(copy);
        }
        ModuleValidator.Validate(result); return result;
    }
}
