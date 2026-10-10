using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;

namespace Sia.Spirv.Compiler.Translation.Front;

/// <summary>Temporary frontend adapter for concrete data and ordered memory operations.</summary>
internal sealed class StructuredControlFlowReader
{
    private readonly ControlFlowFunction function;
    private ControlFlowBlock current;
    private readonly Stack<Dictionary<string, SsaValue>> scopes = [];
    private readonly Stack<int> breaks = [];
    private readonly Stack<int> continues = [];
    private readonly Stack<IReadOnlyList<DiagnosticFilter>> diagnosticScopes = [];
    private readonly HashSet<string> callees;
    private readonly Dictionary<string, ShaderType> symbols;
    private readonly IReadOnlyDictionary<string, ShaderEffects> calleeEffects;
    private bool nativeMemory;
    private sealed class Unsupported(string feature) : Exception(feature);
    private StructuredControlFlowReader(ShaderFunction signature, Module module,
        IReadOnlyDictionary<string, ShaderEffects>? effects = null, bool native = false)
    {
        function = new(signature); current = function.Block(); function.Entry = current.Id;
        calleeEffects = effects ?? ShaderEffectAnalysis.Compute(module); nativeMemory = native;
        callees = module.Functions.Where(f => (f.ReturnType is ShaderType.Void or ShaderType.Pointer || CanonicalTypes.Data(f.ReturnType))
            && f.Arguments.All(a => CanonicalTypes.Data(a.Type)
                || a.Type is ShaderType.Pointer or ShaderType.Image or ShaderType.Sampler or ShaderType.AccelerationStructure))
            .Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        symbols = module.Globals.ToDictionary(g => g.Name, g => g.Space == AddressSpace.Handle ? g.Type
            : (ShaderType)new ShaderType.Pointer(g.Type, g.Space, g.Access), StringComparer.Ordinal);
        foreach (var constant in module.Constants) symbols.Add(constant.Name, constant.Type);
        foreach (var argument in signature.Arguments) symbols[argument.Name] = argument.Type;
    }

    public static bool TryRead(ShaderFunction input, Module module, out ControlFlowFunction? output, out string? deferredFeature,
        IReadOnlyDictionary<string, ShaderEffects>? calleeEffects = null, bool native = false)
    {
        output = null; deferredFeature = null;
        if (!(input.ReturnType is ShaderType.Void or ShaderType.Pointer || CanonicalTypes.Data(input.ReturnType))) {
            deferredFeature = "unsupported return data"; return false;
        }
        try {
            var reader = new StructuredControlFlowReader(input, module, calleeEffects, native);
            reader.Body(input.Body);
            // Valid non-void bodies may leave an unreachable merge after returning in both arms.
            // Reachability removes it before verification; live fallthrough is rejected by ModuleValidator.
            reader.current.Terminator ??= input.ReturnType is ShaderType.Void ? new ControlFlowTerminator.Return()
                : new ControlFlowTerminator.Unreachable();
            output = reader.function; return true;
        }
        catch (Unsupported exception) { deferredFeature = exception.Message; return false; }
    }

    // The native frontend owns block topology and phi parameters. Reuse only
    // expression/statement translation; do not rediscover its graph from regions.
    internal static StructuredControlFlowReader Native(ShaderFunction signature, Module module)
    {
        var reader = new StructuredControlFlowReader(signature, module);
        reader.nativeMemory = true;
        reader.scopes.Push(new(StringComparer.Ordinal));
        reader.diagnosticScopes.Push(signature.DiagnosticFilters);
        return reader;
    }
    internal ControlFlowFunction NativeGraph => function;
    internal ControlFlowBlock NativeCurrent => current;
    internal void NativeBegin(ControlFlowBlock block) => current = block;
    internal void NativeBind(string name, SsaValue value) => scopes.Peek().Add(name, value);
    internal void NativeBody(Block body)
    {
        try { Body(body, scope: false); }
        catch (Unsupported exception) { throw new ShaderException(DiagnosticStage.SpirvParse, "Native CFG requires further translation: " + exception.Message); }
    }
    internal SsaValue NativeValue(Expression expression) => NativeExpression(expression, place: false);
    internal SsaValue NativeAddress(Expression expression) => NativeExpression(expression, place: true);
    private SsaValue NativeExpression(Expression expression, bool place)
    {
        try { return place ? Place(expression) : Expr(expression); }
        catch (Unsupported exception) { throw new ShaderException(DiagnosticStage.SpirvParse, "Native CFG requires further translation: " + exception.Message, expression.Span); }
    }

