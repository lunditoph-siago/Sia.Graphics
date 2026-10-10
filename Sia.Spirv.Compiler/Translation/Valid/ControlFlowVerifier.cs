using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;

namespace Sia.Spirv.Compiler.Translation.Valid;

internal static class ControlFlowVerifier
{
    public static void Validate(CanonicalModule module)
    {
        void Require(bool condition, string message) {
            if (!condition) throw new ShaderException(DiagnosticStage.Validation, "Canonical module: " + message);
        }
        var declarations = new Dictionary<string, ShaderFunction>(StringComparer.Ordinal);
        foreach (var declaration in module.Declarations.Functions) {
            Require(declarations.TryAdd(declaration.Name, declaration), "duplicate function " + declaration.Name);
            Require(module.Functions.ContainsKey(declaration.Name) != module.DeferredFunctions.ContainsKey(declaration.Name),
                "function must have exactly one graph or explicit deferral: " + declaration.Name);
        }
        foreach (var pair in module.DeferredFunctions) {
            Require(declarations.ContainsKey(pair.Key), "unknown deferred function " + pair.Key);
            Require(!string.IsNullOrWhiteSpace(pair.Value), "missing deferral reason for " + pair.Key);
        }
        Require(module.EntryFunctions.All(declarations.ContainsKey), "unknown entry-closure function");
        foreach (var pair in module.Functions) {
            Require(declarations.TryGetValue(pair.Key, out var declaration), "unknown graph function " + pair.Key);
            var signature = pair.Value.Signature;
            Require(signature.Name == pair.Key && signature.ReturnType == declaration!.ReturnType
                && signature.Arguments.SequenceEqual(declaration.Arguments), "graph signature mismatch for " + pair.Key);
            Require(pair.Value.Blocks.All(b => b.Terminator is not null), "missing terminator in " + pair.Key);
        }
        var effects = ShaderEffectAnalysis.Compute(module);
        foreach (var function in module.Functions.Values) Validate(function, module.Declarations, calleeEffects: effects);
    }

