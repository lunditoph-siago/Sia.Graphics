using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Proc;

/// <summary>Per-compilation root identities and bottom-up memory-access summaries.
/// WGSL legality consumes these facts; native SPIR-V aliases remain representable.</summary>
internal static partial class PointerAliasAnalysis
{
    internal sealed record Root(int Parameter = -1, string? Global = null, int Local = 0);
    private sealed record Binding(HashSet<Root> Roots, bool Place);
    private sealed record Summary(HashSet<Root> Reads, HashSet<Root> Writes);

    public static void Validate(Module module, DiagnosticStage stage = DiagnosticStage.Validation,
        ICollection<CanonicalDeferral>? deferrals = null)
        => Validate(module, ReadGraphs(module, deferrals), stage);

    // Static source legality includes statements that canonical reachability
    // deliberately removes. Keep this gate until source origins cover them.
    public static void ValidateSource(Module module, DiagnosticStage stage = DiagnosticStage.Validation)
        => Validate(module, new Dictionary<string, ControlFlowFunction>(StringComparer.Ordinal), stage);

    public static void Validate(Module module, IReadOnlyDictionary<string, ControlFlowFunction> graphs,
        DiagnosticStage stage = DiagnosticStage.Validation)
    {
        graphs = graphs.ToDictionary(p => p.Key, p => CanonicalHelperInliner.Run(p.Value, module), StringComparer.Ordinal);
        foreach (var (name, graph) in graphs) {
            if (name != graph.Signature.Name || !module.Functions.Any(f => f.Name == name))
                throw new ShaderException(stage, "Canonical pointer analysis requires a matching function signature.");
            ControlFlowVerifier.Validate(graph, module);
        }
        var functions = module.Functions.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var summaries = new Dictionary<string, Summary>(StringComparer.Ordinal); var active = new HashSet<string>(StringComparer.Ordinal);
        Summary Function(string name) {
            if (summaries.TryGetValue(name, out var found)) return found;
            if (!active.Add(name)) throw new ShaderException(stage, "Recursive shader calls cannot be analyzed for pointer aliases.");
            var summary = graphs.TryGetValue(name, out var graph)
                ? new CanonicalAnalysis(module, graph, Function, stage).Run()
                : new Analysis(module, functions[name], Function, functions.ContainsKey, stage).Run();
            active.Remove(name); summaries.Add(name, summary); return summary;
        }
        foreach (var function in module.Functions) _ = Function(function.Name);
    }

    private static Dictionary<string, ControlFlowFunction> ReadGraphs(Module module, ICollection<CanonicalDeferral>? deferrals)
    {
        var graphs = new Dictionary<string, ControlFlowFunction>(StringComparer.Ordinal);
        foreach (var function in module.Functions)
            if (StructuredControlFlowReader.TryRead(function, module, out var graph, out var reason)) {
                ControlFlowAnalysis.RemoveUnreachable(graph!); ControlFlowVerifier.Validate(graph!, module);
                graphs.Add(function.Name, graph!);
            }
            else deferrals?.Add(new(function.Name, reason!));
        return graphs;
    }

    private static ShaderEffects PointerEffects(string builtin) => builtin switch {
        "workgroupUniformLoad" or "coopLoad" or "coopLoadT" => ShaderEffects.ReadMemory,
        "coopStore" or "coopStoreT" => ShaderEffects.WriteMemory, _ => ShaderBuiltinEffects.For(builtin)
    };

    private static void ApplyCall(string caller, string called, IReadOnlyList<bool> pointers, HashSet<Root>[] arguments,
        Summary summary, HashSet<Root> reads, HashSet<Root> writes, DiagnosticStage stage, SourceSpan span)
    {
        bool Written(int parameter) => summary.Writes.Contains(new(parameter));
        void Invalid() => throw new ShaderException(stage,
            $"Pointer alias violation in '{caller}': call '{called}' accesses overlapping root identifiers with a write.", span);
        for (int i = 0; i < arguments.Length; i++) {
            if (!pointers[i]) continue;
            if (arguments[i].Count == 0) throw new ShaderException(stage, "Pointer argument root identity is not representable for WGSL alias analysis.", span);
            for (int j = 0; j < i; j++)
                if (arguments[i].Overlaps(arguments[j]) && (Written(i) || Written(j))) Invalid();
            foreach (var root in arguments[i].Where(r => r.Global is not null))
                if (Written(i) && (summary.Reads.Contains(root) || summary.Writes.Contains(root))
                    || summary.Reads.Contains(new(i)) && summary.Writes.Contains(root)) Invalid();
        }
        IEnumerable<Root> Bind(Root root) => root.Parameter < 0 ? [root] : arguments[root.Parameter];
        reads.UnionWith(summary.Reads.SelectMany(Bind)); writes.UnionWith(summary.Writes.SelectMany(Bind));
    }

