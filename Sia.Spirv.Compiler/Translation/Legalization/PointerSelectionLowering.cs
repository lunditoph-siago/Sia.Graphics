using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Legalization;

// Pointer values have no WGSL select representation. Keep the native
// choice until each memory operation, then execute only its selected arm.
// Native Select conditions and AccessChain indices are already snapshots.
internal sealed class PointerSelectionLowering(Module module, DiagnosticStage stage = DiagnosticStage.Validation,
    bool nativeVariablePointers = false, bool fullVariablePointers = true,
    IReadOnlyDictionary<string, string>? descriptorSnapshots = null,
    Func<Expression, ShaderType, SpirvMemoryAccess?, Expression>? memoryLoad = null,
    Func<Expression, Expression, SpirvMemoryAccess?, Statement>? memoryStore = null,
    Func<Block, Expression, Expression, bool, SpirvMemoryAccess?, bool>? aggregateCopy = null)
{
    private readonly HashSet<string> names = new(StringComparer.Ordinal);
    private Dictionary<string, Expression> aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Expression> indexSnapshots = new(StringComparer.Ordinal);
    private int next;
    private ShaderException Error(string message) => new(stage, message);
    private string Name()
    {
        string name; do name = "sia_pointer_value_" + next++; while (!names.Add(name)); return name;
    }
    public Module Run()
    {
        foreach (var name in module.Globals.Select(g => g.Name).Concat(module.Constants.Select(c => c.Name))
            .Concat(module.Structures.Select(s => s.Name)).Concat(module.Functions.Select(f => f.Name))) names.Add(name);
        foreach (var function in module.Functions)
        {
            foreach (var argument in function.Arguments) names.Add(argument.Name);
            Reserve(function.Body);
        }
        var output = new Module { VulkanMemoryModel = module.VulkanMemoryModel, WorkgroupInitializationRequired = module.WorkgroupInitializationRequired };
        output.Structures.AddRange(module.Structures); output.Constants.AddRange(module.Constants); output.Globals.AddRange(module.Globals);
        output.Enables.UnionWith(module.Enables); output.DiagnosticFilters.AddRange(module.DiagnosticFilters);
        foreach (var function in module.Functions)
        {
            aliases = new(StringComparer.Ordinal);
            var copy = new ShaderFunction(function.Name) {
                Stage = function.Stage, ReturnType = function.ReturnType, ReturnBinding = function.ReturnBinding,
                WorkgroupSize = function.WorkgroupSize.ToArray(), TaskPayload = function.TaskPayload, MeshOutput = function.MeshOutput,
                EarlyDepthTest = function.EarlyDepthTest, ConservativeDepth = function.ConservativeDepth, Body = Body(function.Body)
            };
            copy.Arguments.AddRange(function.Arguments); copy.DiagnosticFilters.AddRange(function.DiagnosticFilters); output.Functions.Add(copy);
        }
        return output;
    }
    private void Reserve(Block block)
    {
        foreach (var statement in block.Statements) switch (statement)
        {
            case Statement.Declare d:
                names.Add(d.Name);
                if (!d.Mutable && d.Initializer is not null) indexSnapshots.TryAdd(d.Name, d.Initializer);
                break;
            case Statement.Nested n: Reserve(n.Body); break;
            case Statement.If i: Reserve(i.Accept); Reserve(i.Reject); break;
            case Statement.Loop l: Reserve(l.Body); Reserve(l.Continuing); break;
            case Statement.Switch s: foreach (var c in s.Cases) Reserve(c.Body); break;
        }
    }
    private static Expression Project(Expression pointer, Func<Expression, Expression> leaf) => pointer is Expression.Select s
        ? new Expression.Select(s.Condition, Project(s.Accept, leaf), Project(s.Reject, leaf)) { Span = pointer.Span } : leaf(pointer);
    internal static bool Null(Expression expression) => expression switch {
        Expression.Construct { Type: ShaderType.Pointer, Components.Count: 0 } => true,
        Expression.Unary { Operator: "&" or "*" } u => Null(u.Operand),
        Expression.Access a => Null(a.Base), Expression.Member m => Null(m.Base),
        Expression.Select s => Null(s.Accept) && Null(s.Reject), _ => false
    };
    private Expression Pointer(Expression expression, Block prelude) => expression switch
    {
        Expression.Construct { Components.Count: 0 } => expression,
        Expression.Reference r when aliases.TryGetValue(r.Name, out var alias) => alias,
        Expression.Reference => expression,
        Expression.Select s => PointerSelect(s, prelude),
        Expression.Access a => Project(Pointer(a.Base, prelude), p => new Expression.Access(p, a.Index, IndexedType(p.Type)) { Span = a.Span }),
        Expression.Member m => Project(Pointer(m.Base, prelude), p => new Expression.Member(p, m.Name, MemberType(p.Type, m.Name)) { Span = m.Span }),
        Expression.Unary { Operator: "&" or "*" } u => Project(Pointer(u.Operand, prelude), p => new Expression.Unary(u.Operator, p,
            p.Type is ShaderType.Pointer ? p.Type : u.Type) { Span = u.Span }),
        _ => throw Error("Unsupported selected-pointer provenance.")
    };
    private static ShaderType ProjectType(ShaderType parent, Func<ShaderType, ShaderType> project) => parent is ShaderType.Pointer p
        ? p with { Base = project(p.Base) } : project(parent);
    private ShaderType IndexedType(ShaderType parent) => ProjectType(parent, type => type switch
    {
        ShaderType.Array a => a.Element, ShaderType.BindingArray a => a.Element,
        ShaderType.Vector v => v.Component, ShaderType.Matrix m => new ShaderType.Vector(m.Rows, m.Component),
        _ => throw Error("Invalid selected-pointer index projection.")
    });
    private ShaderType MemberType(ShaderType parent, string name) => ProjectType(parent, type => type is ShaderType.Structure s
        ? s.Members.Single(m => m.Name == name).Type : throw Error("Invalid selected-pointer member projection."));
    private Expression PointerSelect(Expression.Select select, Block prelude)
    {
        var pointer = (ShaderType.Pointer)select.Type;
        if (nativeVariablePointers && pointer.Space is not (AddressSpace.Storage or AddressSpace.Workgroup))
            throw Error("Variable pointers require storage or workgroup memory.");
        if (nativeVariablePointers && pointer.Space == AddressSpace.Workgroup && !fullVariablePointers)
            throw Error("Workgroup pointer selection requires the full VariablePointers capability.");
        if (nativeVariablePointers && ContainsMatrix(pointer.Base)) throw Error("Variable pointers cannot point to objects containing matrices.");
        var accept = Pointer(select.Accept, prelude); var reject = Pointer(select.Reject, prelude);
        if (nativeVariablePointers && !fullVariablePointers)
        {
            var roots = StorageStructures(accept).Concat(StorageStructures(reject)).Distinct(StringComparer.Ordinal).ToArray();
            if (roots.Length > 1 || roots.Any(r => r is null))
                throw Error("VariablePointersStorageBuffer selections must remain within one storage buffer structure.");
        }
        return new Expression.Select(Expr(select.Condition, prelude), accept, reject) { Span = select.Span };
    }
    private static bool ContainsAtomic(ShaderType type) => type switch {
        ShaderType.Atomic => true, ShaderType.Array a => ContainsAtomic(a.Element),
        ShaderType.Structure s => s.Members.Any(m => ContainsAtomic(m.Type)), _ => false
    };
    private static bool ContainsMatrix(ShaderType type) => type switch
    {
        ShaderType.Matrix => true, ShaderType.Array a => ContainsMatrix(a.Element),
        ShaderType.Structure s => s.Members.Any(m => ContainsMatrix(m.Type)), _ => false
    };
    private IEnumerable<string?> StorageStructures(Expression expression) => expression switch
    {
        Expression.Reference r => [module.Globals.Any(g => g.Name == r.Name && g.Type is ShaderType.Structure) ? r.Name : null],
        Expression.Select s => StorageStructures(s.Accept).Concat(StorageStructures(s.Reject)),
        Expression.Access { Base.Type: ShaderType.Pointer { Base: ShaderType.BindingArray } } a => [DescriptorStructure(a)],
        Expression.Access a => StorageStructures(a.Base), Expression.Member m => StorageStructures(m.Base),
        Expression.Unary u => StorageStructures(u.Operand), _ => []
    };
    private string? DescriptorStructure(Expression.Access access)
    {
        if (access.Base is not Expression.Reference root || !module.Globals.Any(g => g.Name == root.Name
            && g.Type is ShaderType.BindingArray { Element: ShaderType.Structure })) return null;
        string? index = DescriptorIndex(access.Index, []);
        return index is null ? null : root.Name + "[" + index + "]";
    }
    private string? DescriptorIndex(Expression expression, HashSet<string> visited)
    {
        if (expression is Expression.Reference reference)
        {
            if (!visited.Add(reference.Name)) return null;
            if (descriptorSnapshots?.TryGetValue(reference.Name, out string? identity) == true) return identity;
            return indexSnapshots.TryGetValue(reference.Name, out var snapshot) ? DescriptorIndex(snapshot, visited) : null;
        }
        if (expression is not Expression.Literal literal) return null;
        string? value = literal.Value switch
        {
            uint n => n.ToString(), int n when n >= 0 => n.ToString(),
            ulong n => n.ToString(), long n when n >= 0 => n.ToString(),
            ushort n => n.ToString(), short n when n >= 0 => n.ToString(), _ => null
        };
        return value is null ? null : "n:" + value;
    }
    private void SelectedStore(Expression pointer, Action<Expression, Block> leaf, Block block)
    {
        // Executed null dereferences are undefined in SPIR-V. Omit that
        // arm without evaluating an unselected non-null memory access.
        if (Null(pointer)) return;
        if (pointer is not Expression.Select s) { leaf(pointer, block); return; }
        var accept = new Block(); var reject = new Block();
        SelectedStore(s.Accept, leaf, accept); SelectedStore(s.Reject, leaf, reject);
        block.Statements.Add(new Statement.If(s.Condition, accept, reject) { Span = pointer.Span });
    }
    private Expression Load(Expression pointer, ShaderType type, SpirvMemoryAccess? memory, Block block)
    {
        Expression Leaf(Expression p, Block target)
        {
            if (Null(p)) return new Expression.Construct(type, []);
            if (p.Type is ShaderType.Pointer value && ContainsAtomic(value.Base) && value.Base is not ShaderType.Atomic)
            {
                string copyName = Name(); var copy = new Expression.Reference(copyName, new ShaderType.Pointer(type, AddressSpace.Function));
                target.Statements.Add(new Statement.Declare(copyName, type, null));
                if (aggregateCopy?.Invoke(target, p, copy, true, memory) != true)
                    throw Error("Selected atomic aggregate requires explicit memory legalization."); return new Expression.Load(copy);
            }
            return memoryLoad?.Invoke(p, type, memory) ?? new Expression.Load(p) { MemoryAccess = memory };
        }
        if (pointer is not Expression.Select) return Leaf(pointer, block);
        string name = Name(); var place = new Expression.Reference(name, new ShaderType.Pointer(type, AddressSpace.Function));
        block.Statements.Add(new Statement.Declare(name, type, null));
        SelectedStore(pointer, (p, target) => target.Statements.Add(new Statement.Store(place, Leaf(p, target))), block);
        return new Expression.Load(place);
    }
    private void Store(Expression pointer, Expression value, SpirvMemoryAccess? memory, SourceSpan span, Block block) =>
        SelectedStore(pointer, (p, target) =>
        {
            if (aggregateCopy?.Invoke(target, p, value, false, memory) != true)
                target.Statements.Add((memoryStore?.Invoke(p, value, memory) ?? new Statement.Store(p, value) { MemoryAccess = memory }) with { Span = span });
        }, block);
    private Expression Call(Expression.Call call, Expression[] arguments, Block block)
    {
        // MemoryAccess marks an ordinary native operation represented by
        // an atomic WGSL builtin. Actual atomic opcodes carry AtomicMemory.
        if (call.MemoryAccess is { } memory && call.Function is "atomicLoad" or "atomicStore")
        {
            Expression pointer = arguments[0] is Expression.Unary { Operator: "&" } address ? address.Operand
                : Project(arguments[0], p => p is Expression.Unary { Operator: "&" } a ? a.Operand : new Expression.Unary("*", p, p.Type));
            if (call.Function == "atomicLoad") return Load(pointer, call.Type, memory, block);
            Store(pointer, arguments[1], memory, call.Span, block); return new Expression.Construct(call.Type, []);
        }
        int index = Array.FindIndex(arguments, a => a.Type is ShaderType.Pointer && (a is Expression.Select || Null(a)));
        if (index < 0) return call with { Arguments = arguments };
        Expression.Reference? result = null;
        if (call.Type is not ShaderType.Void)
        {
            string name = Name(); result = new(name, new ShaderType.Pointer(call.Type, AddressSpace.Function));
            block.Statements.Add(new Statement.Declare(name, call.Type, null));
        }
        Dispatch(arguments, block);
        return result is null ? new Expression.Construct(call.Type, []) : new Expression.Load(result);

        void Dispatch(Expression[] args, Block target)
        {
            if (args.Any(a => a.Type is ShaderType.Pointer && Null(a)))
            {
                if (!call.Function.StartsWith("atomic", StringComparison.Ordinal) && call.Function != "spirvAtomicCompareExchange")
                    throw Error("Null helper arguments require equivalent call specialization.");
                return;
            }
            int selected = Array.FindIndex(args, a => a.Type is ShaderType.Pointer && a is Expression.Select);
            if (selected < 0)
            {
                var value = call with { Arguments = args };
                target.Statements.Add(result is null ? new Statement.Evaluate(value) : new Statement.Store(result, value)); return;
            }
            var choice = (Expression.Select)args[selected]; var accept = new Block(); var reject = new Block();
            var left = args.ToArray(); left[selected] = choice.Accept; Dispatch(left, accept);
            var right = args.ToArray(); right[selected] = choice.Reject; Dispatch(right, reject);
            target.Statements.Add(new Statement.If(choice.Condition, accept, reject));
        }
    }
    private Expression Capture(Expression value, Block block)
    {
        string name = Name(); block.Statements.Add(new Statement.Declare(name, value.Type, value, false));
        return new Expression.Reference(name, value.Type);
    }
    private Expression[] Values(IReadOnlyList<Expression> input, Block block)
    {
        var items = input.Select(e => { var p = new Block(); return (Value: Expr(e, p), Prelude: p); }).ToArray();
        bool capture = items.Any(i => i.Prelude.Statements.Count != 0);
        return items.Select(item =>
        {
            block.Statements.AddRange(item.Prelude.Statements);
            return capture && item.Value.Type is not ShaderType.Pointer ? Capture(item.Value, block) : item.Value;
        }).ToArray();
    }
    private Expression Expr(Expression expression, Block block)
    {
        if (expression.Type is ShaderType.Pointer) return Pointer(expression, block);
        Expression E(Expression value) => Expr(value, block);
        Expression result = expression switch
        {
            Expression.Reference or Expression.Literal or Expression.HelperInvocation => expression,
            Expression.Load l => Load(Pointer(l.Pointer, block), l.Type, l.MemoryAccess, block),
            Expression.Call c => Call(c, Values(c.Arguments, block), block),
            Expression.Construct c => c with { Components = Values(c.Components, block) },
            Expression.Convert c => c with { Operand = E(c.Operand) },
            Expression.Unary u => u with { Operand = E(u.Operand) },
            Expression.Member m => m with { Base = E(m.Base) },
            Expression.Swizzle s => s with { Vector = E(s.Vector) },
            Expression.Access a => Access(a, block),
            Expression.Binary b => Binary(b, block),
            Expression.Select s => Select(s, block),
            _ => throw Error("Unsupported expression in pointer selection lowering.")
        };
        return result with { Span = expression.Span };
    }
    private Expression Access(Expression.Access access, Block block)
    {
        var values = Values([access.Base, access.Index], block); return access with { Base = values[0], Index = values[1] };
    }
    private Expression Select(Expression.Select select, Block block)
    {
        var values = Values([select.Condition, select.Accept, select.Reject], block); return new Expression.Select(values[0], values[1], values[2]);
    }
    private Expression Binary(Expression.Binary binary, Block block)
    {
        var left = Expr(binary.Left, block); var rightPrelude = new Block(); var right = Expr(binary.Right, rightPrelude);
        if (rightPrelude.Statements.Count == 0) return binary with { Left = left, Right = right };
        if (binary.Operator is not ("&&" or "||"))
        {
            left = Capture(left, block); block.Statements.AddRange(rightPrelude.Statements); return binary with { Left = left, Right = right };
        }
        string name = Name(); var place = new Expression.Reference(name, new ShaderType.Pointer(ShaderType.Bool, AddressSpace.Function));
        block.Statements.Add(new Statement.Declare(name, ShaderType.Bool, left));
        rightPrelude.Statements.Add(new Statement.Store(place, right));
        block.Statements.Add(new Statement.If(binary.Operator == "&&" ? new Expression.Load(place)
            : new Expression.Unary("!", new Expression.Load(place), ShaderType.Bool), rightPrelude, new()));
        return new Expression.Load(place);
    }
    private Block Body(Block input, bool scope = true)
    {
        var saved = aliases; if (scope) aliases = new(saved, StringComparer.Ordinal); var output = new Block();
        output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        Expression E(Expression expression) => Expr(expression, output);
        foreach (var statement in input.Statements) switch (statement)
        {
            case Statement.Declare { Type: ShaderType.Pointer, Mutable: false, Initializer: { } value } d:
                aliases[d.Name] = Pointer(value, output); break;
            case Statement.Declare d: output.Statements.Add(d with { Initializer = d.Initializer is null ? null : E(d.Initializer) }); break;
            case Statement.Store s:
                Expression target = Pointer(s.Target, output); Expression source = E(s.Value);
                Store(target, source, s.MemoryAccess, s.Span, output); break;
            case Statement.Evaluate e:
                Expression evaluated = E(e.Value); if (evaluated is Expression.Call) output.Statements.Add(e with { Value = evaluated }); break;
            case Statement.Nested n: output.Statements.Add(n with { Body = Body(n.Body) }); break;
            case Statement.If i: output.Statements.Add(i with { Condition = E(i.Condition), Accept = Body(i.Accept), Reject = Body(i.Reject) }); break;
            case Statement.Switch s: output.Statements.Add(s with { Selector = E(s.Selector), Cases = s.Cases.Select(c => c with { Body = Body(c.Body) }).ToArray() }); break;
            case Statement.Loop l:
                var outer = aliases; aliases = new(outer, StringComparer.Ordinal);
                var body = Body(l.Body, false); var tail = Body(l.Continuing, false);
                Expression? breakIf = l.BreakIf is null ? null : Expr(l.BreakIf, tail); aliases = outer;
                output.Statements.Add(l with { Body = body, Continuing = tail, BreakIf = breakIf }); break;
            case Statement.Return r: output.Statements.Add(r with { Value = r.Value is null ? null : E(r.Value) }); break;
            default: output.Statements.Add(statement); break;
        }
        if (scope) aliases = saved; return output;
    }
}
