using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Proc;

internal static partial class UniformityAnalysis
{
    internal static IReadOnlySet<string> UniformPrivateGlobals(Module module, IReadOnlyDictionary<string, ControlFlowFunction> graphs)
    {
        bool ConstantInitializer(Expression? expression)
        {
            if (expression is null) return true;
            try { return ConstantEvaluator.TryEvaluate(expression, out _); }
            catch (ShaderException) { return false; }
        }
        var eligible = module.Globals.Where(g => g.Space == AddressSpace.Private && g.Type is ShaderType.Scalar or ShaderType.Vector
            && ConstantInitializer(g.Initializer)).Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
        if (graphs.Count != module.Functions.Count) return new HashSet<string>(StringComparer.Ordinal);
        foreach (var graph in graphs.Values) {
            var origins = PointerAliasAnalysis.Origins(module, graph);
            var definitions = graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null).ToDictionary(i => i.Result!.Value.Id, i => i.Operation);
            bool UniformArgument(SsaValue value)
            {
                var seen = new HashSet<int>();
                while (seen.Add(value.Id) && definitions.GetValueOrDefault(value.Id) is ValueOperation.Let alias) value = alias.Value;
                if (definitions.GetValueOrDefault(value.Id) is not ValueOperation.Symbol symbol) return false;
                var argument = graph.Signature.Arguments.FirstOrDefault(a => a.Name == symbol.Name);
                return argument is not null && UniformInput(argument.Type, argument.Binding, graph.Signature.Stage);
            }
            foreach (var block in graph.Blocks) {
                foreach (var instruction in block.Instructions) foreach (var operand in instruction.Operation.Operands) {
                    foreach (var root in origins.GetValueOrDefault(operand.Id) ?? []) {
                        if (root.Global is not { } name || !eligible.Contains(name)) continue;
                        bool allowed = instruction.Operation switch {
                            ValueOperation.Load load => load.Pointer == operand,
                            ValueOperation.Access access => access.Base == operand,
                            ValueOperation.Member member => member.Base == operand,
                            ValueOperation.Let alias => alias.Value == operand,
                            ValueOperation.Store store => store.Pointer == operand && block.Id == graph.Entry && graph.Signature.Stage is not null
                                && definitions.GetValueOrDefault(operand.Id) is ValueOperation.Symbol symbol && symbol.Name == name
                                && UniformArgument(store.Value),
                            _ => false
                        };
                        if (!allowed) eligible.Remove(name);
                    }
                }
                foreach (var operand in block.Terminator!.Operands.Concat(block.Terminator.Edges.SelectMany(e => e.Arguments)))
                    foreach (var root in origins.GetValueOrDefault(operand.Id) ?? [])
                        if (root.Global is { } name) eligible.Remove(name);
            }
        }
        return eligible;
    }
}