    private sealed class Analysis(Module module, ShaderFunction function, Func<string, Summary> callee,
        Func<string, bool> userFunction, DiagnosticStage stage)
    {
        private readonly Dictionary<string, Binding> names = new(StringComparer.Ordinal);
        private readonly HashSet<string> globals = module.Globals.Where(g => g.Space != AddressSpace.Handle).Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
        private readonly HashSet<Root> reads = []; private readonly HashSet<Root> writes = [];
        private int local;
        public Summary Run()
        {
            for (int i = 0; i < function.Arguments.Count; i++) {
                var argument = function.Arguments[i];
                names.Add(argument.Name, new(argument.Type is ShaderType.Pointer ? [new(i)] : [], false));
            }
            Body(function.Body);
            return new(reads.Where(r => r.Local == 0).ToHashSet(), writes.Where(r => r.Local == 0).ToHashSet());
        }
        private HashSet<Root> Origins(Expression expression) => expression switch {
            Expression.Reference r => names.TryGetValue(r.Name, out var binding) ? binding.Roots : globals.Contains(r.Name) ? [new(Global: r.Name)] : [],
            Expression.Unary { Operator: "&" or "*" } u => Origins(u.Operand),
            Expression.Load l when l.Type is ShaderType.Pointer => Origins(l.Pointer),
            Expression.Access a => Origins(a.Base), Expression.Member m => Origins(m.Base), Expression.Swizzle s => Origins(s.Vector),
            Expression.Select s when s.Type is ShaderType.Pointer => Origins(s.Accept).Union(Origins(s.Reject)).ToHashSet(), _ => []
        };
        private bool Place(Expression expression) => expression switch {
            Expression.Reference r => names.TryGetValue(r.Name, out var binding) ? binding.Place : globals.Contains(r.Name),
            Expression.Unary { Operator: "*" } => true,
            Expression.Access a => a.Base.Type is ShaderType.Pointer || Place(a.Base),
            Expression.Member m => m.Base.Type is ShaderType.Pointer || Place(m.Base),
            Expression.Swizzle s => s.Vector.Type is ShaderType.Pointer || Place(s.Vector), _ => false
        };
        private void Address(Expression expression)
        {
            switch (expression) {
                case Expression.Unary { Operator: "&" or "*" } u: Address(u.Operand); break;
                case Expression.Access a: Address(a.Base); Value(a.Index); break;
                case Expression.Member m: Address(m.Base); break;
                case Expression.Swizzle s: Address(s.Vector); break;
                case Expression.Select s: Value(s.Condition); Address(s.Accept); Address(s.Reject); break;
                case Expression.Call c: Call(c); break;
            }
        }
        private void Value(Expression expression)
        {
            switch (expression) {
                case Expression.Reference r:
                    if (r.Type is not ShaderType.Pointer && Place(r)) reads.UnionWith(Origins(r)); break;
                case Expression.Load l: Address(l.Pointer); reads.UnionWith(Origins(l.Pointer)); break;
                case Expression.Unary { Operator: "&" or "*" } u: Address(u); break;
                case Expression.Unary u: Value(u.Operand); break;
                case Expression.Binary b: Value(b.Left); Value(b.Right); break;
                case Expression.Call c: Call(c); break;
                case Expression.Convert c: Value(c.Operand); break;
                case Expression.Construct c: foreach (var part in c.Components) Value(part); break;
                case Expression.Access or Expression.Member or Expression.Swizzle when Place(expression):
                    Address(expression); if (expression.Type is not ShaderType.Pointer) reads.UnionWith(Origins(expression)); break;
                case Expression.Access a: Value(a.Base); Value(a.Index); break;
                case Expression.Member m: Value(m.Base); break;
                case Expression.Swizzle s: Value(s.Vector); break;
                case Expression.Select s: Value(s.Condition); Value(s.Accept); Value(s.Reject); break;
            }
        }
        private void Call(Expression.Call call)
        {
            var arguments = new HashSet<Root>[call.Arguments.Count];
            for (int i = 0; i < arguments.Length; i++) {
                var argument = call.Arguments[i];
                if (argument.Type is ShaderType.Pointer) { Address(argument); arguments[i] = Origins(argument); }
                else { Value(argument); arguments[i] = []; }
            }
            if (call.Binding == CallBinding.Builtin) {
                // Synchronization effects describe ordering of surrounding memory;
                // a collective load still only reads its pointer operand.
                var effects = PointerEffects(call.Function);
                foreach (var roots in arguments) {
                    if ((effects & ShaderEffects.ReadMemory) != 0) reads.UnionWith(roots);
                    if ((effects & ShaderEffects.WriteMemory) != 0) writes.UnionWith(roots);
                }
                return;
            }
            if (!userFunction(call.Function)) throw new ShaderException(stage, "Unknown resolved function call.", call.Span);
            ApplyCall(function.Name, call.Function, call.Arguments.Select(a => a.Type is ShaderType.Pointer).ToArray(),
                arguments, callee(call.Function), reads, writes, stage, call.Span);
        }
        private void Body(Block body, bool scoped = true)
        {
            var saved = names.ToArray();
            foreach (var statement in body.Statements) {
                switch (statement) {
                    case Statement.Declare d:
                        if (d.Initializer is { } initial) { if (d.Type is ShaderType.Pointer) Address(initial); else Value(initial); }
                        names[d.Name] = new(d.Type is ShaderType.Pointer && d.Initializer is { } pointer ? Origins(pointer)
                            : d.Mutable ? [new(Local: ++local)] : [], d.Mutable); break;
                    case Statement.Store s: Address(s.Target); Value(s.Value); writes.UnionWith(Origins(s.Target)); break;
                    case Statement.Evaluate e: Value(e.Value); break;
                    case Statement.Return { Value: { } value }: Value(value); break;
                    case Statement.Nested n: Body(n.Body); break;
                    case Statement.If i: Value(i.Condition); Body(i.Accept); Body(i.Reject); break;
                    case Statement.Switch s: Value(s.Selector); foreach (var arm in s.Cases) Body(arm.Body); break;
                    case Statement.Loop l:
                        var before = names.ToArray(); Body(l.Body, false); Body(l.Continuing, false);
                        if (l.BreakIf is { } condition) Value(condition);
                        names.Clear(); foreach (var pair in before) names.Add(pair.Key, pair.Value); break;
                }
            }
            if (scoped) { names.Clear(); foreach (var pair in saved) names.Add(pair.Key, pair.Value); }
        }
    }
}
