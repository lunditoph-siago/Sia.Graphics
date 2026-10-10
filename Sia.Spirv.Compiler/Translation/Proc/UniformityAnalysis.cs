using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Proc;

internal sealed record UniformityDiagnostic(DiagnosticSeverity Severity, string? Rule, ShaderDiagnostic Diagnostic);

/// <summary>Per-compilation value/control dependencies and bottom-up helper summaries.
/// Canonical instructions are analyzed directly; unmigrated families retain the structured path.</summary>
internal static partial class UniformityAnalysis
{
    private enum Source { Control, Argument, Contents, NonUniform }
    private readonly record struct Dependency(Source Source, int Index = 0);
    private sealed class Node(Dependency? boundary = null)
    {
        public Dependency? Boundary { get; } = boundary;
        public HashSet<Node> Edges { get; } = [];
    }
    private sealed record Requirement(HashSet<Dependency> Dependencies, DiagnosticSeverity Severity, string? Rule, string Cause, SourceSpan Span,
        (string Function, int Value)? Collective = null);
    private sealed record Summary(HashSet<Dependency> Result, Dictionary<int, HashSet<Dependency>> Contents, List<Requirement> Requirements,
        HashSet<Dependency> NonReturningControl, bool MayReturn);
    private sealed class Variable(bool mutable, ShaderType type)
    {
        public bool Mutable { get; } = mutable;
        public ShaderType Type { get; } = type;
        public Address? Alias { get; set; }
    }
    private sealed record Address(Variable? Variable, string? Global, Node Value, bool Partial);
    private sealed class State(Node control)
    {
        public Node Control { get; set; } = control;
        public bool Aborted { get; set; }
        public Dictionary<string, Variable> Names { get; } = new(StringComparer.Ordinal);
        public Dictionary<Variable, Node> Values { get; } = [];
        public State Copy() { var result = new State(Control) { Aborted = Aborted }; foreach (var pair in Names) result.Names.Add(pair.Key, pair.Value); foreach (var pair in Values) result.Values.Add(pair.Key, pair.Value); return result; }
    }
    private enum Exit { Next, Break, Continue, Return, Abort }
    private sealed record Path(Exit Exit, State State);

    public static IReadOnlyList<UniformityDiagnostic> Validate(Module module, DiagnosticStage stage = DiagnosticStage.Validation,
        ICollection<CanonicalDeferral>? deferrals = null)
        => Check(Analyze(module, deferrals), stage);

    public static IReadOnlyList<UniformityDiagnostic> Validate(Module module, IReadOnlyDictionary<string, ControlFlowFunction> graphs,
        DiagnosticStage stage = DiagnosticStage.Validation) => Check(Analyze(module, graphs), stage);

