namespace Sia.Spirv.Compiler.Translation.IR.ControlFlow;

/// <summary>Internal typed CFG. One compilation owns its blocks and edge arguments.</summary>
internal sealed class ControlFlowFunction(ShaderFunction signature)
{
    public ShaderFunction Signature { get; } = signature;
    public List<ControlFlowBlock> Blocks { get; } = [];
    // Frontends retain known structured boundaries as verified CFG facts, without
    // retaining source statements. Null boundaries were removed as unreachable.
    public Dictionary<int, int?> SelectionMerges { get; } = [];
    public Dictionary<int, ControlFlowLoop> Loops { get; } = [];
    public int Entry { get; set; }
    private int nextValue;
    private int nextBlock;
    public SsaValue Value(ShaderType type) => new(nextValue++, type);
    public ControlFlowBlock Block()
    {
        var block = new ControlFlowBlock(nextBlock++);
        Blocks.Add(block); return block;
    }

    internal ControlFlowFunction Copy()
    {
        var output = new ControlFlowFunction(Signature) { Entry = Entry, nextValue = nextValue, nextBlock = nextBlock };
        ControlFlowEdge Edge(ControlFlowEdge edge) => new(edge.Target, edge.Arguments);
        foreach (var block in Blocks) {
            var copy = new ControlFlowBlock(block.Id);
            copy.Parameters.AddRange(block.Parameters);
            copy.Instructions.AddRange(block.Instructions.Select(i => i with { Operation = i.Operation.Map(v => v) }));
            copy.Terminator = block.Terminator switch {
                ControlFlowTerminator.Branch b => b with { Edge = Edge(b.Edge) },
                ControlFlowTerminator.Conditional c => c with { Accept = Edge(c.Accept), Reject = Edge(c.Reject) },
                ControlFlowTerminator.Switch s => s with { Cases = s.Cases.Select(c => c with { Edge = Edge(c.Edge) }).ToArray(), Default = Edge(s.Default) },
                _ => block.Terminator
            };
            output.Blocks.Add(copy);
        }
        foreach (var pair in SelectionMerges) output.SelectionMerges.Add(pair.Key,pair.Value);
        foreach (var pair in Loops) output.Loops.Add(pair.Key,pair.Value);
        return output;
    }
}

internal sealed record ControlFlowLoop(int? Continuing, int? Merge);

internal readonly record struct SsaValue(int Id, ShaderType Type);

internal sealed class ControlFlowBlock(int id)
{
    public int Id { get; } = id;
    public List<SsaValue> Parameters { get; } = [];
    public List<ControlFlowInstruction> Instructions { get; } = [];
    public ControlFlowTerminator? Terminator { get; set; }
}

[Flags]
internal enum ShaderEffects {
    None = 0, ReadMemory = 1, WriteMemory = 2, UnknownCall = 4, Atomic = 8,
    Synchronization = 16, Convergent = 32, MemoryOrdering = 64, Volatile = 128, Resource = 256, InvocationTermination = 512, HelperDemotion = 1024,
    ReadInvocationState = 2048
}