    private SsaValue Emit(ShaderType type, ValueOperation operation, SourceSpan span = default)
    {
        var value = function.Value(type);
        Add(new(value, operation, span)); return value;
    }
    private void Add(ControlFlowInstruction instruction) => current.Instructions.Add(instruction with {
        DiagnosticFilters = diagnosticScopes.SelectMany(s => s).DistinctBy(f => (f.Namespace, f.Rule)).ToArray()
    });
    private SsaValue Expr(Expression expression)
    {
        switch (expression) {
            case Expression.HelperInvocation query:
                return Emit(ShaderType.Bool, new ValueOperation.HelperInvocation(), query.Span);
            case Expression.Reference reference:
                return Reference(reference, place: false);
            case Expression.Literal literal when CanonicalTypes.Data(literal.Type):
                return Emit(literal.Type, new ValueOperation.Literal(literal.Value), literal.Span);
            case Expression.Construct construct when CanonicalTypes.Data(construct.Type):
                return Emit(construct.Type, new ValueOperation.Construct(construct.Components.Select(Expr).ToArray()), construct.Span);
            case Expression.Load load when CanonicalTypes.Data(load.Type) || nativeMemory && load.Type is ShaderType.Pointer:
                return Emit(load.Type, new ValueOperation.Load(Place(load.Pointer), load.MemoryAccess), load.Span);
            case Expression.Unary unary when CanonicalTypes.Data(unary.Type) && unary.Operator is "!" or "~" or "-":
                return Emit(unary.Type, new ValueOperation.Unary(unary.Operator, Expr(unary.Operand)), unary.Span);
            case Expression.Unary { Operator: "&", Type: ShaderType.Pointer } address:
                return Place(address.Operand);
            case Expression.Binary binary when CanonicalTypes.Data(binary.Type) && CanonicalTypes.Data(binary.Left.Type) && CanonicalTypes.Data(binary.Right.Type):
                if (binary.Operator is "&&" or "||") return ShortCircuit(binary);
                var left = Expr(binary.Left); var right = Expr(binary.Right);
                return Emit(binary.Type, new ValueOperation.Binary(binary.Operator, left, right), binary.Span);
            case Expression.Convert convert when CanonicalTypes.Data(convert.Type) && CanonicalTypes.Data(convert.Operand.Type):
                return Emit(convert.Type, new ValueOperation.Convert(Expr(convert.Operand), convert.Bitcast), convert.Span);
            case Expression.Access access:
                if (IsPlace(access)) return AddressOrRead(access);
                var root = Expr(access.Base); var index = Expr(access.Index);
                return Emit(access.Type, new ValueOperation.Access(root, index), access.Span);
            case Expression.Member member:
                if (IsPlace(member)) return AddressOrRead(member);
                return Emit(member.Type, new ValueOperation.Member(Expr(member.Base), member.Name), member.Span);
            case Expression.Swizzle swizzle when CanonicalTypes.Data(swizzle.Type):
                return Emit(swizzle.Type, new ValueOperation.Swizzle(Expr(swizzle.Vector), swizzle.Components), swizzle.Span);
            case Expression.Call call when call.Binding != CallBinding.Builtin && (CanonicalTypes.Data(call.Type) || call.Type is ShaderType.Pointer) && callees.Contains(call.Function):
                return Emit(call.Type, Call(call), call.Span);
            case Expression.Call call when call.Binding != CallBinding.Function && ShaderBuiltinEffects.IsKnown(call.Function) && call.Type is not ShaderType.Void:
                return Emit(call.Type, new ValueOperation.Builtin(call.Function, call.Arguments.Select(Expr).ToArray(), call.Type, call.AtomicMemory, call.MemoryAccess), call.Span);
            case Expression.Select select when CanonicalTypes.Data(select.Type):
                return Emit(select.Type, new ValueOperation.Select(Expr(select.Condition), Expr(select.Accept), Expr(select.Reject)), select.Span);
            case Expression.Select select when select.Type is ShaderType.Pointer:
                // A native select consumes already evaluated addresses. Capture both
                // before branching; the CFG merge carries their actual address identity.
                var predicate = Expr(select.Condition); var selected = Place(select.Accept); var other = Place(select.Reject);
                var yes = function.Block(); var no = function.Block(); var join = function.Block();
                var selectedAddress = function.Value(selected.Type); join.Parameters.Add(selectedAddress);
                function.SelectionMerges.Add(current.Id, join.Id);
                current.Terminator = new ControlFlowTerminator.Conditional(predicate, new(yes.Id), new(no.Id));
                yes.Terminator = new ControlFlowTerminator.Branch(new(join.Id, [selected]));
                no.Terminator = new ControlFlowTerminator.Branch(new(join.Id, [other]));
                current = join; return selectedAddress;
            case Expression.Call call:
                throw new Unsupported("Call " + call.Function);
            default: throw new Unsupported(expression.GetType().Name + ": " + expression.Type);
        }
    }