    private static IReadOnlyList<UniformityDiagnostic> Check(IReadOnlyList<UniformityDiagnostic> diagnostics, DiagnosticStage stage)
    {
        if (diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error) is { } error)
            throw new ShaderException(stage, error.Diagnostic.Message, error.Diagnostic.Span);
        return diagnostics;
    }

    public static IReadOnlyList<UniformityDiagnostic> Analyze(Module module, ICollection<CanonicalDeferral>? deferrals = null)
    {
        var graphs = new Dictionary<string, ControlFlowFunction>(StringComparer.Ordinal);
        foreach (var function in module.Functions)
            if (StructuredControlFlowReader.TryRead(function, module, out var graph, out var reason)) {
                ControlFlowAnalysis.RemoveUnreachable(graph!); graphs.Add(function.Name, graph!);
            }
            else deferrals?.Add(new(function.Name, reason!));
        return Analyze(module, graphs);
    }

    public static IReadOnlyList<UniformityDiagnostic> Analyze(Module module, IReadOnlyDictionary<string, ControlFlowFunction> graphs)
        => AnalyzeCore(module, CollectiveReadRecovery.Recover(module, PrepareGraphs(module, graphs)));

    internal static IReadOnlyDictionary<string, ControlFlowFunction> PrepareGraphs(Module module, IReadOnlyDictionary<string, ControlFlowFunction> graphs)
    {
        var effects = ShaderEffectAnalysis.Compute(module, graphs);
        graphs = graphs.ToDictionary(p => p.Key, p => CanonicalHelperInliner.Run(p.Value, module, graphs, calleeEffects: effects), StringComparer.Ordinal);
        foreach (var graph in graphs.Values) ControlFlowVerifier.Validate(graph, module, calleeEffects: effects);
        return graphs;
    }

    // The recovery proof analyzes its hypothetical collective calls without recursively recovering them.
    internal static IReadOnlyList<UniformityDiagnostic> AnalyzeCore(Module module, IReadOnlyDictionary<string, ControlFlowFunction> graphs,
        ISet<(string Function, int Value)>? divergentCollectives = null)
    {
        var summaries = new Dictionary<string, Summary>(StringComparer.Ordinal);
        var functions = module.Functions.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        var diagnostics = new List<UniformityDiagnostic>();
        Summary Function(string name) {
            if (summaries.TryGetValue(name, out var summary)) return summary;
            if (!active.Add(name)) throw new ShaderException(DiagnosticStage.Validation, "Recursive shader function calls are forbidden.");
            summary = graphs.TryGetValue(name, out var graph)
                ? new CanonicalAnalysis(module, graph, Function, divergentCollectives).Run(diagnostics)
                : new FunctionAnalysis(module, functions[name], Function, functions.ContainsKey, divergentCollectives).Run(diagnostics);
            active.Remove(name); summaries.Add(name, summary); return summary;
        }
        foreach (var function in module.Functions) _ = Function(function.Name);
        return diagnostics;
    }

    private static Node Join(params Node[] inputs) { var node = new Node(); node.Edges.UnionWith(inputs); return node; }
    private static HashSet<Dependency> Dependencies(Node root)
    {
        var result = new HashSet<Dependency>(); var visited = new HashSet<Node>(); var pending = new Stack<Node>(); pending.Push(root);
        while (pending.TryPop(out var node)) {
            if (!visited.Add(node)) continue;
            if (node.Boundary is { } boundary) result.Add(boundary);
            foreach (var edge in node.Edges) pending.Push(edge);
        }
        return result;
    }

    private static bool UniformInput(ShaderType type, IoBinding? binding, ShaderStage? stage) => type is ShaderType.Structure structure
        ? structure.Members.All(m => UniformInput(m.Type, m.Binding, stage))
        : binding?.Builtin is "workgroup_id" or "num_workgroups" or "num_subgroups"
            || binding?.Builtin == "subgroup_size" && stage == ShaderStage.Compute;

    private static string? BuiltinRule(string name) => name.StartsWith("dpdx", StringComparison.Ordinal) || name.StartsWith("dpdy", StringComparison.Ordinal)
        || name.StartsWith("fwidth", StringComparison.Ordinal) || name is "textureSample" or "textureSampleBias" or "textureSampleCompare"
        ? "derivative_uniformity" : SubgroupBuiltins.Contains(name) || name == "subgroupBarrier" ? "subgroup_uniformity" : null;

    private static Node BuiltinValue(string name, Node[] arguments, ShaderType? firstType, Node control, Node nonUniform,
        Action<Node, string?, string> require)
    {
        string? rule = BuiltinRule(name);
        bool synchronization = name is "workgroupBarrier" or "storageBarrier" or "textureBarrier" or "workgroupUniformLoad";
        if (rule is not null || synchronization) require(control, rule, name);
        if (name == "workgroupUniformLoad") {
            if (arguments.Length != 0) require(arguments[0], null, name + " pointer");
            return control;
        }
        if (name is "subgroupShuffleUp" or "subgroupShuffleDown" or "subgroupShuffleXor" && arguments.Length > 1)
            require(arguments[1], rule, name + " index");
        bool nonuniform = rule is not null || (ShaderBuiltinEffects.For(name) & ShaderEffects.Atomic) != 0
            || ShaderBuiltinEffects.IsQuery(name)
            || name == "textureLoad" && firstType is ShaderType.Image { Access: var access } && (access & StorageAccess.Write) != 0
            || !ShaderBuiltinEffects.IsKnown(name);
        return Join(arguments.Prepend(control).Concat(nonuniform ? [nonUniform] : []).ToArray());
    }

    private static Summary Finish(string name, Node returns, Dictionary<int, Node> contents,
        List<(Node Node, DiagnosticSeverity Severity, string? Rule, string Cause, SourceSpan Span, (string Function, int Value)? Collective)> requirements, List<UniformityDiagnostic> diagnostics,
        Node nonReturningControl, bool mayReturn, ISet<(string Function, int Value)>? divergentCollectives)
    {
        var constraints = new List<Requirement>();
        foreach (var requirement in requirements) {
            var dependencies = Dependencies(requirement.Node);
            if (dependencies.Contains(new(Source.NonUniform))) {
                if (requirement.Collective is { } site) divergentCollectives?.Add(site);
                diagnostics.Add(new(requirement.Severity, requirement.Rule, new(DiagnosticStage.Validation,
                    $"Uniformity violation in '{name}': {requirement.Cause} requires uniform control or arguments" +
                    (requirement.Rule is null ? "." : $" ({requirement.Rule})."), requirement.Span)));
            }
            constraints.Add(new(dependencies, requirement.Severity, requirement.Rule, requirement.Cause, requirement.Span, requirement.Collective));
        }
        return new(Dependencies(returns), contents.ToDictionary(p => p.Key, p => Dependencies(p.Value)), constraints, Dependencies(nonReturningControl), mayReturn);
    }

    private sealed class FunctionAnalysis(Module module, ShaderFunction function, Func<string, Summary> callee, Func<string, bool> userFunction,
        ISet<(string Function, int Value)>? divergentCollectives)
    {
        private readonly Node nonUniform = new(new(Source.NonUniform));
        private readonly Node start = new(new(Source.Control));
        private readonly Node returns = new();
        private readonly Node nonReturningControl = new();
        private readonly Stack<IReadOnlyList<DiagnosticFilter>> diagnosticScopes = [];
        private readonly Dictionary<int, (Variable Variable, Node Output)> pointerParameters = [];
        private readonly List<(Node Node, DiagnosticSeverity Severity, string? Rule, string Cause, SourceSpan Span, (string Function, int Value)? Collective)> requirements = [];
        private readonly Dictionary<string, GlobalVariable> globals = module.Globals.ToDictionary(g => g.Name, StringComparer.Ordinal);

        public Summary Run(List<UniformityDiagnostic> diagnostics)
        {
            var state = new State(start);
            for (int i = 0; i < function.Arguments.Count; i++) {
                var argument = function.Arguments[i]; var variable = new Variable(false, argument.Type); state.Names.Add(argument.Name, variable);
                Node value = function.Stage is null ? new(new(Source.Argument, i)) : UniformInput(argument.Type, argument.Binding, function.Stage) ? start : nonUniform;
                state.Values.Add(variable, value);
                if (argument.Type is ShaderType.Pointer pointer) {
                    if (pointer.Space == AddressSpace.Function) {
                        var contents = new Variable(true, pointer.Base); state.Values.Add(contents, new(new(Source.Contents, i)));
                        variable.Alias = new(contents, null, value, pointer.Base is not ShaderType.Scalar); pointerParameters.Add(i, (contents, new Node()));
                    }
                    else variable.Alias = new(null, null, value, true);
                }
            }
            var paths = Body(function.Body, state, scoped: false);
            foreach (var path in paths.Where(p => p.Exit == Exit.Next)) ReturnContents(path.State);
            foreach (var path in paths.Where(p => p.Exit == Exit.Abort)) nonReturningControl.Edges.Add(path.State.Control);
            return Finish(function.Name, returns, pointerParameters.ToDictionary(p => p.Key, p => p.Value.Output), requirements, diagnostics,
                nonReturningControl, paths.Any(p => p.Exit is Exit.Next or Exit.Return), divergentCollectives);
        }

        private DiagnosticSeverity Severity(string? rule) => rule is null ? DiagnosticSeverity.Error
            : diagnosticScopes.SelectMany(s => s).Concat(function.DiagnosticFilters).Concat(module.DiagnosticFilters).FirstOrDefault(f => f.Namespace is null && f.Rule == rule)?.Severity ?? DiagnosticSeverity.Error;
        private void Require(Node value, string? rule, string cause, SourceSpan span, DiagnosticSeverity? severity = null,
            (string Function, int Value)? collective = null)
        {
            var selected = severity ?? Severity(rule);
            if (selected != DiagnosticSeverity.Off) requirements.Add((value, selected, rule, cause, span, collective));
        }
        private void ReturnContents(State state) {
            foreach (var parameter in pointerParameters.Values) parameter.Output.Edges.Add(Join(state.Control, state.Values[parameter.Variable]));
        }

        private Address Place(Expression expression, State state)
        {
            switch (expression) {
                case Expression.Reference reference:
                    if (state.Names.TryGetValue(reference.Name, out var variable))
                        return variable.Alias ?? new(variable, null, state.Control, false);
                    return new(null, reference.Name, state.Control, false);
                case Expression.Unary { Operator: "&" or "*" } unary: return Place(unary.Operand, state);
                case Expression.Access access:
                    var root = Place(access.Base, state); return root with { Value = Join(root.Value, Value(access.Index, state)), Partial = true };
                case Expression.Member member: return Place(member.Base, state) with { Partial = true };
                case Expression.Swizzle swizzle: return Place(swizzle.Vector, state) with { Partial = true };
                default: return new(null, null, Join(state.Control, nonUniform), true);
            }
        }
        private Node Read(Address address, State state)
        {
            if (address.Variable is { } variable) return Join(state.Control, address.Value, state.Values[variable]);
            bool readOnly = address.Global is { } name && globals.TryGetValue(name, out var global)
                && (global.Space is AddressSpace.Uniform or AddressSpace.Immediate or AddressSpace.Handle || (global.Access & StorageAccess.Write) == 0);
            return Join(state.Control, address.Value, readOnly ? state.Control : nonUniform);
        }
        private void Store(Address address, Node value, State state) {
            if (address.Variable is { } variable)
                state.Values[variable] = address.Partial ? Join(state.Control, address.Value, value, state.Values[variable]) : Join(state.Control, address.Value, value);
        }
        private bool IsPlace(Expression expression, State state) => expression switch {
            Expression.Reference reference => state.Names.TryGetValue(reference.Name, out var variable) ? variable.Mutable || variable.Alias is not null
                : globals.TryGetValue(reference.Name, out var global) && global.Space != AddressSpace.Handle,
            Expression.Access access => IsPlace(access.Base, state), Expression.Member member => IsPlace(member.Base, state),
            Expression.Swizzle swizzle => IsPlace(swizzle.Vector, state), Expression.Unary { Operator: "*" } => true, _ => false
        };
        private Node Value(Expression expression, State state)
        {
            switch (expression) {
                case Expression.Literal: return state.Control;
                case Expression.HelperInvocation: return Join(state.Control, nonUniform);
                case Expression.Reference reference:
                    if (state.Names.TryGetValue(reference.Name, out var variable)) return Join(state.Control, variable.Alias?.Value ?? state.Values[variable]);
                    if (module.Constants.Any(c => c.Name == reference.Name)) return state.Control;
                    return expression.Type is ShaderType.Pointer ? Place(expression, state).Value : Read(Place(expression, state), state);
                case Expression.Load load: return Read(Place(load.Pointer, state), state);
                case Expression.Unary { Operator: "&" } address: return Place(address.Operand, state).Value;
                case Expression.Unary { Operator: "*" } dereference: return Read(Place(dereference.Operand, state), state);
                case Expression.Unary unary: return Value(unary.Operand, state);
                case Expression.Binary binary:
                    var left = Value(binary.Left, state);
                    if (binary.Operator is "&&" or "||") {
                        var rhs = state.Copy(); rhs.Control = left; var result = Value(binary.Right, rhs);
                        foreach (var slot in state.Values.Keys.ToArray())
                            if (rhs.Values[slot] != state.Values[slot]) state.Values[slot] = Join(state.Values[slot], rhs.Values[slot]);
                        return result;
                    }
                    return Join(left, Value(binary.Right, state));
                case Expression.Convert convert: return Value(convert.Operand, state);
                case Expression.Construct construct: return Join(construct.Components.Select(v => Value(v, state)).Prepend(state.Control).ToArray());
                case Expression.Select select: return Join(Value(select.Condition, state), Value(select.Accept, state), Value(select.Reject, state));
                case Expression.Access access:
                    return access.Type is ShaderType.Pointer ? Place(access, state).Value : IsPlace(access.Base, state)
                        ? Read(Place(access, state), state) : Join(Value(access.Base, state), Value(access.Index, state));
                case Expression.Member member: return member.Type is ShaderType.Pointer ? Place(member, state).Value : IsPlace(member.Base, state)
                    ? Read(Place(member, state), state) : Value(member.Base, state);
                case Expression.Swizzle swizzle: return IsPlace(swizzle.Vector, state) ? Read(Place(swizzle, state), state) : Value(swizzle.Vector, state);
                case Expression.Call call: return Call(call, state);
                default: return Join(state.Control, nonUniform);
            }
        }
        private Node Call(Expression.Call call, State state)
        {
            var arguments = new Node[call.Arguments.Count]; var addresses = new Address?[call.Arguments.Count];
            for (int i = 0; i < arguments.Length; i++) {
                if (call.Arguments[i].Type is ShaderType.Pointer) {
                    addresses[i] = Place(call.Arguments[i], state); arguments[i] = Join(state.Control, addresses[i]!.Value);
                }
                else arguments[i] = Value(call.Arguments[i], state);
            }
            var contents = addresses.Select(a => a is null ? state.Control : Read(a, state)).ToArray();
            if (call.Binding != CallBinding.Builtin && userFunction(call.Function)) {
                var summary = callee(call.Function);
                Node Bind(IEnumerable<Dependency> dependencies) => Join(dependencies.Select(d => d.Source switch {
                    Source.Control => state.Control, Source.NonUniform => nonUniform,
                    Source.Argument => d.Index < arguments.Length ? arguments[d.Index] : nonUniform,
                    Source.Contents => d.Index < contents.Length ? contents[d.Index] : nonUniform, _ => nonUniform
                }).ToArray());
                foreach (var requirement in summary.Requirements)
                    Require(Bind(requirement.Dependencies), requirement.Rule, call.Function + " -> " + requirement.Cause, call.Span, requirement.Severity, requirement.Collective);
                var result = Join(state.Control, Bind(summary.Result));
                var writes = summary.Contents.Select(p => (Index: p.Key, Value: Bind(p.Value))).ToArray();
                foreach (var write in writes) if (write.Index < addresses.Length && addresses[write.Index] is { } address) Store(address, write.Value, state);
                if (summary.NonReturningControl.Count != 0 || !summary.MayReturn) {
                    var abortControl = Join(state.Control, Bind(summary.NonReturningControl));
                    nonReturningControl.Edges.Add(abortControl); state.Control = abortControl;
                }
                state.Aborted |= !summary.MayReturn;
                return result;
            }
            return BuiltinValue(call.Function, arguments, call.Arguments.FirstOrDefault()?.Type, state.Control, nonUniform,
                (value, rule, cause) => Require(value, rule, cause, call.Span));
        }

        private State Merge(State input, IEnumerable<State> branches, Node control)
        {
            var result = input.Copy(); result.Control = control; var paths = branches.ToArray();
            foreach (var variable in input.Values.Keys) result.Values[variable] = Join(paths.Select(p => p.Values.GetValueOrDefault(variable, input.Values[variable])).ToArray());
            return result;
        }
        private List<Path> Body(Block body, State state, bool scoped = true)
        {
            diagnosticScopes.Push(body.DiagnosticFilters);
            var names = state.Names.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            var paths = new List<Path>(); State? current = state;
            foreach (var statement in body.Statements) {
                if (current is null) break;
                var next = Visit(statement, current).Select(p => p.State.Aborted ? p with { Exit = Exit.Abort } : p).ToList();
                paths.AddRange(next.Where(p => p.Exit != Exit.Next));
                var continuing = next.Where(p => p.Exit == Exit.Next).Select(p => p.State).ToArray();
                current = continuing.Length == 0 ? null : continuing.Length == 1 ? continuing[0] : Merge(current, continuing, Join(continuing.Select(p => p.Control).ToArray()));
            }
            if (current is not null) paths.Add(new(Exit.Next, current));
            if (scoped) foreach (var path in paths) { path.State.Names.Clear(); foreach (var name in names) path.State.Names.Add(name.Key, name.Value); }
            diagnosticScopes.Pop(); return paths;
        }
        private List<Path> Visit(Statement statement, State state)
        {
            switch (statement) {
                case Statement.Declare declaration:
                    var initial = declaration.Initializer is null ? declaration.Initialize ? state.Control : Join(state.Control, nonUniform) : Value(declaration.Initializer, state);
                    var variable = new Variable(declaration.Mutable, declaration.Type);
                    if (declaration.Type is ShaderType.Pointer && declaration.Initializer is { } initializer) variable.Alias = Place(initializer, state);
                    state.Names[declaration.Name] = variable; state.Values.Add(variable, initial); break;
                case Statement.Store store: var address = Place(store.Target, state); Store(address, Value(store.Value, state), state); break;
                case Statement.Evaluate evaluate: _ = Value(evaluate.Value, state); break;
                case Statement.Barrier barrier: Require(state.Control, barrier.Subgroup ? "subgroup_uniformity" : null, "barrier", barrier.Span); break;
                case Statement.MemoryBarrier: break; // An execution barrier cannot be inferred from a native memory fence.
                case Statement.MeshStore store:
                    _ = Value(store.Index, state); _ = Value(store.Value, state); break;
                case Statement.MeshSetOutputs counts:
                    _ = Value(counts.Vertices, state); _ = Value(counts.Primitives, state);
                    Require(state.Control, null, "mesh output publication", counts.Span); break;
                case Statement.TaskDispatch dispatch:
                    _ = Value(dispatch.Dimensions, state);
                    Require(state.Control, null, "task dispatch", dispatch.Span); return [new(Exit.Abort, state)];
                case Statement.Nested nested: return Body(nested.Body, state);
                case Statement.Return ret:
                    if (ret.Value is { } value) returns.Edges.Add(Value(value, state)); ReturnContents(state); return [new(Exit.Return, state)];
                case Statement.Kill: break; // Demotion retains normal helper execution and control flow.
                // This path does not return a value or pointer contents to its caller.
                case Statement.Unreachable: return [new(Exit.Abort, state)];
                case Statement.InvocationKill: return [new(Exit.Abort, state)];
                case Statement.Break: return [new(Exit.Break, state)];
                case Statement.Continue: return [new(Exit.Continue, state)];
                case Statement.If branch:
                    var condition = Value(branch.Condition, state); var accept = state.Copy(); accept.Control = condition; var reject = state.Copy(); reject.Control = condition;
                    return Selection(state, Body(branch.Accept, accept).Concat(Body(branch.Reject, reject)).ToList());
                case Statement.Switch selection:
                    var selector = Value(selection.Selector, state); var arms = new List<Path>();
                    foreach (var arm in selection.Cases) { var entry = state.Copy(); entry.Control = selector; arms.AddRange(Body(arm.Body, entry).Select(p => p.Exit == Exit.Break ? p with { Exit = Exit.Next } : p)); }
                    if (!selection.Cases.Any(c => c.IsDefault)) { var entry = state.Copy(); entry.Control = selector; arms.Add(new(Exit.Next, entry)); }
                    return Selection(state, arms);
                case Statement.Loop loop: return Loop(loop, state);
            }
            return [new(Exit.Next, state)];
        }
        private List<Path> Selection(State input, List<Path> paths)
        {
            var next = paths.Where(p => p.Exit == Exit.Next).Select(p => p.State).ToArray();
            if (next.Length == 0) return paths;
            var control = paths.All(p => p.Exit == Exit.Next) ? input.Control : Join(paths.Select(p => p.State.Control).ToArray());
            return paths.Where(p => p.Exit != Exit.Next).Append(new Path(Exit.Next, Merge(input, next, control))).ToList();
        }
        private List<Path> Loop(Statement.Loop loop, State input)
        {
            diagnosticScopes.Push(loop.Body.DiagnosticFilters);
            var header = input.Copy(); header.Control = Join(input.Control);
            var phis = input.Values.Where(p => p.Key.Mutable).ToDictionary(p => p.Key, p => Join(p.Value));
            foreach (var phi in phis) header.Values[phi.Key] = phi.Value;
            var body = Body(loop.Body, header, scoped: false);
            var breaks = body.Where(p => p.Exit == Exit.Break).Select(p => p.State).ToList();
            var returns = body.Where(p => p.Exit is Exit.Return or Exit.Abort).ToList();
            var continuing = body.Where(p => p.Exit is Exit.Next or Exit.Continue).Select(p => p.State).ToArray();
            if (continuing.Length != 0) {
                var entry = continuing[0].Copy(); entry = Merge(entry, continuing, Join(continuing.Select(p => p.Control).ToArray()));
                diagnosticScopes.Push(loop.Continuing.DiagnosticFilters);
                foreach (var path in Body(loop.Continuing, entry, scoped: false)) {
                    if (path.Exit is Exit.Return or Exit.Abort) { returns.Add(path); continue; }
                    if (path.Exit == Exit.Break) { breaks.Add(path.State); continue; }
                    if (loop.BreakIf is { } condition) { path.State.Control = Value(condition, path.State); breaks.Add(path.State); }
                    header.Control.Edges.Add(path.State.Control);
                    foreach (var phi in phis) phi.Value.Edges.Add(path.State.Values.GetValueOrDefault(phi.Key, phi.Value));
                }
                diagnosticScopes.Pop();
            }
            if (breaks.Count != 0) {
                var control = returns.Count == 0 ? input.Control : Join(body.Select(p => p.State.Control).Prepend(header.Control).ToArray());
                returns.Add(new(Exit.Next, Merge(input, breaks, control)));
            }
            diagnosticScopes.Pop(); return returns;
        }
    }
}