internal sealed record ControlFlowInstruction(SsaValue? Result, ValueOperation Operation, SourceSpan Span = default)
{
    public IReadOnlyList<DiagnosticFilter> DiagnosticFilters { get; init; } = [];
    public ShaderEffects Effects => Operation switch {
        ValueOperation.Local { ZeroInitialize: true } => ShaderEffects.WriteMemory,
        ValueOperation.Load load => ShaderEffects.ReadMemory | ShaderBuiltinEffects.Memory(load.MemoryAccess),
        ValueOperation.Store store => ShaderEffects.WriteMemory | ShaderBuiltinEffects.Memory(store.MemoryAccess),
        ValueOperation.MeshStore => ShaderEffects.WriteMemory,
        ValueOperation.InterfaceLoad => ShaderEffects.ReadMemory | ShaderEffects.Resource,
        ValueOperation.InterfaceStore => ShaderEffects.WriteMemory | ShaderEffects.Resource,
        ValueOperation.MeshSetOutputs => ShaderEffects.WriteMemory | ShaderEffects.Convergent,
        ValueOperation.Builtin builtin => ShaderBuiltinEffects.For(builtin.Function) | ShaderBuiltinEffects.Memory(builtin.MemoryAccess)
            | ShaderBuiltinEffects.AtomicMemory(builtin.AtomicMemory),
        ValueOperation.Barrier barrier => ShaderBuiltinEffects.Barrier(barrier.Control),
        ValueOperation.Demote => ShaderEffects.Convergent | ShaderEffects.HelperDemotion,
        ValueOperation.HelperInvocation => ShaderEffects.Convergent | ShaderEffects.ReadInvocationState,
        // Until interprocedural effect analysis proves more, calls are ordered opaque effects.
        // UnknownCall includes synchronization and convergence requirements of the callee.
        ValueOperation.Call call => ShaderEffects.ReadMemory | ShaderEffects.WriteMemory | ShaderEffects.UnknownCall | call.CalleeEffects,
        _ => ShaderEffects.None
    };
}

internal abstract record ValueOperation
{
    public sealed record Literal(object Value) : ValueOperation;
    public sealed record Symbol(string Name) : ValueOperation;
    public sealed record Local(string Name, bool ZeroInitialize = true) : ValueOperation;
    public sealed record Let(string Name, SsaValue Value) : ValueOperation;
    public sealed record Unary(string Operator, SsaValue Operand) : ValueOperation;
    public sealed record Binary(string Operator, SsaValue Left, SsaValue Right) : ValueOperation;
    public sealed record Convert(SsaValue Operand, bool Bitcast) : ValueOperation;
    public sealed record Construct(IReadOnlyList<SsaValue> Components) : ValueOperation;
    public sealed record Select(SsaValue Condition, SsaValue Accept, SsaValue Reject) : ValueOperation;
    public sealed record Access(SsaValue Base, SsaValue Index) : ValueOperation;
    public sealed record Member(SsaValue Base, string Name) : ValueOperation;
    public sealed record Swizzle(SsaValue Vector, string Components) : ValueOperation;
    public sealed record Builtin(string Function, IReadOnlyList<SsaValue> Arguments, ShaderType ReturnType,
        SpirvAtomicMemory? AtomicMemory = null, SpirvMemoryAccess? MemoryAccess = null) : ValueOperation;
    public sealed record Call(string Function, IReadOnlyList<SsaValue> Arguments, ShaderType ReturnType,
        SpirvAtomicMemory? AtomicMemory = null, SpirvMemoryAccess? MemoryAccess = null, ShaderEffects CalleeEffects = ShaderEffects.None) : ValueOperation;
    public sealed record Load(SsaValue Pointer, SpirvMemoryAccess? MemoryAccess = null) : ValueOperation;
    public sealed record Store(SsaValue Pointer, SsaValue Value, SpirvMemoryAccess? MemoryAccess = null) : ValueOperation;
    public sealed record MeshStore(MeshOutputField Field, SsaValue Index, SsaValue Value) : ValueOperation;
    public sealed record InterfaceLoad(EntryInterfaceField Field) : ValueOperation;
    public sealed record InterfaceStore(EntryInterfaceField Field, SsaValue Value) : ValueOperation;
    public sealed record MeshSetOutputs(SsaValue Vertices, SsaValue Primitives) : ValueOperation;
    public sealed record Demote : ValueOperation;
    public sealed record HelperInvocation : ValueOperation;
    public sealed record Barrier(bool Control, bool Storage, bool Workgroup, bool Texture, bool Subgroup,
        SpirvBarrierMemory? NativeMemory = null) : ValueOperation;

