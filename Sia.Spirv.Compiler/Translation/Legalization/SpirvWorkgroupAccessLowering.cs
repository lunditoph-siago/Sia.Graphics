using System.Collections.Frozen;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Give workgroup addresses physical types and place value conversions at ordered accesses.</summary>
internal sealed partial class SpirvWorkgroupAccessLowering(SpirvPhysicalLayout layout)
{
    private readonly Stack<Dictionary<string, bool>> scopes = [];
    private readonly Dictionary<string, GlobalVariable> globals = layout.Module.Globals.ToDictionary(g => g.Name, StringComparer.Ordinal);
    private ShaderType Physical(ShaderType logical) => layout.WorkgroupTypes.TryGetValue(logical, out var physical) ? physical
        : layout.WorkgroupTypes.Values.Contains(logical) ? logical
        : throw new ShaderException(DiagnosticStage.SpirvWrite, "Workgroup address type was not prepared by target legalization.");
    private ShaderType PointerType(ShaderType type) => type is ShaderType.Pointer p
        ? p with { Base = p.Space == AddressSpace.Workgroup ? Physical(p.Base) : PointerType(p.Base) } : type;
    private static ShaderType Data(ShaderType type) => type is ShaderType.Pointer p ? p.Base : type;
    private bool Global(string name) => !scopes.Any(s => s.ContainsKey(name)) && globals.TryGetValue(name, out var g) && g.Space == AddressSpace.Workgroup;
    private bool Workgroup(Expression e) => e.Type is ShaderType.Pointer { Space: AddressSpace.Workgroup } || e switch {
        Expression.Reference r => Global(r.Name), Expression.Access a => Workgroup(a.Base),
        Expression.Member m => Workgroup(m.Base), Expression.Swizzle s => Workgroup(s.Vector),
        Expression.Unary { Operator: "&" or "*" } u => Workgroup(u.Operand), _ => false
    };
    private ShaderType PlaceType(Expression e) => e.Type is ShaderType.Pointer ? PointerType(e.Type)
        : new ShaderType.Pointer(Physical(e.Type), AddressSpace.Workgroup);
    private Expression Conversion(Expression value, ShaderType logical, bool toPhysical)
    {
        if (Physical(logical) == logical) return value;
        if (!layout.WorkgroupConversions.TryGetValue((logical, toPhysical), out var helper))
            throw new ShaderException(DiagnosticStage.SpirvWrite, "Workgroup access needs a prepared value conversion.", value.Span);
        return new Expression.Call(helper.Name, [value], helper.ReturnType, CallBinding.Function) { Span = value.Span };
    }
    private Expression Read(Expression pointer, ShaderType logical, SpirvMemoryAccess? memory = null, SourceSpan span = default)
        => Conversion(new Expression.Load(pointer) { MemoryAccess = memory, Span = span }, logical, false);
    private IReadOnlyList<Expression> Values(IReadOnlyList<Expression> input)
    {
        var output = input.Select(Value).ToArray();
        return output.Where((e, i) => !ReferenceEquals(e, input[i])).Any() ? output : input;
    }
    private Expression Place(Expression e)
    {
        if (!Workgroup(e)) return Value(e);
        Expression result = e switch {
            Expression.Reference r => new Expression.Reference(r.Name, PlaceType(r)),
            Expression.Access a => new Expression.Access(Place(a.Base), Value(a.Index), PlaceType(a)),
            Expression.Member m => new Expression.Member(Place(m.Base), m.Name, PlaceType(m)),
            Expression.Swizzle s => new Expression.Swizzle(Place(s.Vector), s.Components, PlaceType(s)),
            Expression.Unary u => new Expression.Unary(u.Operator, u.Operator == "&" ? Place(u.Operand) : Value(u.Operand), PlaceType(u)),
            _ => Value(e)
        };
        result = result with { Span = e.Span };
        return result == e ? e : result;
    }
    private Expression Value(Expression e)
    {
        if (e.Type is not ShaderType.Pointer && Workgroup(e) && e is Expression.Reference or Expression.Access or Expression.Member or Expression.Unary { Operator: "*" })
            return Read(Place(e), e.Type, span: e.Span);
        Expression result = e switch {
            Expression.Reference r when r.Type is ShaderType.Pointer => new Expression.Reference(r.Name, PointerType(r.Type)),
            Expression.Load l when Workgroup(l.Pointer) => Read(Place(l.Pointer), l.Type, l.MemoryAccess, l.Span),
            Expression.Load l => new Expression.Load(Value(l.Pointer)) { MemoryAccess = l.MemoryAccess },
            Expression.Unary u => new Expression.Unary(u.Operator, u.Operator == "&" ? Place(u.Operand) : Value(u.Operand), PointerType(u.Type)),
            Expression.Binary b => b with { Left = Value(b.Left), Right = Value(b.Right) },
            Expression.Call c => Call(c),
            Expression.Construct c => c with { Components = Values(c.Components) },
            Expression.Convert c => new Expression.Convert(PointerType(c.Type), Value(c.Operand), c.Bitcast),
            Expression.Access a when Workgroup(a) => Place(a),
            Expression.Access a => new Expression.Access(Value(a.Base), Value(a.Index), PointerType(a.Type)),
            Expression.Member m when Workgroup(m) => Place(m),
            Expression.Member m => new Expression.Member(Value(m.Base), m.Name, PointerType(m.Type)),
            Expression.Swizzle s when s.Type is not ShaderType.Pointer => new Expression.Swizzle(Value(s.Vector), s.Components, s.Type),
            Expression.Swizzle s when Workgroup(s) => Place(s),
            Expression.Swizzle s => new Expression.Swizzle(Value(s.Vector), s.Components, PointerType(s.Type)),
            Expression.Select s => new Expression.Select(Value(s.Condition), Value(s.Accept), Value(s.Reject)),
            _ => e
        };
        result = result with { Span = e.Span };
        return result == e ? e : result;
    }
    private Expression Call(Expression.Call call)
    {
        var arguments = Values(call.Arguments);
        bool builtin = call.Binding == CallBinding.Builtin;
        bool uniform = builtin && call.Function == "workgroupUniformLoad"
            && call.Arguments.Count == 1 && call.Arguments[0].Type is ShaderType.Pointer { Base: not ShaderType.Atomic };
        var type = uniform ? Physical(call.Type) : PointerType(call.Type);
        var result = new Expression.Call(call.Function, arguments, type, call.Binding) {             AtomicMemory = call.AtomicMemory,MemoryAccess = call.MemoryAccess,Span = call.Span };
        return uniform ? Conversion(result, call.Type, false) : result;
    }
    private Statement Statement(Statement s)
    {
        Statement result;
        switch (s) {
            case Statement.Declare d:
                var initial = d.Initializer is null ? null : Value(d.Initializer);
                result = d with { Type = PointerType(d.Type), Initializer = initial };
                scopes.Peek()[d.Name] = true; break;
            case Statement.Store store:
                result = store with { Target = Workgroup(store.Target) ? Place(store.Target) : Value(store.Target),
                    Value = Workgroup(store.Target) ? Conversion(Value(store.Value), store.Value.Type, true) : Value(store.Value) }; break;
            case Statement.Evaluate e: result = e with { Value = Value(e.Value) }; break;
            case Statement.Nested n: result = n with { Body = Body(n.Body) }; break;
            case Statement.If i: result = i with { Condition = Value(i.Condition), Accept = Body(i.Accept), Reject = Body(i.Reject) }; break;
            case Statement.Loop l:
                var body = Body(l.Body, keepScope: true);
                var continuing = Body(l.Continuing, keepScope: true);
                result = l with { Body = body, Continuing = continuing, BreakIf = l.BreakIf is null ? null : Value(l.BreakIf) };
                scopes.Pop(); scopes.Pop(); break;
            case Statement.Switch sw:
                var cases = sw.Cases.Select(c => c with { Body = Body(c.Body) }).ToArray();
                result = sw with { Selector = Value(sw.Selector), Cases = cases.Where((c, i) => c != sw.Cases[i]).Any() ? cases : sw.Cases }; break;
            case Statement.Return r: result = r with { Value = r.Value is null ? null : Value(r.Value) }; break;
            case Statement.MeshStore store: result = store with { Index = Value(store.Index), Value = Value(store.Value) }; break;
            case Statement.MeshSetOutputs counts: result = counts with { Vertices = Value(counts.Vertices), Primitives = Value(counts.Primitives) }; break;
            case Statement.TaskDispatch dispatch: result = dispatch with { Dimensions = Value(dispatch.Dimensions) }; break;
            default: return s;
        }
        return result == s ? s : result;
    }
    private Block Body(Block input, bool keepScope = false)
    {
        scopes.Push(new(StringComparer.Ordinal));
        var output = new Block(); output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        output.Statements.AddRange(input.Statements.Select(Statement));
        if (!keepScope) scopes.Pop();
        return output.Statements.Where((s, i) => !ReferenceEquals(s, input.Statements[i])).Any() ? output : input;
    }
    internal static SpirvPhysicalLayout Run(SpirvPhysicalLayout input, IDictionary<string, ControlFlowFunction>? ownedGraphs = null)
    {
        if (input.WorkgroupTypes.All(p => p.Key == p.Value)) return input;
        return new SpirvWorkgroupAccessLowering(input).Run(ownedGraphs);
    }
    private SpirvPhysicalLayout Run(IDictionary<string, ControlFlowFunction>? ownedGraphs)
    {
        var input = layout.Module;
        var graphs = ownedGraphs ?? new Dictionary<string, ControlFlowFunction>(StringComparer.Ordinal);
        if (ownedGraphs is null)
            foreach (var function in input.Functions)
                if (SpirvControlFlowLowering.TryRead(function, input, out var graph, out _)) graphs.Add(function.Name, graph!);
        var output = new Module { VulkanMemoryModel = input.VulkanMemoryModel, WorkgroupInitializationRequired = input.WorkgroupInitializationRequired };
        output.Structures.AddRange(input.Structures); output.Constants.AddRange(input.Constants);
        output.Globals.AddRange(input.Globals.Select(g => g.Space == AddressSpace.Workgroup ? g with { Type = Physical(g.Type) } : g));
        output.Enables.UnionWith(input.Enables); output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        foreach (var f in input.Functions) {
            // Only explicit deferrals use the structured access adapter. The body
            // on a graph-owned declaration is borrowed frontend context, not code.
            var body = f.Body;
            if (!graphs.ContainsKey(f.Name)) {
                scopes.Push(f.Arguments.ToDictionary(a => a.Name, _ => true, StringComparer.Ordinal));
                body = Body(f.Body); scopes.Pop();
            }
            var args = f.Arguments.Select(a => a with { Type = PointerType(a.Type) }).ToArray();
            var returns = PointerType(f.ReturnType);
            if (ReferenceEquals(body, f.Body) && args.SequenceEqual(f.Arguments) && returns == f.ReturnType) { output.Functions.Add(f); continue; }
            var copy = new ShaderFunction(f.Name) { Body = body, Stage = f.Stage, ReturnType = returns, ReturnBinding = f.ReturnBinding,
                WorkgroupSize = f.WorkgroupSize.ToArray(), TaskPayload = f.TaskPayload, MeshOutput = f.MeshOutput,
                EarlyDepthTest = f.EarlyDepthTest, ConservativeDepth = f.ConservativeDepth };
            copy.Arguments.AddRange(args); copy.DiagnosticFilters.AddRange(f.DiagnosticFilters); output.Functions.Add(copy);
        }
        // The normal entry preparation validates this module after layout lowering.
        // Raw native fixture preparation may retain pointer selectors that cannot
        // be represented by the source-language validator; its native gate remains.
        var types = layout.WorkgroupTypes.ToDictionary(p => p.Key, p => p.Value);
        foreach (var physical in layout.WorkgroupTypes.Values) types.TryAdd(physical, physical);
        var prepared = layout with { Module = output, WorkgroupTypes = types.ToFrozenDictionary() };
        foreach (var declaration in output.Functions)
            if (graphs.TryGetValue(declaration.Name, out var graph)) {
                var copy = graph.Copy(declaration);
                Run(copy, prepared); graphs[declaration.Name] = copy;
            }
        return ownedGraphs is null ? SpirvControlFlowLowering.Prepare(prepared, graphs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)) : prepared;
    }
}
