using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Express native memory operations without weakening their requirements.</summary>
internal sealed class WgslMemoryLowering(Module input)
{
    private readonly Module output = new() { VulkanMemoryModel = input.VulkanMemoryModel, WorkgroupInitializationRequired = input.WorkgroupInitializationRequired };
    private readonly HashSet<string> names = new(StringComparer.Ordinal);
    private readonly HashSet<string> globals = input.Globals.Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
    private readonly HashSet<string> volatileGlobals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MemoryDecorations> bufferMemory = input.Globals.ToDictionary(g => g.Name,
        g => g.MemoryDecorations | TypeMemory(g.Type), StringComparer.Ordinal);
    private static MemoryDecorations TypeMemory(ShaderType type) => type switch
    {
        ShaderType.Pointer p => TypeMemory(p.Base), ShaderType.Array a => TypeMemory(a.Element), ShaderType.BindingArray a => TypeMemory(a.Element),
        ShaderType.Structure s => s.Members.Aggregate(MemoryDecorations.None, (flags, m) => flags | m.MemoryDecorations | TypeMemory(m.Type)),
        _ => MemoryDecorations.None
    };
    private HashSet<string> localRoots = new(StringComparer.Ordinal);
    private readonly HashSet<string> singleInvocationFunctions = SingleInvocationFunctions(input);
    private bool singleInvocation;
    private bool collecting;
    private int next;
    public static Module Run(Module input)
    {
        var pass = new WgslMemoryLowering(input);
        foreach (var global in input.Globals)
            if (TypeMemory(global.Type) != MemoryDecorations.None && global.Space != AddressSpace.Storage
                && (global.Space != AddressSpace.Workgroup || (TypeMemory(global.Type) & MemoryDecorations.Volatile) != 0))
                throw Error("Native member memory decorations require a WGSL storage-buffer owner.");
        pass.output.Enables.UnionWith(input.Enables); pass.output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        pass.output.Structures.AddRange(input.Structures); pass.output.Constants.AddRange(input.Constants); pass.output.Globals.AddRange(input.Globals);
        pass.names.UnionWith(input.Structures.Select(s => s.Name).Concat(input.Constants.Select(c => c.Name))
            .Concat(input.Globals.Select(g => g.Name)).Concat(input.Functions.Select(f => f.Name)));
        foreach (var f in input.Functions)
        {
            pass.names.UnionWith(f.Arguments.Select(a => a.Name)); pass.Reserve(f.Body);
        }
        // A later access may lift a whole buffer to volatile. Discover every
        // requirement before deciding whether an earlier strong CAS may retry.
        pass.collecting = true;
        foreach (var f in input.Functions)
        {
            pass.singleInvocation = pass.singleInvocationFunctions.Contains(f.Name);
            pass.localRoots = f.Arguments.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
            pass.Reserve(f.Body, pass.localRoots); _ = pass.Body(f.Body);
        }
        pass.collecting = false;
        foreach (var f in input.Functions)
        {
            pass.singleInvocation = pass.singleInvocationFunctions.Contains(f.Name);
            pass.localRoots = f.Arguments.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
            pass.Reserve(f.Body, pass.localRoots);
            var copy = new ShaderFunction(f.Name) { Stage = f.Stage, ReturnType = f.ReturnType, ReturnBinding = f.ReturnBinding,
                TaskPayload = f.TaskPayload, MeshOutput = f.MeshOutput, WorkgroupSize = f.WorkgroupSize.ToArray(),
                EarlyDepthTest = f.EarlyDepthTest, ConservativeDepth = f.ConservativeDepth, Body = pass.Body(f.Body) };
            copy.Arguments.AddRange(f.Arguments); copy.DiagnosticFilters.AddRange(f.DiagnosticFilters); pass.output.Functions.Add(copy);
        }
        for (int i = 0; i < pass.output.Globals.Count; i++)
        {
            var global = pass.output.Globals[i];
            pass.output.Globals[i] = global with { MemoryDecorations = global.Space == AddressSpace.Workgroup ? MemoryDecorations.None
                : pass.bufferMemory[global.Name] | (pass.volatileGlobals.Contains(global.Name) ? MemoryDecorations.Volatile : MemoryDecorations.None) };
        }
        return pass.output;
    }
    private string Fresh() { string name; do name = "sia_atomic_" + next++; while (!names.Add(name)); return name; }
    private static ShaderException Error(string message, SourceSpan span = default) => new(DiagnosticStage.WgslWrite, message, span);
    private static HashSet<string> SingleInvocationFunctions(Module module)
    {
        var calls = module.Functions.ToDictionary(f => f.Name, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        void Expr(Expression e, HashSet<string> found)
        {
            if (e is Expression.Call call && call.Binding != CallBinding.Builtin && calls.ContainsKey(call.Function)) found.Add(call.Function);
            IEnumerable<Expression> children = e switch
            {
                Expression.Call c => c.Arguments, Expression.Unary u => [u.Operand], Expression.Load l => [l.Pointer],
                Expression.Binary b => [b.Left, b.Right], Expression.Construct c => c.Components, Expression.Convert c => [c.Operand],
                Expression.Access a => [a.Base, a.Index], Expression.Member m => [m.Base], Expression.Swizzle s => [s.Vector],
                Expression.Select s => [s.Condition, s.Accept, s.Reject], _ => []
            };
            foreach (var child in children) Expr(child, found);
        }
        void Body(Block block, HashSet<string> found)
        {
            foreach (var s in block.Statements)
            {
                IEnumerable<Expression> expressions = s switch
                {
                    Statement.Declare { Initializer: { } e } => [e], Statement.Store st => [st.Target, st.Value], Statement.Evaluate e => [e.Value],
                    Statement.If i => [i.Condition], Statement.Loop { BreakIf: { } e } => [e], Statement.Switch sw => [sw.Selector], Statement.Return { Value: { } e } => [e], _ => []
                };
                foreach (var e in expressions) Expr(e, found);
                IEnumerable<Block> bodies = s switch { Statement.Nested n => [n.Body], Statement.If i => [i.Accept, i.Reject], Statement.Loop l => [l.Body, l.Continuing], Statement.Switch sw => sw.Cases.Select(c => c.Body), _ => [] };
                foreach (var body in bodies) Body(body, found);
            }
        }
        foreach (var f in module.Functions) Body(f.Body, calls[f.Name]);
        var small = new HashSet<string>(StringComparer.Ordinal); var large = new HashSet<string>(StringComparer.Ordinal);
        void Reach(string name, HashSet<string> found) { if (found.Add(name)) foreach (string callee in calls[name]) Reach(callee, found); }
        foreach (var f in module.Functions.Where(f => f.Stage is not null))
        {
            bool one = f.Stage is ShaderStage.Compute or ShaderStage.Task or ShaderStage.Mesh && f.WorkgroupSize.All(e =>
                ConstantEvaluator.TryEvaluateRuntime(new Expression.Convert(ShaderType.U32, e), out var value) && value is Expression.Literal { Value: uint n } && n == 1);
            Reach(f.Name, one ? small : large);
        }
        small.ExceptWith(large); return small;
    }
    private void Reserve(Block block, HashSet<string>? locals = null)
    {
        foreach (var s in block.Statements)
            switch (s)
            {
                case Statement.Declare d: names.Add(d.Name); locals?.Add(d.Name); break;
                case Statement.Nested n: Reserve(n.Body, locals); break;
                case Statement.If i: Reserve(i.Accept, locals); Reserve(i.Reject, locals); break;
                case Statement.Loop l: Reserve(l.Body, locals); Reserve(l.Continuing, locals); break;
                case Statement.Switch sw: foreach (var c in sw.Cases) Reserve(c.Body, locals); break;
            }
    }
    private Expression Call(Expression.Call call)
    {
        if (call.Binding == CallBinding.Function) return call with { Arguments = call.Arguments.Select(Expr).ToArray() };
        if (call.MemoryAccess is { } access)
        {
            int pointerIndex = call.Function is "coopStore" or "coopStoreT" ? 1 : 0;
            Access(access, call.Arguments[pointerIndex], call.Span);
        }
        if (call.AtomicMemory is { } memory)
        {
            if (memory.Scope == 0)
                throw Error("Native atomic scope has no equivalent WGSL builtin scope.", call.Span);
            // Relaxed atomics impose no ordering on other storage classes. A
            // wider atomic scope strengthens ordering of the atomic location;
            // it does not remove required behavior from a race-free program.
            // Workgroup memory cannot be accessed outside its owning group.
            // Keep the original operands for native output, and use WGSL's
            // address-space scope only when no ordering/availability is lost.
            const uint memoryClasses = 64 | 128 | 256 | 512 | 1024 | 2048 | 4096;
            if ((memory.Semantics & ~(memoryClasses | 32768u)) != 0 || (memory.UnequalSemantics.GetValueOrDefault() & ~(memoryClasses | 32768u)) != 0)
                throw Error("Native atomic memory semantics have no equivalent WGSL relaxed builtin; preserve SPIR-V output.", call.Span);
            if (((memory.Semantics | memory.UnequalSemantics.GetValueOrDefault()) & 32768) != 0)
                Access(new(1), call.Arguments[0], call.Span);
        }
        if (collecting) { foreach (var argument in call.Arguments) _ = Expr(argument); return call; }
        if (call.Function == "spirvAtomicCompareExchange" &&
            (((call.AtomicMemory?.Semantics ?? 0) & 32768) != 0 || StorageRoot(call.Arguments[0]) is string root
                && ((bufferMemory[root] & MemoryDecorations.Volatile) != 0 || volatileGlobals.Contains(root))))
            throw Error("Native volatile strong compare/exchange cannot be duplicated by a WGSL weak retry loop.", call.Span);
        var arguments = call.Arguments.Select(Expr).ToArray();
        if (call.Function != "spirvAtomicCompareExchange") return call with { Arguments = arguments, AtomicMemory = null, MemoryAccess = null };
        var helper = new ShaderFunction(Fresh()) { ReturnType = call.Type };
        var actual = new List<Expression>();
        Expression Capture(Expression value)
        {
            string name = Fresh(); helper.Arguments.Add(new(name, value.Type)); actual.Add(value);
            return new Expression.Reference(name, value.Type);
        }
        Expression Pointer(Expression pointer) => pointer switch
        {
            Expression.Reference r when globals.Contains(r.Name) => r,
            Expression.Unary { Operator: "&" or "*" } u => u with { Operand = Pointer(u.Operand) },
            Expression.Member m => m with { Base = Pointer(m.Base) },
            Expression.Access a => a with { Base = Pointer(a.Base), Index = Capture(a.Index) },
            _ => throw Error("Native compare/exchange pointer requires a module variable access path for WGSL lowering.", call.Span)
        };
        Expression pointer = Pointer(arguments[0]), expected = Capture(arguments[1]), desired = Capture(arguments[2]);
        string resultName = Fresh();
        var resultType = new ShaderType.Structure(Fresh(), [new StructMember("old_value", call.Type), new StructMember("exchanged", ShaderType.Bool)])
        { BuiltinResult = BuiltinResultKind.AtomicCompareExchange };
        var result = new Expression.Reference(resultName, resultType);
        var old = new Expression.Member(result, "old_value", call.Type);
        var done = new Expression.Binary("||", new Expression.Member(result, "exchanged", ShaderType.Bool),
            new Expression.Binary("!=", old, expected, ShaderType.Bool), ShaderType.Bool);
        string observedName = Fresh();
        var observed = new Expression.Reference(observedName, new ShaderType.Pointer(call.Type, AddressSpace.Function));
        var exit = new Block(); exit.Statements.Add(new Statement.Store(observed, old)); exit.Statements.Add(new Statement.Break());
        var loop = new Block();
        loop.Statements.Add(new Statement.Declare(resultName, resultType,
            new Expression.Call("atomicCompareExchangeWeak", [pointer, expected, desired], resultType, CallBinding.Builtin), false));
        loop.Statements.Add(new Statement.If(done, exit, new()));
        helper.Body.Statements.Add(new Statement.Declare(observedName, call.Type, new Expression.Construct(call.Type, [])));
        helper.Body.Statements.Add(new Statement.Loop(loop, new()));
        helper.Body.Statements.Add(new Statement.Return(new Expression.Load(observed)));
        output.Functions.Add(helper);
        return new Expression.Call(helper.Name, actual, call.Type) { Span = call.Span };
    }
    private Expression Expr(Expression e) => e switch
    {
        Expression.Load l => Load(l), Expression.Unary u => u with { Operand = Expr(u.Operand) },
        Expression.Binary b => b with { Left = Expr(b.Left), Right = Expr(b.Right) }, Expression.Call c => Call(c),
        Expression.Construct c => c with { Components = c.Components.Select(Expr).ToArray() }, Expression.Convert c => c with { Operand = Expr(c.Operand) },
        Expression.Access a => a with { Base = Expr(a.Base), Index = Expr(a.Index) }, Expression.Member m => m with { Base = Expr(m.Base) },
        Expression.Swizzle s => s with { Vector = Expr(s.Vector) }, Expression.Select s => s with { Condition = Expr(s.Condition), Accept = Expr(s.Accept), Reject = Expr(s.Reject) },
        _ => e
    };
    private void Access(SpirvMemoryAccess? memory, Expression pointer, SourceSpan span)
    {
        if (memory is null) return;
        string? root = StorageRoot(pointer);
        if ((memory.Flags & (8 | 16)) != 0)
        {
            // Queue-family pointer visibility/availability is the Vulkan
            // lowering of the pinned WGSL @coherent buffer attribute. A Device
            // scope cannot be narrowed to it. Other scopes need their own proof.
            bool workgroup = SharedRoot(pointer, AddressSpace.Workgroup) is not null
                && (memory.AvailableScope is null or 2) && (memory.VisibleScope is null or 2);
            if (!workgroup && (root is null || memory.AvailableScope is uint a && a != 5 || memory.VisibleScope is uint v && v != 5))
                throw Error("Native per-access availability/visibility requirements have no equivalent WGSL memory access.", span);
            if (!workgroup) bufferMemory[root!] |= MemoryDecorations.Coherent;
        }
        // Alignment and nontemporal are promises/hints. Shared WGSL memory
        // already participates in inter-thread ordering; retaining additional
        // non-private accesses only strengthens ordering for race-free code.
        if ((memory.Flags & 1) == 0) return;
        if (root is not null) { volatileGlobals.Add(root); return; }
        throw Error("Native volatile memory access requires a proven WGSL storage-buffer root.", span);
    }
    private string? StorageRoot(Expression pointer) => SharedRoot(pointer, AddressSpace.Storage);
    private string? SharedRoot(Expression pointer, AddressSpace space)
    {
        Expression root = pointer;
        while (true)
        {
            if (root is Expression.Unary { Operator: "&" or "*" } u) root = u.Operand;
            else if (root is Expression.Access a) root = a.Base;
            else if (root is Expression.Member m) root = m.Base;
            else break;
        }
        if (root is Expression.Reference reference && !localRoots.Contains(reference.Name)
            && input.Globals.Any(g => g.Name == reference.Name && g.Space == space))
            return reference.Name;
        return null;
    }
    private Expression Load(Expression.Load load)
    {
        Access(load.MemoryAccess, load.Pointer, load.Span);
        return load with { Pointer = Expr(load.Pointer), MemoryAccess = null };
    }
    private Statement Store(Statement.Store store)
    {
        Access(store.MemoryAccess, store.Target, store.Span);
        return store with { Target = Expr(store.Target), Value = Expr(store.Value), MemoryAccess = null };
    }
    private static Statement.Barrier Barrier(Statement.Barrier barrier)
    {
        if (barrier.NativeMemory is not { } memory) return barrier;
        uint classes = memory.Semantics & (64 | 128 | 256 | 512 | 1024 | 2048 | 4096);
        if ((memory.Semantics & (16 | 8192 | 16384)) != 0 || (classes & (128 | 512 | 1024 | 4096)) != 0)
            throw Error("Native barrier memory semantics have no equivalent WGSL synchronization builtin.", barrier.Span);
        if (memory.ExecutionScope == 3)
        {
            if ((classes & ~256u) != 0 || classes != 0 && memory.Scope is not (3 or 4))
                throw Error("Native subgroup barrier memory scope/classes have no equivalent WGSL subgroupBarrier.", barrier.Span);
            return new(false, false, Subgroup: true) { Span = barrier.Span };
        }
        // A control barrier for an entire workgroup can strengthen subgroup or
        // invocation memory ordering. A device/queue-family fence for storage
        // or images cannot be narrowed to WGSL's workgroup memory scope.
        if (memory.Scope == 0 || memory.Scope is 1 or 5 && (classes & ~256u) != 0)
            throw Error("Native barrier memory scope cannot be narrowed to the WGSL workgroup scope.", barrier.Span);
        return new((classes & 64) != 0, classes == 0 || (classes & 256) != 0, (classes & 2048) != 0) { Span = barrier.Span };
    }
    private Statement.Barrier Fence(Statement.MemoryBarrier fence)
    {
        if (!singleInvocation)
            throw Error("Native memory-only barrier requires uniformity-proven WGSL lowering; an execution barrier cannot be introduced implicitly.", fence.Span);
        // A workgroup with one invocation cannot wait for another invocation,
        // including when its only invocation calls the fence conditionally.
        if (fence.NativeMemory is { } memory)
            return Barrier(new(false, false) { NativeMemory = memory with { ExecutionScope = 2 }, Span = fence.Span });
        return new(fence.Storage, fence.Workgroup || fence.Subgroup, fence.Texture) { Span = fence.Span };
    }
    private Block Body(Block block)
    {
        var copy = new Block();
        copy.DiagnosticFilters.AddRange(block.DiagnosticFilters);
        foreach (var s in block.Statements) copy.Statements.Add(s switch
        {
            Statement.Nested n => n with { Body = Body(n.Body) }, Statement.Declare d => d with { Initializer = d.Initializer is null ? null : Expr(d.Initializer) },
            Statement.Store st => Store(st), Statement.Evaluate ev => ev with { Value = Expr(ev.Value) },
            Statement.If i => i with { Condition = Expr(i.Condition), Accept = Body(i.Accept), Reject = Body(i.Reject) },
            Statement.Loop l => l with { Body = Body(l.Body), Continuing = Body(l.Continuing), BreakIf = l.BreakIf is null ? null : Expr(l.BreakIf) },
            Statement.Switch sw => sw with { Selector = Expr(sw.Selector), Cases = sw.Cases.Select(c => c with { Body = Body(c.Body) }).ToArray() },
            Statement.Return r => r with { Value = r.Value is null ? null : Expr(r.Value) },
            Statement.Barrier b => Barrier(b),
            Statement.MemoryBarrier m => Fence(m),
            _ => s
        });
        return copy;
    }
}