    public IEnumerable<SsaValue> Operands => this switch {
        Let l => [l.Value], Unary u => [u.Operand], Binary b => [b.Left, b.Right], Convert c => [c.Operand],
        Construct c => c.Components, Select s => [s.Condition, s.Accept, s.Reject],
        Access a => [a.Base, a.Index], Member m => [m.Base], Swizzle s => [s.Vector],
        Load l => [l.Pointer], Store s => [s.Pointer, s.Value], MeshStore s => [s.Index, s.Value],
        InterfaceStore s => [s.Value],
        MeshSetOutputs s => [s.Vertices, s.Primitives], Builtin b => b.Arguments, Call c => c.Arguments, _ => []
    };

    public ValueOperation Map(Func<SsaValue, SsaValue> value) => this switch {
        Let l => l with { Value = value(l.Value) }, Unary u => u with { Operand = value(u.Operand) }, Binary b => b with { Left = value(b.Left), Right = value(b.Right) },
        Convert c => c with { Operand = value(c.Operand) }, Access a => a with { Base = value(a.Base), Index = value(a.Index) },
        Construct c => c with { Components = c.Components.Select(value).ToArray() },
        Select s => s with { Condition = value(s.Condition), Accept = value(s.Accept), Reject = value(s.Reject) },
        Member m => m with { Base = value(m.Base) }, Swizzle s => s with { Vector = value(s.Vector) },
        Load l => l with { Pointer = value(l.Pointer) }, Store s => s with { Pointer = value(s.Pointer), Value = value(s.Value) },
        InterfaceStore s => s with { Value = value(s.Value) },
        MeshStore s => s with { Index = value(s.Index), Value = value(s.Value) },
        MeshSetOutputs s => s with { Vertices = value(s.Vertices), Primitives = value(s.Primitives) },
        Builtin b => b with { Arguments = b.Arguments.Select(value).ToArray() },
        Call c => c with { Arguments = c.Arguments.Select(value).ToArray() }, _ => this
    };
}

internal sealed class ControlFlowEdge(int target, IEnumerable<SsaValue>? arguments = null)
{
    public int Target { get; } = target;
    public List<SsaValue> Arguments { get; } = arguments?.ToList() ?? [];
}

internal sealed record ControlFlowCase(IReadOnlyList<Expression.Literal> Values, ControlFlowEdge Edge);

internal abstract record ControlFlowTerminator
{
    public sealed record Branch(ControlFlowEdge Edge) : ControlFlowTerminator;
    public sealed record Conditional(SsaValue Condition, ControlFlowEdge Accept, ControlFlowEdge Reject) : ControlFlowTerminator;
    public sealed record Switch(SsaValue Selector, IReadOnlyList<ControlFlowCase> Cases, ControlFlowEdge Default) : ControlFlowTerminator;
    public sealed record Return(SsaValue? Value = null) : ControlFlowTerminator;
    public sealed record TaskDispatch(SsaValue Dimensions, string Payload, SourceSpan Span = default) : ControlFlowTerminator;
    public sealed record Unreachable(SourceSpan Span = default) : ControlFlowTerminator;
    public sealed record InvocationKill(SourceSpan Span = default, bool ExplicitTermination = false) : ControlFlowTerminator;
    public ShaderEffects Effects => this is TaskDispatch ? ShaderBuiltinEffects.Barrier(true) | ShaderEffects.InvocationTermination
        : this is InvocationKill ? ShaderEffects.Convergent | ShaderEffects.InvocationTermination : ShaderEffects.None;

    public IEnumerable<ControlFlowEdge> Edges => this switch {
        Branch b => [b.Edge], Conditional c => [c.Accept, c.Reject], Switch s => s.Cases.Select(c => c.Edge).Append(s.Default), _ => []
    };
    public IEnumerable<SsaValue> Operands => this switch {
        Conditional c => [c.Condition], Switch s => [s.Selector], Return { Value: { } value } => [value], TaskDispatch d => [d.Dimensions], _ => []
    };
    public ControlFlowTerminator Map(Func<SsaValue, SsaValue> value) => this switch {
        Conditional c => c with { Condition = value(c.Condition) }, Switch s => s with { Selector = value(s.Selector) },
        Return { Value: { } v } => new Return(value(v)), TaskDispatch d => d with { Dimensions = value(d.Dimensions) }, _ => this
    };
}