    public static void Validate(ControlFlowFunction function, Module module, ControlFlowAnalysisContext? analyses = null,
        IReadOnlyDictionary<string, ShaderEffects>? calleeEffects = null)
    {
        analyses?.RequireFunction(function);
        analyses ??= new ControlFlowAnalysisContext(function);
        void Require(bool condition, string message, SourceSpan span = default) {
            if (!condition) throw new ShaderException(DiagnosticStage.Validation, "Canonical " + function.Signature.Name + ": " + message, span);
        }
        Require(function.Blocks.Count != 0, "missing entry block");
        Require(function.Blocks.Select(b => b.Id).Distinct().Count() == function.Blocks.Count, "duplicate block");
        var blocks = function.Blocks.ToDictionary(b => b.Id);
        Require(blocks.ContainsKey(function.Entry), "entry does not exist");
        var symbols = module.Globals.ToDictionary(g => g.Name, g => g.Space == AddressSpace.Handle
            ? g.Type : (ShaderType)new ShaderType.Pointer(g.Type, g.Space, g.Access), StringComparer.Ordinal);
        foreach (var constant in module.Constants) symbols.Add(constant.Name, constant.Type);
        foreach (var argument in function.Signature.Arguments) symbols[argument.Name] = argument.Type;
        var definitions = new Dictionary<int, (SsaValue Value, int Block, int Index)>();
        void Define(SsaValue value, int block, int index) {
            Require(value.Id >= 0 && definitions.TryAdd(value.Id, (value, block, index)), "duplicate or invalid value v" + value.Id);
        }
        foreach (var block in function.Blocks) {
            Require(block.Terminator is not null, "b" + block.Id + " has no terminator");
            foreach (var parameter in block.Parameters) Define(parameter, block.Id, -1);
            for (int i = 0; i < block.Instructions.Count; i++) {
                var instruction = block.Instructions[i];
                Require((instruction.Operation is ValueOperation.Store or ValueOperation.InterfaceStore or ValueOperation.MeshStore or ValueOperation.MeshSetOutputs or ValueOperation.Barrier or ValueOperation.Demote or ValueOperation.Call { ReturnType: ShaderType.Void } or ValueOperation.Builtin { ReturnType: ShaderType.Void })
                    == (instruction.Result is null), "instruction result/effect mismatch", instruction.Span);
                if (instruction.Result is { } result) Define(result, block.Id, i);
            }
            foreach (var edge in block.Terminator!.Edges) {
                Require(blocks.TryGetValue(edge.Target, out var target), "edge to missing block b" + edge.Target);
                Require(edge.Arguments.Count == target!.Parameters.Count, "merge arity mismatch at b" + edge.Target);
                for (int i = 0; i < edge.Arguments.Count; i++)
                    Require(edge.Arguments[i].Type == target.Parameters[i].Type, "merge type mismatch at b" + edge.Target);
            }
        }
        Require(ControlFlowAnalysis.Reachable(function).Count == blocks.Count, "unreachable block must be removed before SSA verification");
        var predecessors = analyses.Predecessors;
        Require(!predecessors[function.Entry].Any(), "entry has a predecessor");
        Require(blocks[function.Entry].Parameters.Count == 0, "entry parameters lack incoming values");
        var dominance = analyses.Dominance;
        foreach (var (header, merge) in function.SelectionMerges) {
            Require(blocks.TryGetValue(header, out var selection) && selection.Terminator is ControlFlowTerminator.Conditional or ControlFlowTerminator.Switch,
                "selection header is missing or lacks a selection terminator");
            Require(merge is null || merge != header && blocks.ContainsKey(merge.Value) && dominance[merge.Value].Contains(header), "invalid selection merge");
        }
        foreach (var (header, loop) in function.Loops) {
            Require(blocks.ContainsKey(header), "loop header is missing");
            Require(loop.Merge is null || loop.Merge != header && blocks.ContainsKey(loop.Merge.Value) && dominance[loop.Merge.Value].Contains(header), "invalid loop merge");
            Require(loop.Continuing is null || loop.Continuing != header && loop.Continuing != loop.Merge && blocks.ContainsKey(loop.Continuing.Value)
                && dominance[loop.Continuing.Value].Contains(header), "invalid loop continuing target");
            foreach (var (predecessor, _) in predecessors[header].Where(p => dominance[p.Block.Id].Contains(header)))
                Require(loop.Continuing is int continuing && dominance[predecessor.Id].Contains(continuing), "loop backedge bypasses continuing target");
        }
        void Use(SsaValue value, int block, int index) {
            Require(definitions.TryGetValue(value.Id, out var definition), "undefined value v" + value.Id);
            Require(definition.Value.Type == value.Type, "inconsistent type for v" + value.Id);
            Require(definition.Block == block ? definition.Index < index : dominance[block].Contains(definition.Block),
                "v" + value.Id + " does not dominate use in b" + block);
        }
        var validateInstruction = ModuleValidator.CanonicalInstructions(module, function);
        calleeEffects ??= ShaderEffectAnalysis.Compute(module);
        foreach (var block in function.Blocks) {
            for (int i = 0; i < block.Instructions.Count; i++) {
                var instruction = block.Instructions[i];
                foreach (var operand in instruction.Operation.Operands) Use(operand, block.Id, i);
                ShaderType? result = instruction.Result?.Type;
                bool valid = instruction.Operation switch {
                    ValueOperation.Symbol symbol => symbols.TryGetValue(symbol.Name, out var type) && type == result,
                    ValueOperation.Local local => result is ShaderType.Pointer { Space: AddressSpace.Function } p
                        && (CanonicalTypes.Data(p.Base) || !local.ZeroInitialize && p.Base is ShaderType.Pointer),
                    ValueOperation.Let let => result == let.Value.Type,
                    ValueOperation.Builtin builtin => ShaderBuiltinEffects.IsKnown(builtin.Function)
                        && (builtin.ReturnType is ShaderType.Void ? result is null : result == builtin.ReturnType),
                    ValueOperation.Barrier or ValueOperation.Demote or ValueOperation.MeshStore or ValueOperation.MeshSetOutputs => result is null,
                    ValueOperation.HelperInvocation => result == ShaderType.Bool,
                    ValueOperation.InterfaceLoad load => load.Field.Input && result == load.Field.Type && CanonicalTypes.Data(load.Field.Type),
                    ValueOperation.InterfaceStore store => !store.Field.Input && store.Value.Type == store.Field.Type && CanonicalTypes.Data(store.Field.Type),
                    ValueOperation.Load load => load.Pointer.Type is ShaderType.Pointer pointer && pointer.Base == result
                        && (pointer.Access & StorageAccess.Read) != 0 && pointer.Space != AddressSpace.Handle,
                    ValueOperation.Call call => module.Functions.SingleOrDefault(f => f.Name == call.Function) is { } callee
                        && callee.Stage is null && callee.ReturnType == call.ReturnType
                        && (instruction.Effects & calleeEffects[call.Function]) == calleeEffects[call.Function]
                        && (call.ReturnType is ShaderType.Void ? result is null : result == call.ReturnType)
                        && callee.Arguments.Select(a => a.Type).SequenceEqual(call.Arguments.Select(a => a.Type)),
                    ValueOperation.Store store => store.Pointer.Type is ShaderType.Pointer pointer && pointer.Base == store.Value.Type
                        && (pointer.Access & StorageAccess.Write) != 0 && pointer.Space is not (AddressSpace.Uniform or AddressSpace.Immediate or AddressSpace.Handle),
                    ValueOperation.Literal or ValueOperation.Unary or ValueOperation.Binary or ValueOperation.Convert
                        or ValueOperation.Construct or ValueOperation.Select or ValueOperation.Access or ValueOperation.Member or ValueOperation.Swizzle => true,
                    _ => false
                };
                Require(valid, "illegal " + instruction.Operation.GetType().Name + " in b" + block.Id, instruction.Span);
                try { validateInstruction(instruction); }
                catch (ShaderException exception) { throw new ShaderException(DiagnosticStage.Validation,
                    "Canonical " + function.Signature.Name + ": illegal " + instruction.Operation.GetType().Name + ": " + exception.Message, instruction.Span); }
            }
            var terminator = block.Terminator!;
            foreach (var operand in terminator.Operands.Concat(terminator.Edges.SelectMany(e => e.Arguments))) Use(operand, block.Id, int.MaxValue);
            switch (terminator) {
                case ControlFlowTerminator.Conditional conditional: Require(conditional.Condition.Type == ShaderType.Bool, "condition is not bool"); break;
                case ControlFlowTerminator.Switch selection:
                    Require(selection.Selector.Type == ShaderType.I32 || selection.Selector.Type == ShaderType.U32, "switch selector is not integer");
                    Require(selection.Cases.SelectMany(c => c.Values).All(v => v.Type == selection.Selector.Type), "switch case type mismatch");
                    Require(selection.Cases.SelectMany(c => c.Values).Select(v => v.Value).Distinct().Count() == selection.Cases.Sum(c => c.Values.Count), "duplicate switch case"); break;
                case ControlFlowTerminator.Return returned:
                    Require(returned.Value?.Type == function.Signature.ReturnType || returned.Value is null && function.Signature.ReturnType is ShaderType.Void, "return type mismatch"); break;
                case ControlFlowTerminator.InvocationKill kill:
                    Require(function.Signature.Stage is null or ShaderStage.Fragment, "invocation kill requires a fragment entry", kill.Span); break;
                case ControlFlowTerminator.TaskDispatch dispatch:
                    Require(function.Signature.Stage is null or ShaderStage.Task, "task dispatch requires a task entry", dispatch.Span);
                    Require(dispatch.Dimensions.Type == new ShaderType.Vector(3, ShaderType.U32), "task dispatch dimensions must be vec3u", dispatch.Span);
                    Require(module.Globals.Any(g => g.Name == dispatch.Payload && g.Space == AddressSpace.TaskPayload), "task dispatch requires task payload", dispatch.Span); break;
            }
        }
    }

}
