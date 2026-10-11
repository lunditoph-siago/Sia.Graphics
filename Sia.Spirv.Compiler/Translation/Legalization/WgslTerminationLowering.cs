using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Legalize native non-returning paths for WGSL.
/// Canonical IR and SPIR-V retain the original non-returning terminator.</summary>
internal static class WgslTerminationLowering
{
    public static Module Run(Module input)
        => Run(input, null);

    internal static CanonicalModule Run(CanonicalModule input)
    {
        ModuleValidator.Validate(input, native: true);
        input = InvocationTerminationControlFlow.PrepareWgsl(input);
        var graphs = input.Functions.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var pair in graphs.ToArray()) {
            if (!pair.Value.Blocks.Any(b => b.Terminator is ControlFlowTerminator.Unreachable or ControlFlowTerminator.InvocationKill)) continue;
            var graph = pair.Value.Copy();
            foreach (var block in graph.Blocks) {
                if (block.Terminator is not (ControlFlowTerminator.Unreachable or ControlFlowTerminator.InvocationKill)) continue;
                var span = block.Terminator is ControlFlowTerminator.Unreachable u ? u.Span : ((ControlFlowTerminator.InvocationKill)block.Terminator).Span;
                if (block.Terminator is ControlFlowTerminator.InvocationKill) block.Instructions.Add(new(null, new ValueOperation.Demote(), span));
                SsaValue? returned = null;
                if (graph.Signature.ReturnType is not ShaderType.Void) {
                    if (!CanonicalTypes.Data(graph.Signature.ReturnType))
                        throw new ShaderException(DiagnosticStage.WgslWrite,
                            "Native unreachable needs a constructible WGSL return type after helper legalization.", span);
                    returned = graph.Value(graph.Signature.ReturnType);
                    block.Instructions.Add(new(returned, new ValueOperation.Construct([]), span));
                }
                block.Terminator = new ControlFlowTerminator.Return(returned) { Span = span };
            }
            graphs[pair.Key] = graph;
        }
        var module = Run(input.Declarations, input.DeferredFunctions.Keys.ToHashSet(StringComparer.Ordinal));
        var effects = ShaderEffectAnalysis.Compute(module, graphs);
        foreach (var pair in graphs.ToArray()) {
            if (!pair.Value.Blocks.SelectMany(b => b.Instructions).Any(i => i.Operation is ValueOperation.Call c && (c.CalleeEffects & effects[c.Function]) != effects[c.Function])) continue;
            var graph = pair.Value.Copy();
            foreach (var block in graph.Blocks) for (int i = 0; i < block.Instructions.Count; i++)
                if (block.Instructions[i].Operation is ValueOperation.Call call)
                    block.Instructions[i] = block.Instructions[i] with { Operation = call with { CalleeEffects = call.CalleeEffects | effects[call.Function] } };
            graphs[pair.Key] = graph;
        }
        var output = new CanonicalModule(module, graphs, input.DeferredFunctions, input.EntryFunctions);
        ModuleValidator.Validate(output, native: true); return output;
    }

    private static Module Run(Module input, IReadOnlySet<string>? functions)
    {
        bool Contains(Block body) => body.Statements.Any(s => s switch {
            Statement.Unreachable or Statement.InvocationKill => true, Statement.Nested n => Contains(n.Body),
            Statement.If i => Contains(i.Accept) || Contains(i.Reject),
            Statement.Loop l => Contains(l.Body) || Contains(l.Continuing),
            Statement.Switch sw => sw.Cases.Any(c => Contains(c.Body)), _ => false
        });
        bool Selected(ShaderFunction f) => (functions is null || functions.Contains(f.Name)) && Contains(f.Body);
        if (!input.Functions.Any(Selected)) return input;
        Block Body(Block source, ShaderType result, bool continuing = false)
        {
            var output = new Block(); output.DiagnosticFilters.AddRange(source.DiagnosticFilters);
            foreach (var statement in source.Statements) {
                if (statement is Statement.Unreachable or Statement.InvocationKill) {
                    if (statement is Statement.InvocationKill) {
                        if (continuing) throw new ShaderException(DiagnosticStage.WgslWrite,
                            "Native invocation kill in continuing requires control-flow relocation.", statement.Span);
                        output.Statements.Add(new Statement.Kill { Span = statement.Span });
                    }
                    // WGSL forbids return in continuing. Falling through is an
                    // allowed choice for this source-undefined path; no defined
                    // execution reaches it. Do not create an invalid infinite loop.
                    if (!continuing) {
                        if (result is not ShaderType.Void && !CanonicalTypes.Data(result))
                            throw new ShaderException(DiagnosticStage.WgslWrite,
                                "Native unreachable needs a constructible WGSL return type after helper legalization.", statement.Span);
                        output.Statements.Add(new Statement.Return(result is ShaderType.Void ? null : new Expression.Construct(result, [])) {
                            Span = statement.Span
                        });
                    }
                    break;
                }
                output.Statements.Add(statement switch {
                    Statement.Nested n => n with { Body = Body(n.Body, result, continuing) },
                    Statement.If i => i with { Accept = Body(i.Accept, result, continuing), Reject = Body(i.Reject, result, continuing) },
                    Statement.Switch sw => sw with { Cases = sw.Cases.Select(c => c with { Body = Body(c.Body, result, continuing) }).ToArray() },
                    Statement.Loop l => l with { Body = Body(l.Body, result), Continuing = Body(l.Continuing, result, continuing: true) },
                    _ => statement
                });
            }
            return output;
        }
        var module = new Module { VulkanMemoryModel = input.VulkanMemoryModel, WorkgroupInitializationRequired = input.WorkgroupInitializationRequired };
        module.Structures.AddRange(input.Structures); module.Constants.AddRange(input.Constants); module.Globals.AddRange(input.Globals);
        module.Enables.UnionWith(input.Enables); module.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        foreach (var function in input.Functions) {
            if (!Selected(function)) { module.Functions.Add(function); continue; }
            var copy = new ShaderFunction(function.Name) { Stage = function.Stage, ReturnType = function.ReturnType,
                ReturnBinding = function.ReturnBinding, WorkgroupSize = function.WorkgroupSize.ToArray(),
                TaskPayload = function.TaskPayload, MeshOutput = function.MeshOutput,
                EarlyDepthTest = function.EarlyDepthTest, ConservativeDepth = function.ConservativeDepth,
                Body = Body(function.Body, function.ReturnType) };
            copy.Arguments.AddRange(function.Arguments); copy.DiagnosticFilters.AddRange(function.DiagnosticFilters); module.Functions.Add(copy);
        }
        return module;
    }
}
