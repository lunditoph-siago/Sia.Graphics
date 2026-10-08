using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Valid;

public static partial class ModuleValidator
{
    private sealed partial class Validator
    {
        private bool Body(Block block, bool scope = true, bool continuing = false)
        {
            if (scope) scopes.Push(new(StringComparer.Ordinal));
            bool fallsThrough = true;
            foreach (var statement in block.Statements)
            {
                bool next = true;
                switch (statement)
                {
                    case Statement.Nested nested: next = Body(nested.Body, continuing: continuing); break;
                    case Statement.Declare declaration:
                        Type(declaration.Type);
                        Require(declaration.Type is not ShaderType.Void && !RuntimeSized(declaration.Type) && !ContainsAtomic(declaration.Type), "Invalid local variable type.", statement.Span);
                        // SPIR-V function temporaries may contain specialization-sized
                        // arrays. WGSL source admission rejects these in its frontend;
                        // WGSL writing requires pipeline resolution for mutable values.
                        if (declaration.Mutable) Require(Data(declaration.Type) || declaration.Type is ShaderType.RayQuery, "Mutable local must have a constructible data type or be a ray query.", statement.Span);
                        if (declaration.Type is ShaderType.RayQuery) Require(declaration.Mutable && declaration.Initializer is null, "Ray queries require uninitialized mutable locals.", statement.Span);
                        if (declaration.Initializer is not null) Same(Expr(declaration.Initializer), declaration.Type, "Local initializer type mismatch.", statement.Span);
                        else Require(declaration.Mutable, "Immutable local needs an initializer.", statement.Span);
                        Require(scopes.Peek().TryAdd(declaration.Name, new(declaration.Type, declaration.Mutable, declaration.Mutable)), "Duplicate local name.", statement.Span); break;
                    case Statement.Store store:
                        Expr(store.Target); ShaderType value = Expr(store.Value); var place = Place(store.Target);
                        MemoryAccess(store.MemoryAccess, place.Space, false, store.Span);
                        if (place.Space == AddressSpace.TaskPayload) Restrict(Task);
                        Require(place.Place && place.Writable && !ContainsAtomic(place.Type) && place.Type is not ShaderType.RayQuery, "Store requires writable non-atomic data; ray queries cannot be copied.", statement.Span);
                        Same(value, place.Type, "Store type mismatch.", statement.Span); break;
                    case Statement.Evaluate evaluate: Expr(evaluate.Value); break;
                    case Statement.If branch:
                        Same(Expr(branch.Condition), ShaderType.Bool, "If condition must be boolean.", statement.Span);
                        bool accept = Body(branch.Accept, continuing: continuing), reject = Body(branch.Reject, continuing: continuing);
                        next = accept || reject; break;
                    case Statement.Loop loop:
                        Require(!continuing, "Continuing block cannot contain a loop.", statement.Span);
                        var loopTarget = new BreakTarget(true); breaks.Push(loopTarget); loopDepth++;
                        // Body declarations remain visible in continuing, but not after the loop.
                        scopes.Push(new(StringComparer.Ordinal)); Body(loop.Body, false);
                        scopes.Push(new(StringComparer.Ordinal)); Body(loop.Continuing, false, true);
                        if (loop.BreakIf is not null) Same(Expr(loop.BreakIf), ShaderType.Bool, "break if condition must be boolean.", statement.Span);
                        scopes.Pop(); scopes.Pop(); loopDepth--; breaks.Pop(); next = loopTarget.Used || loop.BreakIf is not null; break;
                    case Statement.Switch selection:
                        ShaderType selector = Expr(selection.Selector);
                        Require(selector is ShaderType.Scalar && Integer(selector), "Switch selector must be an integer scalar.", statement.Span);
                        var target = new BreakTarget(false); breaks.Push(target); var values = new HashSet<object>(); int defaults = 0; bool caseFalls = false;
                        foreach (var arm in selection.Cases)
                        {
                            if (arm.IsDefault) defaults++;
                            Require(arm.IsDefault || arm.Values.Count != 0, "Switch arm has no selectors.", statement.Span);
                            foreach (var item in arm.Values) { Same(Expr(item), selector, "Switch case type mismatch.", item.Span); Require(values.Add(item.Value), "Duplicate switch case.", item.Span); }
                            caseFalls |= Body(arm.Body, continuing: continuing);
                        }
                        breaks.Pop(); Require(defaults == 1, "Switch needs exactly one default case.", statement.Span);
                        next = caseFalls || target.Used; break;
                    case Statement.Return ret:
                        Require(!continuing, "Return is forbidden in continuing.", statement.Span);
                        Same(ret.Value is null ? new ShaderType.Void() : Expr(ret.Value), function!.ReturnType, "Return type mismatch.", statement.Span); next = false; break;
                    case Statement.Break:
                        Require(breaks.Count != 0 && (!continuing || !breaks.Peek().Loop), "Break has no enclosing target or exits continuing.", statement.Span);
                        breaks.Peek().Used = true; next = false; break;
                    case Statement.Continue:
                        Require(loopDepth != 0 && !continuing, "Continue requires a loop body.", statement.Span); next = false; break;
                    case Statement.Kill: Require(!continuing, "Discard is forbidden in continuing.", statement.Span); Restrict(Fragment); next = false; break;
                    case Statement.Barrier barrier:
                        if (barrier.NativeMemory is { } control) BarrierMemory(control, true, statement.Span);
                        else
                        {
                            Require(barrier.Storage || barrier.Workgroup || barrier.Texture || barrier.Subgroup, "Empty barrier.", statement.Span);
                            Require(!barrier.Subgroup || !(barrier.Storage || barrier.Workgroup || barrier.Texture), "Mixed subgroup/workgroup execution barriers require explicit native operands.", statement.Span);
                            Restrict(WorkgroupStages);
                        }
                        break;
                    case Statement.MemoryBarrier barrier:
                        if (barrier.NativeMemory is { } memory) BarrierMemory(memory, false, statement.Span);
                        else
                        {
                            Require(barrier.Storage || barrier.Workgroup || barrier.Texture || barrier.Subgroup, "Empty memory barrier.", statement.Span);
                            Require(!barrier.Subgroup || !(barrier.Storage || barrier.Workgroup || barrier.Texture), "Mixed subgroup/workgroup memory scopes require explicit native operands.", statement.Span);
                            if (!barrier.Subgroup) Restrict(WorkgroupStages);
                        }
                        break;
                    default: throw Error("Unknown statement variant.", statement.Span);
                }
                fallsThrough &= next;
            }
            if (scope) scopes.Pop(); return fallsThrough;
        }
    }
}