    private ValueOperation.Call Call(Expression.Call call) => new(call.Function, call.Arguments.Select(Expr).ToArray(),
        call.Type, call.AtomicMemory, call.MemoryAccess, calleeEffects[call.Function]);

    private SsaValue Place(Expression expression)
    {
        if (expression is Expression.Reference reference) return Reference(reference, place: true);
        if (expression is Expression.Unary { Operator: "*" } dereference) return Expr(dereference.Operand);
        if (expression is Expression.Access access) {
            var root = Place(access.Base);
            if (root.Type is not ShaderType.Pointer pointer) throw new Unsupported("non-addressable index target");
            var element = access.Type is ShaderType.Pointer p ? p.Base : access.Type;
            return Emit(new ShaderType.Pointer(element, pointer.Space, pointer.Access), new ValueOperation.Access(root, Expr(access.Index)), access.Span);
        }
        if (expression is Expression.Member member) {
            var root = Place(member.Base);
            if (root.Type is not ShaderType.Pointer pointer) throw new Unsupported("non-addressable member target");
            var field = member.Type is ShaderType.Pointer p ? p.Base : member.Type;
            return Emit(new ShaderType.Pointer(field, pointer.Space, pointer.Access), new ValueOperation.Member(root, member.Name), member.Span);
        }
        return Expr(expression);
    }

    private bool IsPlace(Expression expression) => expression switch {
        Expression.Reference reference => scopes.SelectMany(s => s).FirstOrDefault(p => p.Key == reference.Name).Value.Type is ShaderType.Pointer
            || !scopes.Any(s => s.ContainsKey(reference.Name)) && symbols.GetValueOrDefault(reference.Name) is ShaderType.Pointer,
        Expression.Access access => IsPlace(access.Base), Expression.Member member => IsPlace(member.Base),
        Expression.Unary { Operator: "*", Operand.Type: ShaderType.Pointer } => true, _ => false
    };

    private SsaValue AddressOrRead(Expression expression)
    {
        var address = Place(expression);
        return expression.Type is ShaderType.Pointer ? address : Emit(expression.Type, new ValueOperation.Load(address), expression.Span);
    }

    private SsaValue Reference(Expression.Reference reference, bool place)
    {
        SsaValue? binding = null;
        foreach (var scope in scopes) if (scope.TryGetValue(reference.Name, out var local)) { binding = local; break; }
        var value = binding ?? Emit(symbols.GetValueOrDefault(reference.Name, reference.Type), new ValueOperation.Symbol(reference.Name), reference.Span);
        // Native-reader scalar references may denote implicit reads or assignment places.
        // Resolve ownership/type first, then make reads explicit without loading store targets.
        if (!place && value.Type is ShaderType.Pointer pointer && pointer.Base == reference.Type)
            return Emit(reference.Type, new ValueOperation.Load(value), reference.Span);
        return value;
    }

