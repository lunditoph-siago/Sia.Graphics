using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Materialize synchronization and default atomic operands in ordered target IR.</summary>
internal sealed partial class SpirvSynchronizationLowering(Module input)
{
    private sealed record Local(bool Place);
    private readonly Stack<Dictionary<string, Local>> scopes = [];
    private readonly Dictionary<string, GlobalVariable> globals = input.Globals.ToDictionary(g => g.Name, StringComparer.Ordinal);
    private readonly HashSet<string> names = input.Functions.Select(f => f.Name).Concat(input.Globals.Select(g => g.Name))
        .Concat(input.Constants.Select(c => c.Name)).Concat(input.Structures.Select(s => s.Name)).ToHashSet(StringComparer.Ordinal);
    private int nextName;
    private string Fresh()
    {
        string name;
        do name = "sia_spv_sync_" + nextName++; while (!names.Add(name));
        return name;
    }
    private Local? Lookup(string name) => scopes.Select(s => s.GetValueOrDefault(name)).FirstOrDefault(s => s is not null);
    private bool IsPlace(Expression e) => e switch {
        Expression.Reference r => Lookup(r.Name)?.Place ?? globals.ContainsKey(r.Name),
        Expression.Unary { Operator: "*" } => true,
        Expression.Access a => IsPlace(a.Base), Expression.Member m => IsPlace(m.Base), _ => false
    };
    private static ShaderType Data(ShaderType type) => type is ShaderType.Pointer p ? p.Base : type;
    private ShaderType.Pointer Pointer(Expression e)
    {
        if (e.Type is ShaderType.Pointer p) return p;
        if (e is Expression.Access a) return Pointer(a.Base) with { Base = e.Type };
        if (e is Expression.Member m) return Pointer(m.Base) with { Base = e.Type };
        if (e is Expression.Reference r && Lookup(r.Name) is null && globals.TryGetValue(r.Name, out var global))
            return new(e.Type, global.Space, global.Space is AddressSpace.Uniform or AddressSpace.Immediate or AddressSpace.Handle ? StorageAccess.Read : global.Access);
        return new(e.Type, AddressSpace.Function);
    }
    private bool Builtin(Expression.Call call) => call.Binding == CallBinding.Builtin;
    private static bool BarrierName(string name) => name is "storageBarrier" or "workgroupBarrier" or "textureBarrier" or "subgroupBarrier";
    private static SpirvBarrierMemory BarrierMemory(bool storage, bool workgroup, bool texture, bool subgroup, bool control)
        => new(subgroup ? 3u : 2u, 8u | (storage ? 64u : 0) | (workgroup || subgroup ? 256u : 0) | (texture ? 2048u : 0), control ? subgroup ? 3u : 2u : null);
    private static Statement.Barrier Barrier(string name, SourceSpan span) => new(name == "storageBarrier", name == "workgroupBarrier", name == "textureBarrier", name == "subgroupBarrier") {
        NativeMemory = BarrierMemory(name == "storageBarrier", name == "workgroupBarrier", name == "textureBarrier", name == "subgroupBarrier", true), Span = span
    };
    private Expression.Call Atomic(Expression.Call call)
    {
        if (!Builtin(call)) return call;
        return call with { AtomicMemory = DefaultAtomic(call.Function, call.Arguments.FirstOrDefault()?.Type,
            call.AtomicMemory, call.MemoryAccess, input.VulkanMemoryModel) };
    }
    private static SpirvAtomicMemory? DefaultAtomic(string name, ShaderType? pointer, SpirvAtomicMemory? memory,
        SpirvMemoryAccess? ordinaryMemory, bool vulkan)
    {
        if (memory is not null || ordinaryMemory is not null && name is "atomicLoad" or "atomicStore") return memory;
        bool uniform = name == "workgroupUniformLoad" && pointer is ShaderType.Pointer { Base: ShaderType.Atomic };
        if (!uniform && !name.StartsWith("atomic", StringComparison.Ordinal) && !name.StartsWith("textureAtomic", StringComparison.Ordinal)
            && name != "spirvAtomicCompareExchange") return memory;
        bool workgroup = pointer is ShaderType.Pointer { Space: AddressSpace.Workgroup or AddressSpace.TaskPayload };
        return new(uniform || workgroup ? 2u : vulkan ? 5u : 1u, 0,
            name is "atomicCompareExchangeWeak" or "spirvAtomicCompareExchange" ? 0u : null);
    }
    private Expression Capture(Expression value, List<Statement> prefix, bool place = false)
    {
        ShaderType type = place ? Pointer(value) : value.Type;
        string name = Fresh();
        Expression initial = place ? new Expression.Unary("&", value, type) { Span = value.Span } : value;
        prefix.Add(new Statement.Declare(name, type, initial, false) { Span = value.Span });
        var reference = new Expression.Reference(name, type) { Span = value.Span };
        return place ? new Expression.Unary("*", reference, type) { Span = value.Span } : reference;
    }
    private Expression[] Children(IReadOnlyList<(Expression Value, bool Place)> input, List<Statement> prefix)
    {
        var parts = input.Select(item => {
            var statements = new List<Statement>();
            return (Value: Rewrite(item.Value, statements, item.Place), Prefix: statements);
        }).ToArray();
        var values = new Expression[parts.Length];
        for (int i = 0; i < parts.Length; i++) {
            prefix.AddRange(parts[i].Prefix);
            values[i] = parts.Skip(i + 1).Any(p => p.Prefix.Count != 0) ? Capture(parts[i].Value, prefix, input[i].Place) : parts[i].Value;
        }
        return values;
    }
    private static ShaderType ProjectedType(ShaderType original, Expression parent)
        => original is not ShaderType.Pointer && parent.Type is ShaderType.Pointer p ? new ShaderType.Pointer(original, p.Space, p.Access) : original;
    private Expression Rewrite(Expression e, List<Statement> prefix, bool place = false)
    {
        Expression result;
        switch (e) {
            case Expression.Load load:
                result = new Expression.Load(Rewrite(load.Pointer, prefix, true)) { MemoryAccess = load.MemoryAccess }; break;
            case Expression.Unary u:
                result = u with { Operand = Rewrite(u.Operand, prefix, u.Operator == "&") }; break;
            case Expression.Binary b:
                var binary = Children([(b.Left, false), (b.Right, false)], prefix);
                result = b with { Left = binary[0], Right = binary[1] }; break;
            case Expression.Call c:
                var args = Children(c.Arguments.Select(a => (a, false)).ToArray(), prefix);
                result = Atomic(c with { Arguments = args.Where((a,i) => !ReferenceEquals(a,c.Arguments[i])).Any() ? args : c.Arguments });
                if (Builtin(c) && c.Function == "workgroupUniformLoad") {
                    var call = (Expression.Call)result;
                    var pointer = Capture(call.Arguments[0], prefix);
                    prefix.Add(Barrier("workgroupBarrier", c.Span));
                    Expression read = pointer.Type is ShaderType.Pointer { Base: ShaderType.Atomic }
                        ? new Expression.Call("atomicLoad", [pointer], c.Type, CallBinding.Builtin) { AtomicMemory = call.AtomicMemory,Span = c.Span }

                        : new Expression.Load(new Expression.Unary("*", pointer, pointer.Type) { Span = c.Span }) { MemoryAccess = call.MemoryAccess, Span = c.Span };
                    result = Capture(read, prefix);
                    prefix.Add(Barrier("workgroupBarrier", c.Span));
                }
                break;
            case Expression.Construct c:
                var components = Children(c.Components.Select(a => (a, false)).ToArray(), prefix);
                result = c with { Components = components.Where((a,i) => !ReferenceEquals(a,c.Components[i])).Any() ? components : c.Components }; break;
            case Expression.Convert c: result = c with { Operand = Rewrite(c.Operand, prefix) }; break;
            case Expression.Access a:
                var access = Children([(a.Base, place || IsPlace(a)), (a.Index, false)], prefix);
                result = new Expression.Access(access[0], access[1], ProjectedType(a.Type, access[0])); break;
            case Expression.Member m:
                var parent = Rewrite(m.Base, prefix, place || IsPlace(m));
                result = new Expression.Member(parent, m.Name, ProjectedType(m.Type, parent)); break;
            case Expression.Swizzle s: result = s with { Vector = Rewrite(s.Vector, prefix) }; break;
            case Expression.Select s:
                var select = Children([(s.Condition,false),(s.Accept,false),(s.Reject,false)], prefix);
                result = new Expression.Select(select[0], select[1], select[2]); break;
            default: return e;
        }
        result = result with { Span = e.Span };
        if (!place && e.Type is not ShaderType.Pointer && result.Type is ShaderType.Pointer && IsPlace(e))
            result = new Expression.Load(result) { Span = e.Span };
        return result == e ? e : result;
    }
    private IReadOnlyList<Statement> Statement(Statement s)
    {
        var prefix = new List<Statement>();
        Statement result;
        switch (s) {
            case Statement.Declare d:
                result = d with { Initializer = d.Initializer is null ? null : Rewrite(d.Initializer, prefix) };
                scopes.Peek()[d.Name] = new(d.Mutable); break;
            case Statement.Store store:
                var targetPrefix = new List<Statement>(); var target = Rewrite(store.Target, targetPrefix, true);
                var valuePrefix = new List<Statement>(); var value = Rewrite(store.Value, valuePrefix);
                prefix.AddRange(targetPrefix);
                if (valuePrefix.Count != 0) target = Capture(target, prefix, true);
                prefix.AddRange(valuePrefix);
                result = store with { Target = target, Value = value }; break;
            case Statement.Evaluate { Value: Expression.Call c } when Builtin(c) && BarrierName(c.Function):
                result = Barrier(c.Function, c.Span); break;
            case Statement.Evaluate ev: result = ev with { Value = Rewrite(ev.Value, prefix) }; break;
            case Statement.Nested n: result = n with { Body = Body(n.Body) }; break;
            case Statement.If i: result = i with { Condition = Rewrite(i.Condition, prefix), Accept = Body(i.Accept), Reject = Body(i.Reject) }; break;
            case Statement.Loop l:
                var body = Body(l.Body,true); var continuing = Body(l.Continuing,true);
                var tail = new List<Statement>(); var condition = l.BreakIf is null ? null : Rewrite(l.BreakIf,tail);
                if (tail.Count != 0) { var copy = new Block(); copy.DiagnosticFilters.AddRange(continuing.DiagnosticFilters); copy.Statements.AddRange(continuing.Statements); copy.Statements.AddRange(tail); continuing = copy; }
                result = l with { Body = body, Continuing = continuing, BreakIf = condition };
                scopes.Pop(); scopes.Pop(); break;
            case Statement.Switch sw:
                var cases = sw.Cases.Select(c => c with { Body = Body(c.Body) }).ToArray();
                result = sw with { Selector = Rewrite(sw.Selector,prefix), Cases = cases.Where((c,i) => c != sw.Cases[i]).Any() ? cases : sw.Cases }; break;
            case Statement.Return r: result = r with { Value = r.Value is null ? null : Rewrite(r.Value,prefix) }; break;
            case Statement.Barrier b: result = b with { NativeMemory = b.NativeMemory ?? BarrierMemory(b.Storage,b.Workgroup,b.Texture,b.Subgroup,true) }; break;
            case Statement.MemoryBarrier b: result = b with { NativeMemory = b.NativeMemory ?? BarrierMemory(b.Storage,b.Workgroup,b.Texture,b.Subgroup,false) }; break;
            case Statement.MeshStore store:
                var mesh = Children([(store.Index,false),(store.Value,false)],prefix);
                result = store with { Index = mesh[0], Value = mesh[1] }; break;
            case Statement.MeshSetOutputs counts:
                var size = Children([(counts.Vertices,false),(counts.Primitives,false)],prefix);
                result = counts with { Vertices = size[0], Primitives = size[1] }; break;
            case Statement.TaskDispatch dispatch: result = dispatch with { Dimensions = Rewrite(dispatch.Dimensions,prefix) }; break;
            default: return [s];
        }
        prefix.Add(result == s ? s : result);
        return prefix;
    }
    private Block Body(Block input, bool keepScope = false)
    {
        scopes.Push(new(StringComparer.Ordinal));
        var output = new Block(); output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        output.Statements.AddRange(input.Statements.SelectMany(Statement));
        if (!keepScope) scopes.Pop();
        return output.Statements.Count == input.Statements.Count && output.Statements.Select((s,i) => ReferenceEquals(s,input.Statements[i])).All(s => s) ? input : output;
    }
    private void Names(Block b)
    {
        foreach (var s in b.Statements) {
            if (s is Statement.Declare d) names.Add(d.Name);
            foreach (var child in s switch {
                Statement.Nested n => new[] {n.Body}, Statement.If i => [i.Accept,i.Reject], Statement.Loop l => [l.Body,l.Continuing],
                Statement.Switch sw => sw.Cases.Select(c => c.Body).ToArray(), _ => [] }) Names(child);
        }
    }
    internal static Module Run(Module module) => new SpirvSynchronizationLowering(module).Run();
    internal static Module Run(Module module, IReadOnlySet<string> functions) => new SpirvSynchronizationLowering(module).Run(functions);
    private Module Run(IReadOnlySet<string>? selected = null)
    {
        foreach (var f in input.Functions) { foreach (var a in f.Arguments) names.Add(a.Name); Names(f.Body); }
        var output = new Module { VulkanMemoryModel = input.VulkanMemoryModel, WorkgroupInitializationRequired = input.WorkgroupInitializationRequired };
        output.Structures.AddRange(input.Structures); output.Constants.AddRange(input.Constants); output.Globals.AddRange(input.Globals);
        output.Enables.UnionWith(input.Enables); output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        foreach (var f in input.Functions) {
            if (selected is not null && !selected.Contains(f.Name)) { output.Functions.Add(f); continue; }
            scopes.Push(f.Arguments.ToDictionary(a => a.Name,_ => new Local(false),StringComparer.Ordinal));
            var body = Body(f.Body); scopes.Pop();
            if (ReferenceEquals(body,f.Body)) { output.Functions.Add(f); continue; }
            var copy = new ShaderFunction(f.Name) { Body = body, Stage = f.Stage, ReturnType = f.ReturnType, ReturnBinding = f.ReturnBinding,
                WorkgroupSize = f.WorkgroupSize.ToArray(), TaskPayload = f.TaskPayload, MeshOutput = f.MeshOutput,
                EarlyDepthTest = f.EarlyDepthTest, ConservativeDepth = f.ConservativeDepth };
            copy.Arguments.AddRange(f.Arguments); copy.DiagnosticFilters.AddRange(f.DiagnosticFilters); output.Functions.Add(copy);
        }
        return output.Functions.Where((f,i) => !ReferenceEquals(f,input.Functions[i])).Any() ? output : input;
    }
}
