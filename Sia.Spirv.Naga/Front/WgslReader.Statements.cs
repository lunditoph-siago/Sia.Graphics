using Sia.Spirv.Naga.IR;

namespace Sia.Spirv.Naga.Front;

public static partial class WgslReader
{
    private sealed partial class Lowerer
    {
        private int loopDepth, switchDepth;
        private Block Body(SBlock source, bool newScope = true)
        {
            if (newScope) scopes.Push(new(StringComparer.Ordinal));
            var body = new Block();
            foreach (var statement in source.Statements) LowerStatement(statement, body);
            if (newScope) scopes.Pop();
            return body;
        }

        private void LowerStatement(SStatement source, Block block)
        {
            switch (source)
            {
                case SStatement.Nested n: block.Statements.Add(new Statement.Nested(Body(n.Body))); break;
                case SStatement.Declaration d:
                    var declaration = d.Value;
                    if (declaration.Attributes.Count != 0 || declaration.Qualifiers.Count > 1 || declaration.Qualifiers.Count == 1 && declaration.Qualifiers[0] is not SExpression.Name { Text: "function" }) throw Error("Invalid local variable qualifier.", declaration.Span);
                    var prelude = new Block();
                    Expression? initializer = declaration.Value is null ? null : Eval(declaration.Value, prelude);
                    ShaderType type = declaration.Type is not null ? ResolveType(declaration.Type) : initializer!.Type;
                    if (type is ShaderType.Array { OverrideLength: not null } or ShaderType.BindingArray { OverrideLength: not null })
                        throw Error("Local declaration type must be constructible before pipeline constant resolution.", declaration.Span);
                    if (declaration.Kind != "const") type = DefaultType(type);
                    if (initializer is not null) initializer = Materialize(initializer, type);
                    Symbol symbol;
                    if (declaration.Kind == "const")
                    {
                        if (initializer is null || prelude.Statements.Count != 0 || !ConstantEvaluator.TryEvaluate(initializer, out var constant)) throw Error("Local const needs a constant expression.", declaration.Span);
                        symbol = new(constant, false, false);
                    }
                    else
                    {
                        bool mutable = declaration.Kind == "var";
                        block.Statements.AddRange(prelude.Statements);
                        block.Statements.Add(new Statement.Declare(declaration.Name, type, initializer, mutable));
                        symbol = new(new Expression.Reference(declaration.Name, mutable ? new ShaderType.Pointer(type, AddressSpace.Function) : type), mutable, mutable);
                    }
                    if (!scopes.Peek().TryAdd(declaration.Name, symbol)) throw Error($"Duplicate local identifier '{declaration.Name}'.", declaration.Span);
                    break;
                case SStatement.Assignment a:
                    if (a.Target is SExpression.Name { Text: "_" })
                    {
                        if (a.Operator != "=" || a.Value is null) throw Error("Phony assignment requires '='.");
                        Expression ignored = Eval(a.Value, block);
                        if (ignored.Type is ShaderType.Void) throw Error("Cannot assign void.");
                        ignored = Materialize(ignored, DefaultType(ignored.Type));
                        block.Statements.Add(new Statement.Declare(Fresh(), ignored.Type, ignored, false)); break;
                    }
                    Symbol place = Place(a.Target, block);
                    if (!place.Writable) throw Error("Cannot write through an immutable or read-only reference.", a.Target.Span);
                    ShaderType target = ValueType(place.Value.Type);
                    Expression value;
                    if (a.Operator is "++" or "--")
                    {
                        if (target is not ShaderType.Scalar { Kind: ScalarKind.Uint or ScalarKind.Sint }) throw Error("Increment/decrement needs an integer scalar.");
                        value = Binary(a.Operator == "++" ? "+" : "-", Snapshot(new Expression.Load(place.Value), block), target == ShaderType.U32 ? Expression.U32(1) : Expression.I32(1));
                    }
                    else if (a.Operator == "=") value = Eval(a.Value!, block);
                    else
                    {
                        Expression previous = Snapshot(new Expression.Load(place.Value), block);
                        value = Binary(a.Operator[..^1], previous, Eval(a.Value!, block));
                    }
                    block.Statements.Add(new Statement.Store(place.Value, Materialize(value, target))); break;
                case SStatement.Evaluate e:
                    Expression evaluation = Eval(e.Value, block);
                    if (e.Value is SExpression.Call { Target: SExpression.Name called } &&
                        (mustUseFunctions.Contains(called.Text) || !functions.ContainsKey(called.Text) && evaluation.Type is not ShaderType.Void && !called.Text.StartsWith("atomic", StringComparison.Ordinal)))
                        throw Error($"Return value from '{called.Text}' must be used or explicitly discarded with '_ ='.", e.Value.Span);
                    // Non-void user calls and effectful builtins were already emitted as temporaries.
                    if (evaluation is Expression.Call call) block.Statements.Add(new Statement.Evaluate(call));
                    break;
                case SStatement.If i:
                    Expression condition = Materialize(Eval(i.Condition, block), ShaderType.Bool);
                    block.Statements.Add(new Statement.If(condition, Body(i.Accept), Body(i.Reject))); break;
                case SStatement.While w:
                    loopDepth++;
                    var whileBody = new Block();
                    var test = Materialize(Eval(w.Condition, whileBody), ShaderType.Bool);
                    var exit = new Block(); exit.Statements.Add(new Statement.Break());
                    whileBody.Statements.Add(new Statement.If(new Expression.Unary("!", test, ShaderType.Bool), exit, new()));
                    whileBody.Statements.Add(new Statement.Nested(Body(w.Body)));
                    block.Statements.Add(new Statement.Loop(whileBody, new())); loopDepth--; break;
                case SStatement.For f:
                    scopes.Push(new(StringComparer.Ordinal)); loopDepth++;
                    var forScope = new Block();
                    if (f.Initializer is not null) LowerStatement(f.Initializer, forScope);
                    var forBody = new Block(); var continuing = new Block();
                    if (f.Condition is not null)
                    {
                        Expression check = Materialize(Eval(f.Condition, forBody), ShaderType.Bool);
                        var forExit = new Block(); forExit.Statements.Add(new Statement.Break());
                        forBody.Statements.Add(new Statement.If(new Expression.Unary("!", check, ShaderType.Bool), forExit, new()));
                    }
                    forBody.Statements.Add(new Statement.Nested(Body(f.Body)));
                    if (f.Update is not null) LowerStatement(f.Update, continuing);
                    forScope.Statements.Add(new Statement.Loop(forBody, continuing));
                    block.Statements.Add(new Statement.Nested(forScope)); loopDepth--; scopes.Pop(); break;
                case SStatement.Loop l:
                    loopDepth++; scopes.Push(new(StringComparer.Ordinal));
                    Block loopBody = Body(l.Body, false), tail = new(); Expression? breakIf = null;
                    for (int index = 0; index < l.Continuing.Statements.Count; index++)
                    {
                        var statement = l.Continuing.Statements[index];
                        if (statement is SStatement.Break { Condition: not null } b && index == l.Continuing.Statements.Count - 1)
                            breakIf = Materialize(Eval(b.Condition, tail), ShaderType.Bool);
                        else LowerStatement(statement, tail);
                    }
                    block.Statements.Add(new Statement.Loop(loopBody, tail, breakIf)); scopes.Pop(); loopDepth--; break;
                case SStatement.Switch s:
                    Expression selector = Eval(s.Selector, block);
                    var selectors = s.Cases.Select(c => c.Values.Select(v => Eval(v, new())).ToArray()).ToArray();
                    ShaderType selectorType = selector.Type;
                    foreach (var candidate in selectors.SelectMany(v => v))
                        if (Implicit(selectorType, candidate.Type)) selectorType = candidate.Type;
                        else if (!Implicit(candidate.Type, selectorType)) throw Error("Switch case types are incompatible.");
                    selector = Materialize(selector, DefaultType(selectorType));
                    if (selector.Type is not ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint }) throw Error("Switch selector must be a concrete integer scalar.");
                    switchDepth++; var cases = new List<SwitchCase>();
                    for (int caseIndex = 0; caseIndex < s.Cases.Count; caseIndex++)
                    {
                        var c = s.Cases[caseIndex];
                        var literals = new List<Expression.Literal>();
                        foreach (var v in selectors[caseIndex])
                        {
                            var caseValue = Materialize(v, selector.Type);
                            if (!ConstantEvaluator.TryEvaluate(caseValue, out var constant) || constant is not Expression.Literal literal) throw Error("Switch cases must be constant integers.");
                            literals.Add(literal);
                        }
                        cases.Add(new(literals, c.IsDefault, Body(c.Body)));
                    }
                    switchDepth--; block.Statements.Add(new Statement.Switch(selector, cases)); break;
                case SStatement.Return r:
                    if (currentFunction is null) throw Error("Return outside a function.");
                    if (r.Value is null && currentFunction.ReturnType is not ShaderType.Void || r.Value is not null && currentFunction.ReturnType is ShaderType.Void) throw Error("Return does not match function result.");
                    block.Statements.Add(new Statement.Return(r.Value is null ? null : Materialize(Eval(r.Value, block), currentFunction.ReturnType))); break;
                case SStatement.Break b:
                    if (b.Condition is not null) throw Error("break if must be the final statement of a continuing block.");
                    if (loopDepth == 0 && switchDepth == 0) throw Error("break is outside a loop or switch.");
                    block.Statements.Add(new Statement.Break()); break;
                case SStatement.Continue:
                    if (loopDepth == 0) throw Error("continue is outside a loop.");
                    block.Statements.Add(new Statement.Continue()); break;
                case SStatement.Discard: block.Statements.Add(new Statement.Kill()); break;
                case SStatement.Assert a: Assert(a.Condition); break;
                default: throw Error("Unsupported WGSL statement.");
            }
        }
    }
}