    private SsaValue ShortCircuit(Expression.Binary binary)
    {
        var left = Expr(binary.Left);
        var right = function.Block(); var skipped = function.Block(); var join = function.Block();
        var result = function.Value(ShaderType.Bool); join.Parameters.Add(result);
        function.SelectionMerges.Add(current.Id, join.Id);
        current.Terminator = binary.Operator == "&&"
            ? new ControlFlowTerminator.Conditional(left, new(right.Id), new(skipped.Id))
            : new ControlFlowTerminator.Conditional(left, new(skipped.Id), new(right.Id));
        current = right; var value = Expr(binary.Right);
        current.Terminator = new ControlFlowTerminator.Branch(new(join.Id, [value]));
        current = skipped; value = Emit(ShaderType.Bool, new ValueOperation.Literal(binary.Operator == "||"));
        current.Terminator = new ControlFlowTerminator.Branch(new(join.Id, [value]));
        current = join; return result;
    }

    private void Body(Block input, bool scope = true)
    {
        diagnosticScopes.Push(input.DiagnosticFilters);
        if (scope) scopes.Push(new(StringComparer.Ordinal));
        foreach (var statement in input.Statements) {
            if (current.Terminator is not null) break;
            switch (statement) {
                case Statement.Declare { Mutable: true, Type: ShaderType.RayQuery, Initializer: null } query:
                    // Opaque query identity is an allocation, not a zero-valued
                    // data object or a promotable load/store slot.
                    var handle = Emit(new ShaderType.Pointer(query.Type, AddressSpace.Function),
                        new ValueOperation.Local(query.Name, false), query.Span);
                    scopes.Peek().Add(query.Name, handle); break;
                case Statement.Declare declare when CanonicalTypes.Data(declare.Type) || (!declare.Mutable || nativeMemory) && declare.Type is ShaderType.Pointer:
                    if (declare.Mutable) {
                        var initializer = declare.Initializer ?? (declare.Initialize ? new Expression.Construct(declare.Type, []) { Span = declare.Span } : null);
                        SsaValue? value = initializer is null ? null : Expr(initializer);
                        var local = Emit(new ShaderType.Pointer(declare.Type, AddressSpace.Function), new ValueOperation.Local(declare.Name, declare.Initialize), declare.Span);
                        scopes.Peek().Add(declare.Name, local);
                        if (value is { } initial) Add(new(null, new ValueOperation.Store(local, initial), declare.Span));
                    }
                    else {
                        var initial = Expr(declare.Initializer ?? throw new Unsupported("uninitialized immutable local"));
                        scopes.Peek().Add(declare.Name, Emit(declare.Type, new ValueOperation.Let(declare.Name, initial), declare.Span));
                    }
                    break;
                case Statement.Store store when CanonicalTypes.Data(store.Value.Type) || nativeMemory && store.Value.Type is ShaderType.Pointer:
                    var target = Place(store.Target); var stored = Expr(store.Value);
                    Add(new(null, new ValueOperation.Store(target, stored, store.MemoryAccess), store.Span)); break;
                case Statement.Evaluate { Value: Expression.Call { Type: ShaderType.Void } call } when call.Binding != CallBinding.Builtin && callees.Contains(call.Function):
                    Add(new(null, Call(call), call.Span)); break;
                case Statement.Evaluate { Value: Expression.Call { Type: ShaderType.Void } builtin } when builtin.Binding != CallBinding.Function && ShaderBuiltinEffects.IsKnown(builtin.Function):
                    var arguments = builtin.Arguments.Select(Expr).ToArray();
                    Add(new(null, new ValueOperation.Builtin(builtin.Function, arguments, builtin.Type,
                        builtin.AtomicMemory, builtin.MemoryAccess), builtin.Span)); break;
                case Statement.Barrier barrier:
                    Add(new(null, new ValueOperation.Barrier(true, barrier.Storage, barrier.Workgroup, barrier.Texture, barrier.Subgroup,
                        barrier.NativeMemory), barrier.Span)); break;
                case Statement.MemoryBarrier barrier:
                    Add(new(null, new ValueOperation.Barrier(false, barrier.Storage, barrier.Workgroup, barrier.Texture, barrier.Subgroup,
                        barrier.NativeMemory), barrier.Span)); break;
                case Statement.Evaluate evaluate: _ = Expr(evaluate.Value); break;
                case Statement.MeshStore store:
                    Add(new(null, new ValueOperation.MeshStore(store.Field, Expr(store.Index), Expr(store.Value)), store.Span)); break;
                case Statement.MeshSetOutputs counts:
                    Add(new(null, new ValueOperation.MeshSetOutputs(Expr(counts.Vertices), Expr(counts.Primitives)), counts.Span)); break;
                case Statement.TaskDispatch dispatch:
                    current.Terminator = new ControlFlowTerminator.TaskDispatch(Expr(dispatch.Dimensions), dispatch.Payload, dispatch.Span); break;
                case Statement.Nested nested: Body(nested.Body); break;
                case Statement.If branch:
                    var condition = Expr(branch.Condition);
                    var accept = function.Block(); var reject = function.Block(); var merge = function.Block();
                    function.SelectionMerges.Add(current.Id, merge.Id);
                    current.Terminator = new ControlFlowTerminator.Conditional(condition, new(accept.Id), new(reject.Id));
                    current = accept; Body(branch.Accept); Jump(merge.Id);
                    current = reject; Body(branch.Reject); Jump(merge.Id);
                    current = merge; break;
                case Statement.Loop loop:
                    diagnosticScopes.Push(loop.Body.DiagnosticFilters);
                    var header = function.Block(); var continuing = function.Block(); var exit = function.Block();
                    function.Loops.Add(header.Id, new(continuing.Id, exit.Id));
                    Jump(header.Id); breaks.Push(exit.Id); continues.Push(continuing.Id);
                    scopes.Push(new(StringComparer.Ordinal));
                    current = header; Body(loop.Body, scope: false); Jump(continuing.Id);
                    scopes.Push(new(StringComparer.Ordinal));
                    current = continuing; Body(loop.Continuing, scope: false);
                    diagnosticScopes.Push(loop.Continuing.DiagnosticFilters);
                    if (current.Terminator is null) {
                        if (loop.BreakIf is null) current.Terminator = new ControlFlowTerminator.Branch(new(header.Id));
                        else {
                            var breakIf = Expr(loop.BreakIf);
                            current.Terminator = new ControlFlowTerminator.Conditional(breakIf, new(exit.Id), new(header.Id));
                        }
                    }
                    diagnosticScopes.Pop(); scopes.Pop(); scopes.Pop();
                    diagnosticScopes.Pop(); continues.Pop(); breaks.Pop(); current = exit; break;
                case Statement.Switch selection:
                    var selector = Expr(selection.Selector); var done = function.Block();
                    function.SelectionMerges.Add(current.Id, done.Id);
                    var arms = selection.Cases.Select(c => (Case: c, Block: function.Block())).ToArray();
                    var fallback = arms.SingleOrDefault(c => c.Case.IsDefault).Block;
                    if (fallback is null) { fallback = function.Block(); fallback.Terminator = new ControlFlowTerminator.Branch(new(done.Id)); }
                    current.Terminator = new ControlFlowTerminator.Switch(selector,
                        arms.Where(c => c.Case.Values.Count != 0).Select(c => new ControlFlowCase(c.Case.Values, new(c.Block.Id))).ToArray(), new(fallback.Id));
                    breaks.Push(done.Id);
                    foreach (var arm in arms) { current = arm.Block; Body(arm.Case.Body); Jump(done.Id); }
                    breaks.Pop(); current = done; break;
                case Statement.Break: current.Terminator = new ControlFlowTerminator.Branch(new(breaks.Peek())); break;
                case Statement.Continue: current.Terminator = new ControlFlowTerminator.Branch(new(continues.Peek())); break;
                case Statement.Return { Value: null } returned: current.Terminator = new ControlFlowTerminator.Return { Span = returned.Span }; break;
                case Statement.Unreachable unreachable: current.Terminator = new ControlFlowTerminator.Unreachable(unreachable.Span); break;
                case Statement.InvocationKill kill: current.Terminator = new ControlFlowTerminator.InvocationKill(kill.Span, kill.ExplicitTermination); break;
                case Statement.Kill kill: Add(new(null, new ValueOperation.Demote(), kill.Span)); break;
                case Statement.Return returned when returned.Value is { } value && (CanonicalTypes.Data(value.Type) || value.Type is ShaderType.Pointer):
                    var returnValue = Expr(value); current.Terminator = new ControlFlowTerminator.Return(returnValue) { Span = returned.Span }; break;
                default: throw new Unsupported(statement.GetType().Name);
            }
        }
        if (scope) scopes.Pop();
        diagnosticScopes.Pop();
    }
    private void Jump(int target) => current.Terminator ??= new ControlFlowTerminator.Branch(new(target));
}
