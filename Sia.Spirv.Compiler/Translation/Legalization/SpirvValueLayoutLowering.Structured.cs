using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Legalization;

internal sealed partial class SpirvValueLayoutLowering
{
    private readonly Stack<HashSet<string>> scopes = [];
    private AddressSpace? PlaceSpace(Expression e) => e.Type is ShaderType.Pointer p ? p.Space : e switch {
        Expression.Reference r when !scopes.Any(s => s.Contains(r.Name)) => input.Module.Globals.SingleOrDefault(g => g.Name == r.Name)?.Space,
        Expression.Access a => PlaceSpace(a.Base), Expression.Member m => PlaceSpace(m.Base), Expression.Swizzle s => PlaceSpace(s.Vector),
        Expression.Unary { Operator: "&" or "*" } u => PlaceSpace(u.Operand), _ => null
    };
    private ShaderType PlaceType(Expression e) => e.Type is ShaderType.Pointer ? ValueType(e.Type)
        : PlaceSpace(e) == AddressSpace.Workgroup ? ValueType(input.WorkgroupTypes.GetValueOrDefault(e.Type, e.Type))
        : PlaceSpace(e) is { } space && BufferSpace(space) ? Buffer(e.Type) : ValueType(e.Type);
    private Expression Convert(Expression value, ShaderType type)
    {
        if (value.Type == type) return value;
        var helper = Conversion(value.Type, type);
        return new Expression.Call(helper.Name, [value], type, CallBinding.Function) { Span = value.Span };
    }
    private Expression Place(Expression e) => (e switch {
        Expression.Reference r => new Expression.Reference(r.Name, PlaceType(r)),
        Expression.Access a => new Expression.Access(Place(a.Base), Value(a.Index), PlaceType(a)),
        Expression.Member m => new Expression.Member(Place(m.Base), m.Name, PlaceType(m)),
        Expression.Swizzle s => new Expression.Swizzle(Place(s.Vector), s.Components, PlaceType(s)),
        Expression.Unary u => new Expression.Unary(u.Operator, u.Operator == "&" ? Place(u.Operand) : Value(u.Operand), PlaceType(u)),
        _ => Value(e)
    }) with { Span = e.Span };
    private Expression Value(Expression e)
    {
        if (e.Type is not ShaderType.Pointer && PlaceSpace(e) is not (null or AddressSpace.Handle)
            && e is Expression.Reference or Expression.Access or Expression.Member or Expression.Unary { Operator: "*" }) {
            var place = Place(e); var type = ValueType(e.Type);
            return place.Type == type ? place : Convert(new Expression.Load(place) { Span = e.Span }, type);
        }
        Expression result = e switch {
            Expression.Reference r => new Expression.Reference(r.Name, ValueType(r.Type)),
            Expression.Load l => Convert(new Expression.Load(Place(l.Pointer)) { MemoryAccess = l.MemoryAccess, Span = l.Span }, ValueType(l.Type)),
            Expression.Unary u => new Expression.Unary(u.Operator, u.Operator == "&" ? Place(u.Operand) : Value(u.Operand), ValueType(u.Type)),
            Expression.Binary b => new Expression.Binary(b.Operator, Value(b.Left), Value(b.Right), ValueType(b.Type)),
            Expression.Call c => Call(c),
            Expression.Construct c => new Expression.Construct(ValueType(c.Type), c.Components.Select(Value).ToArray()),
            Expression.Convert c => new Expression.Convert(ValueType(c.Type), Value(c.Operand), c.Bitcast),
            Expression.Access a => new Expression.Access(Value(a.Base), Value(a.Index), ValueType(a.Type)),
            Expression.Member m => new Expression.Member(Value(m.Base), m.Name, ValueType(m.Type)),
            Expression.Swizzle s => new Expression.Swizzle(Value(s.Vector), s.Components, ValueType(s.Type)),
            Expression.Select s => new Expression.Select(Value(s.Condition), Value(s.Accept), Value(s.Reject)),
            _ => e
        };
        return result with { Span = e.Span };
    }
    private Expression Call(Expression.Call call)
    {
        var args = call.Arguments.Select(Value).ToArray(); ShaderType type = ValueType(call.Type);
        bool uniform = call.Binding == CallBinding.Builtin && call.Function == "workgroupUniformLoad"
            && args is [{ Type: ShaderType.Pointer { Base: not ShaderType.Atomic } }];
        var mapped = new Expression.Call(call.Function, args, uniform ? Data(args.Single().Type) : type, call.Binding)
            { AtomicMemory = call.AtomicMemory, MemoryAccess = call.MemoryAccess, ImageAccess = call.ImageAccess, Span = call.Span };
        return uniform ? Convert(mapped, type) : mapped;
    }
    private Statement Statement(Statement s)
    {
        switch (s) {
            case Statement.Declare d:
                var initial = d.Initializer is { } v ? Value(v) : null; scopes.Peek().Add(d.Name);
                return d with { Type = ValueType(d.Type), Initializer = initial };
            case Statement.Store store:
                var place = Place(store.Target);
                return store with { Target = place, Value = Convert(Value(store.Value), Data(place.Type)) };
            case Statement.Evaluate e: return e with { Value = Value(e.Value) };
            case Statement.Nested n: return n with { Body = Body(n.Body) };
            case Statement.If i: return i with { Condition = Value(i.Condition), Accept = Body(i.Accept), Reject = Body(i.Reject) };
            case Statement.Loop l:
                var body = Body(l.Body, true); var continuing = Body(l.Continuing, true); var condition = l.BreakIf is { } b ? Value(b) : null;
                scopes.Pop(); scopes.Pop(); return l with { Body = body, Continuing = continuing, BreakIf = condition };
            case Statement.Switch sw: return sw with { Selector = Value(sw.Selector), Cases = sw.Cases.Select(c => c with { Body = Body(c.Body) }).ToArray() };
            case Statement.Return r: return r with { Value = r.Value is { } returned ? Value(returned) : null };
            case Statement.MeshStore store: return store with { Index = Value(store.Index), Value = Value(store.Value) };
            case Statement.MeshSetOutputs counts: return counts with { Vertices = Value(counts.Vertices), Primitives = Value(counts.Primitives) };
            case Statement.TaskDispatch dispatch: return dispatch with { Dimensions = Value(dispatch.Dimensions) };
            default: return s;
        }
    }
    private Block Body(Block input, bool keepScope = false)
    {
        scopes.Push(new(StringComparer.Ordinal)); var body = new Block(); body.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        body.Statements.AddRange(input.Statements.Select(Statement)); if (!keepScope) scopes.Pop(); return body;
    }
}
