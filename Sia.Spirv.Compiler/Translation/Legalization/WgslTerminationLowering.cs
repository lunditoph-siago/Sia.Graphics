using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Legalize native non-returning paths for WGSL.
/// Canonical IR and SPIR-V retain the original non-returning terminator.</summary>
internal static class WgslTerminationLowering
{
    public static Module Run(Module input)
    {
        bool Contains(Block body) => body.Statements.Any(s => s switch {
            Statement.Unreachable or Statement.InvocationKill => true, Statement.Nested n => Contains(n.Body),
            Statement.If i => Contains(i.Accept) || Contains(i.Reject),
            Statement.Loop l => Contains(l.Body) || Contains(l.Continuing),
            Statement.Switch sw => sw.Cases.Any(c => Contains(c.Body)), _ => false
        });
        if (!input.Functions.Any(f => Contains(f.Body))) return input;
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
