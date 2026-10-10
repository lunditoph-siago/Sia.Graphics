using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Tests;

/// <summary>Test-only scalar interpreter with C# arithmetic, independent of compiler folding and SSA passes.</summary>
internal sealed class CanonicalExecution(Module module, uint[] input, bool initiallyHelper = false)
{
    private sealed record Address(string Name, int? Index = null, Dictionary<string, object>? Owner = null);
    private enum Flow { Next, Break, Continue, Return }
    private readonly Stack<Dictionary<string, object>> scopes = [];
    private readonly uint[] output = new uint[2];
    private readonly List<int> reads = [];
    private int budget = 200000;
    private object? returnedValue;
    private sealed class InvocationTermination : Exception;
    public bool InvocationKilled { get; private set; } = initiallyHelper;
    public List<bool> HelperQueryValues { get; } = [];
    public object? EntryResult => returnedValue;

    public (uint[] Output, int[] Reads) Run()
    {
        scopes.Push(new(StringComparer.Ordinal));
        foreach (var constant in module.Constants) scopes.Peek().Add(constant.Name, E(constant.Value!));
        foreach (var global in module.Globals.Where(g => g.Space == AddressSpace.Private))
            scopes.Peek().Add(global.Name, global.Initializer is not null ? E(global.Initializer)
                : global.Type == ShaderType.Bool ? (object)false : global.Type == ShaderType.U32 || global.Type == ShaderType.I32
                    ? 0u : throw new NotSupportedException("Test private global " + global.Name));
        try { _ = Body(module.Functions.Single(f => f.Stage is not null).Body); }
        catch (InvocationTermination) { InvocationKilled = true; }
        return (output, reads.ToArray());
    }
    private object Get(string name) {
        foreach (var scope in scopes) if (scope.TryGetValue(name, out var value)) return value;
        throw new InvalidOperationException("Unknown test local " + name);
    }
    private object Read(Address address) {
        if (address.Owner is not null) return address.Owner[address.Name];
        var global = module.Globals.SingleOrDefault(g => g.Name == address.Name);
        if (global is null) return Get(address.Name);
        if (global.Binding is null) throw new NotSupportedException("Test global " + global.Name);
        int index = address.Index ?? 0;
        if (global.Binding!.Binding == 0) { reads.Add(index); return input[index]; }
        return output[index];
    }
    private void Store(Address address, object value) {
        if (address.Owner is not null) { address.Owner[address.Name] = value; return; }
        var global = module.Globals.SingleOrDefault(g => g.Name == address.Name);
        if (global is not null) {
            if (global.Binding is null) throw new NotSupportedException("Test global " + global.Name);
            if (global.Binding!.Binding != 1) throw new InvalidOperationException("Unexpected write to input");
            if (!InvocationKilled) output[address.Index ?? 0] = (uint)value; return;
        }
        foreach (var scope in scopes) if (scope.ContainsKey(address.Name)) { scope[address.Name] = value; return; }
        throw new InvalidOperationException("Unknown test store " + address.Name);
    }
    private Address ReferenceAddress(string name) {
        foreach (var scope in scopes) if (scope.TryGetValue(name, out var value))
            return value is Address address ? address : new Address(name, Owner: scope);
        return new Address(name);
    }
    private Address P(Expression expression) => expression switch {
        Expression.Reference reference => ReferenceAddress(reference.Name),
        Expression.Access access => P(access.Base) with { Index = checked((int)(uint)E(access.Index)) },
        Expression.Member member => P(member.Base),
        Expression.Unary { Operator: "&" } unary => P(unary.Operand),
        Expression.Unary { Operator: "*" } unary => (Address)E(unary.Operand),
        _ => (Address)E(expression)
    };
    private object E(Expression expression)
    {
        switch (expression) {
            case Expression.HelperInvocation: HelperQueryValues.Add(InvocationKilled); return InvocationKilled;
            case Expression.Literal literal: return literal.Value is int integer ? unchecked((uint)integer) : literal.Value;
            case Expression.Reference reference:
                if (reference.Type is not ShaderType.Pointer) return Get(reference.Name);
                return ReferenceAddress(reference.Name);
            case Expression.Load load: return Read(P(load.Pointer));
            case Expression.Access access: return ((Address)E(access.Base)) with { Index = checked((int)(uint)E(access.Index)) };
            case Expression.Member member: return E(member.Base); // Native array buffer wrappers have one data member.
            case Expression.Construct construct when construct.Components.Count == 0: return construct.Type == ShaderType.Bool ? (object)false : 0u;
            case Expression.Construct construct when construct.Components.Count == 1: return E(construct.Components[0]);
            case Expression.Convert convert: return E(convert.Operand);
            case Expression.Unary { Operator: "&" } addressOf: return P(addressOf.Operand);
            case Expression.Unary unary:
                var operand = E(unary.Operand);
                return unary.Operator switch { "!" => !(bool)operand, "~" => ~(uint)operand, "-" => unchecked(0u - (uint)operand), _ => throw new NotSupportedException(unary.Operator) };
            case Expression.Binary binary:
                if (binary.Operator == "&&") return (bool)E(binary.Left) && (bool)E(binary.Right);
                if (binary.Operator == "||") return (bool)E(binary.Left) || (bool)E(binary.Right);
                var left = E(binary.Left); var right = E(binary.Right);
                if (left is bool boolean) return binary.Operator switch { "==" => boolean == (bool)right, "!=" => boolean != (bool)right, _ => throw new NotSupportedException(binary.Operator) };
                uint a = (uint)left, b = (uint)right;
                bool signed = binary.Left.Type == ShaderType.I32;
                return binary.Operator switch {
                    "+" => unchecked(a + b), "-" => unchecked(a - b), "*" => unchecked(a * b),
                    "/" => signed ? unchecked((uint)((int)a / (int)b)) : a / b,
                    "%" => signed ? unchecked((uint)((int)a % (int)b)) : a % b,
                    "&" => a & b, "|" => a | b, "^" => a ^ b,
                    "<<" => a << (int)b, ">>" => signed ? unchecked((uint)((int)a >> (int)b)) : a >> (int)b,
                    "==" => a == b, "!=" => a != b,
                    "<" => signed ? (int)a < (int)b : a < b, ">" => signed ? (int)a > (int)b : a > b,
                    "<=" => signed ? (int)a <= (int)b : a <= b, ">=" => signed ? (int)a >= (int)b : a >= b,
                    _ => throw new NotSupportedException(binary.Operator)
                };
            case Expression.Select select:
                var reject = E(select.Reject); var accept = E(select.Accept); return (bool)E(select.Condition) ? accept : reject;
            case Expression.Call { Function: "arrayLength" } length:
                var array = (Address)E(length.Arguments[0]);
                return (uint)(module.Globals.Single(g => g.Name == array.Name).Binding!.Binding == 0 ? input.Length : output.Length);
            case Expression.Call { Function: "min" } min: return System.Math.Min((uint)E(min.Arguments[0]), (uint)E(min.Arguments[1]));
            case Expression.Call { Function: "max" } max: return System.Math.Max((uint)E(max.Arguments[0]), (uint)E(max.Arguments[1]));
            case Expression.Call { Function: "select", Arguments.Count: 3 } selection:
                // WGSL evaluates argument expressions before choosing b when cond is true.
                var rejected = E(selection.Arguments[0]); var accepted = E(selection.Arguments[1]);
                return (bool)E(selection.Arguments[2]) ? accepted : rejected;
            case Expression.Call call:
                var function = module.Functions.SingleOrDefault(f => f.Name == call.Function)
                    ?? throw new NotSupportedException("Test call " + call.Function);
                var arguments = call.Arguments.Select(E).ToArray();
                var caller = scopes.ToArray(); var previousReturn = returnedValue;
                scopes.Clear(); scopes.Push(caller.Last());
                scopes.Push(function.Arguments.Select((a, i) => (a.Name, Value: arguments[i])).ToDictionary(a => a.Name, a => a.Value));
                try {
                    returnedValue = null; _ = Body(function.Body);
                    return returnedValue ?? (function.ReturnType is ShaderType.Void ? 0u : throw new InvalidOperationException("Missing test return"));
                }
                finally { returnedValue = previousReturn; scopes.Clear(); foreach (var scope in caller.Reverse()) scopes.Push(scope); }
            default: throw new NotSupportedException(expression.GetType().Name);
        }
    }
    private Flow Body(Block block, bool scope = true)
    {
        if (scope) scopes.Push(new(StringComparer.Ordinal));
        try {
            foreach (var statement in block.Statements) {
                if (--budget == 0) throw new InvalidOperationException("Test interpreter step limit exceeded");
                switch (statement) {
                    case Statement.Declare declaration: scopes.Peek().Add(declaration.Name, declaration.Initializer is null
                        ? declaration.Type == ShaderType.Bool ? (object)false : 0u : E(declaration.Initializer)); break;
                    case Statement.Store store: var address = P(store.Target); Store(address, E(store.Value)); break;
                    case Statement.Evaluate evaluate: _ = E(evaluate.Value); break;
                    case Statement.Return returned: returnedValue = returned.Value is null ? null : E(returned.Value); return Flow.Return;
                    case Statement.Break: return Flow.Break;
                    case Statement.Continue: return Flow.Continue;
                    case Statement.Unreachable: throw new InvalidOperationException("Native unreachable has undefined execution; no numeric expectation exists.");
                    case Statement.InvocationKill: throw new InvocationTermination();
                    case Statement.Kill: InvocationKilled = true; break;
                    case Statement.Nested nested: var nestedFlow = Body(nested.Body); if (nestedFlow != Flow.Next) return nestedFlow; break;
                    case Statement.If conditional:
                        var branchFlow = Body((bool)E(conditional.Condition) ? conditional.Accept : conditional.Reject);
                        if (branchFlow != Flow.Next) return branchFlow; break;
                    case Statement.Switch selection:
                        uint selector = (uint)E(selection.Selector);
                        var selected = selection.Cases.FirstOrDefault(c => c.Values.Any(v => (uint)E(v) == selector))
                            ?? selection.Cases.Single(c => c.IsDefault);
                        var caseFlow = Body(selected.Body);
                        if (caseFlow is Flow.Continue or Flow.Return) return caseFlow; break;
                    case Statement.Loop loop:
                        while (true) {
                            if (--budget == 0) throw new InvalidOperationException("Test interpreter loop limit exceeded");
                            scopes.Push(new(StringComparer.Ordinal));
                            try {
                                var flow = Body(loop.Body, scope: false); if (flow == Flow.Return) return flow; if (flow == Flow.Break) break;
                                scopes.Push(new(StringComparer.Ordinal));
                                try {
                                    flow = Body(loop.Continuing, scope: false); if (flow == Flow.Return) return flow; if (flow == Flow.Break) break;
                                    if (loop.BreakIf is not null && (bool)E(loop.BreakIf)) break;
                                }
                                finally { scopes.Pop(); }
                            }
                            finally { scopes.Pop(); }
                        }
                        break;
                    default: throw new NotSupportedException(statement.GetType().Name);
                }
            }
            return Flow.Next;
        }
        finally { if (scope) scopes.Pop(); }
    }
}
