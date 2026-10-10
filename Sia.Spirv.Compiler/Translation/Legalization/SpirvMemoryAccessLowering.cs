using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Proc;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Capture qualified address requirements on ordered accesses before serialization.</summary>
internal sealed partial class SpirvMemoryAccessLowering(Module input)
{
    private sealed record Symbol(bool Place, AddressSpace Space, MemoryDecorations? Captured = null);
    private readonly Stack<Dictionary<string, Symbol>> scopes = [];
    private readonly Dictionary<string, GlobalVariable> globals = input.Globals.ToDictionary(g => g.Name, StringComparer.Ordinal);
    private readonly MemoryDecorations sharedMemory = input.Globals.Where(g => g.Space == AddressSpace.Storage)
        .Aggregate(MemoryDecorations.None, (flags, g) => flags | g.MemoryDecorations | ShaderMemoryRequirements.TypeMemory(g.Type));
    private readonly bool vulkan = input.VulkanMemoryModel || ShaderMemoryRequirements.UsesCooperativeMemoryModel(input);
    private Symbol? Local(string name) => scopes.Select(s => s.GetValueOrDefault(name)).FirstOrDefault(s => s is not null);
    private bool IsPlace(Expression e) => e switch {
        Expression.Reference r => Local(r.Name)?.Place ?? globals.ContainsKey(r.Name),
        Expression.Unary { Operator: "*" } => true,
        Expression.Access a => IsPlace(a.Base), Expression.Member m => IsPlace(m.Base), _ => false
    };
    private AddressSpace Space(Expression e) => e.Type is ShaderType.Pointer p ? p.Space : e switch {
        Expression.Reference r => Local(r.Name)?.Space ?? globals.GetValueOrDefault(r.Name)?.Space ?? AddressSpace.Function,
        Expression.Access a => Space(a.Base), Expression.Member m => Space(m.Base),
        Expression.Unary { Operator: "&" or "*" } u => Space(u.Operand), _ => AddressSpace.Function
    };
    private static ShaderType Data(ShaderType type) => type is ShaderType.Pointer p ? p.Base : type;
    private MemoryDecorations Requirements(Expression pointer)
    {
        Expression root = pointer;
        while (root is Expression.Unary { Operator: "&" or "*" } or Expression.Access or Expression.Member)
            root = root switch { Expression.Unary u => u.Operand, Expression.Access a => a.Base, Expression.Member m => m.Base, _ => root };
        // Native accesses already carry their original function-memory operands.
        // Logical snapshots must not inherit member qualifications on new locals.
        if (pointer.Type is ShaderType.Pointer { Space: AddressSpace.Function }
            || root is Expression.Reference local && Local(local.Name) is { Place: true, Space: AddressSpace.Function })
            return MemoryDecorations.None;
        MemoryDecorations Inherited(Expression e) => e switch {
            Expression.Unary { Operator: "&" or "*" } u => Inherited(u.Operand),
            Expression.Access a => Inherited(a.Base),
            Expression.Member m => Inherited(m.Base) | ((Data(m.Base.Type) as ShaderType.Structure)?.Members.FirstOrDefault(f => f.Name == m.Name)?.MemoryDecorations ?? MemoryDecorations.None),
            Expression.Reference r when Local(r.Name)?.Captured is { } captured => captured,
            Expression.Reference r when Local(r.Name) is null && globals.TryGetValue(r.Name, out var g) => g.MemoryDecorations,
            _ => e.Type is ShaderType.Pointer { Space: AddressSpace.Storage } ? sharedMemory : MemoryDecorations.None
        };
        return Inherited(pointer) | ShaderMemoryRequirements.TypeMemory(pointer.Type);
    }
    private SpirvMemoryAccess? Access(Expression pointer, SpirvMemoryAccess? memory, bool load = true)
        => ShaderMemoryRequirements.Decorate(memory, Requirements(pointer), Space(pointer), vulkan, load);
    private IReadOnlyList<Expression> Values(IReadOnlyList<Expression> input)
    {
        var output = input.Select(Value).ToArray();
        return output.Where((e, i) => !ReferenceEquals(e, input[i])).Any() ? output : input;
    }
    private Expression Place(Expression e)
    {
        Expression result = e switch {
            Expression.Unary { Operator: "&" } u => u with { Operand = Place(u.Operand) },
            Expression.Unary { Operator: "*" } u => u with { Operand = Value(u.Operand) },
            Expression.Access a => a with { Base = Place(a.Base), Index = Value(a.Index) },
            Expression.Member m => m with { Base = Place(m.Base) }, _ => e
        };
        return result == e ? e : result;
    }
    private Expression Value(Expression e)
    {
        if (IsPlace(e) && e is Expression.Reference or Expression.Access or Expression.Member or Expression.Unary { Operator: "*" }) {
            var memory = Access(e, null);
            // Only qualified implicit reads need an explicit target node. Plain
            // source-adapter reads retain their existing serialization and IDs.
            if (memory is not null) return new Expression.Load(Place(e)) { MemoryAccess = memory, Span = e.Span };
        }
        Expression result = e switch {
            Expression.Load l => new Expression.Load(Place(l.Pointer)) { MemoryAccess = Access(l.Pointer, l.MemoryAccess), Span = l.Span },
            Expression.Unary { Operator: "&" } u => u with { Operand = Place(u.Operand) },
            Expression.Unary u => u with { Operand = Value(u.Operand) },
            Expression.Binary b => b with { Left = Value(b.Left), Right = Value(b.Right) },
            Expression.Call c => Call(c),
            Expression.Construct c => c with { Components = Values(c.Components) },
            Expression.Convert c => c with { Operand = Value(c.Operand) },
            Expression.Access a => a with { Base = IsPlace(a) ? Place(a.Base) : Value(a.Base), Index = Value(a.Index) },
            Expression.Member m => m with { Base = IsPlace(m) ? Place(m.Base) : Value(m.Base) },
            Expression.Swizzle s => s with { Vector = Value(s.Vector) },
            Expression.Select s => s with { Condition = Value(s.Condition), Accept = Value(s.Accept), Reject = Value(s.Reject) }, _ => e
        };
        return result == e ? e : result;
    }
    private Expression Call(Expression.Call c)
    {
        var result = c with { Arguments = Values(c.Arguments) };
        if (c.Binding == CallBinding.Function) return result == c ? c : result;
        bool ordinaryAtomic = c.MemoryAccess is not null && c.Function is "atomicLoad" or "atomicStore";
        bool load = c.Function is "coopLoad" or "coopLoadT" or "workgroupUniformLoad" or "atomicLoad";
        bool store = c.Function is "coopStore" or "coopStoreT" or "atomicStore";
        if (c.Arguments.Count != 0 && (c.Function == "workgroupUniformLoad" || c.Function.StartsWith("coop", StringComparison.Ordinal) && (load || store) || ordinaryAtomic)) {
            var pointer = c.Arguments[c.Function is "coopStore" or "coopStoreT" ? 1 : 0];
            if (c.Function != "workgroupUniformLoad" || pointer.Type is not ShaderType.Pointer { Base: ShaderType.Atomic })
                result = result with { MemoryAccess = Access(pointer, c.MemoryAccess, load) };
        }
        if (c.Arguments.Count != 0 && !ordinaryAtomic && (c.Function.StartsWith("atomic", StringComparison.Ordinal) || c.Function == "spirvAtomicCompareExchange"
            || c.Function == "workgroupUniformLoad" && c.Arguments[0].Type is ShaderType.Pointer { Base: ShaderType.Atomic })) {
            if (vulkan && (Requirements(c.Arguments[0]) & MemoryDecorations.Volatile) != 0) {
                var memory = c.AtomicMemory ?? new(Space(c.Arguments[0]) is AddressSpace.Workgroup or AddressSpace.TaskPayload ? 2u : input.VulkanMemoryModel ? 5u : 1u, 0);
                bool compare = c.Function is "atomicCompareExchangeWeak" or "spirvAtomicCompareExchange";
                result = result with { AtomicMemory = memory with { Semantics = memory.Semantics | 32768,
                    UnequalSemantics = compare || memory.UnequalSemantics is not null ? (memory.UnequalSemantics ?? memory.Semantics) | 32768 : null } };
            }
        }
        return result == c ? c : result;
    }
    private Statement Statement(Statement s)
    {
        Statement result;
        switch (s) {
            case Statement.Declare d:
                var initial = d.Initializer is null ? null : Value(d.Initializer);
                MemoryDecorations? captured = !d.Mutable && d.Type is ShaderType.Pointer && d.Initializer is { } pointer ? Requirements(pointer) : null;
                result = d with { Initializer = initial };
                scopes.Peek()[d.Name] = new(d.Mutable, AddressSpace.Function, captured); break;
            case Statement.Store store: result = store with { Target = Place(store.Target), Value = Value(store.Value), MemoryAccess = Access(store.Target, store.MemoryAccess, false) }; break;
            case Statement.Evaluate e: result = e with { Value = Value(e.Value) }; break;
            case Statement.Nested n: result = n with { Body = Body(n.Body) }; break;
            case Statement.If i: result = i with { Condition = Value(i.Condition), Accept = Body(i.Accept), Reject = Body(i.Reject) }; break;
            case Statement.Loop l:
                var body = Body(l.Body, true); var continuing = Body(l.Continuing, true);
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
    internal static Module Run(Module module) => new SpirvMemoryAccessLowering(module).Run();
    internal static Module Run(Module module, IReadOnlySet<string> functions) => new SpirvMemoryAccessLowering(module).Run(functions);
    private Module Run(IReadOnlySet<string>? selected = null)
    {
        var output = new Module { VulkanMemoryModel = input.VulkanMemoryModel, WorkgroupInitializationRequired = input.WorkgroupInitializationRequired };
        output.Structures.AddRange(input.Structures); output.Constants.AddRange(input.Constants); output.Globals.AddRange(input.Globals);
        output.Enables.UnionWith(input.Enables); output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        foreach (var f in input.Functions) {
            if (selected is not null && !selected.Contains(f.Name)) { output.Functions.Add(f); continue; }
            scopes.Push(f.Arguments.ToDictionary(a => a.Name, _ => new Symbol(false, AddressSpace.Function), StringComparer.Ordinal));
            var body = Body(f.Body); scopes.Pop();
            if (ReferenceEquals(body, f.Body)) { output.Functions.Add(f); continue; }
            var copy = new ShaderFunction(f.Name) { Body = body, Stage = f.Stage, ReturnType = f.ReturnType, ReturnBinding = f.ReturnBinding,
                WorkgroupSize = f.WorkgroupSize.ToArray(), TaskPayload = f.TaskPayload, MeshOutput = f.MeshOutput,
                EarlyDepthTest = f.EarlyDepthTest, ConservativeDepth = f.ConservativeDepth };
            copy.Arguments.AddRange(f.Arguments); copy.DiagnosticFilters.AddRange(f.DiagnosticFilters); output.Functions.Add(copy);
        }
        return output.Functions.Where((f, i) => !ReferenceEquals(f, input.Functions[i])).Any() ? output : input;
    }
}
