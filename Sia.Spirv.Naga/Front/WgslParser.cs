namespace Sia.Spirv.Naga.Front;

internal sealed class WgslParser(string source)
{
    private readonly List<Token> tokens = WgslLexer.Tokenize(source);
    private int cursor, nesting;
    private Token Current => tokens[cursor];
    private ShaderException Error(string message) => new(DiagnosticStage.WgslParse, message, Current.Span);
    private Token Take() { var token = Current; if (token.Kind != TokenKind.End) cursor++; return token; }
    private bool Eat(string text) { if (Current.Text != text) return false; cursor++; return true; }
    private void Expect(string text) { if (!Eat(text)) throw Error($"Expected '{text}', found '{Current.Text}'."); }
    private string Identifier(bool attribute = false)
    {
        if (Current.Kind != TokenKind.Word || Current.Text == "_" || Current.Text.StartsWith("__", StringComparison.Ordinal)) throw Error("Expected identifier.");
        if (!attribute && WgslKeywords.IsReserved(Current.Text)) throw Error($"Reserved keyword '{Current.Text}' cannot be an identifier.");
        return Take().Text;
    }
    private void Enter() { if (++nesting > 256) throw Error("WGSL nesting exceeds 256 levels."); }
    private void Leave() => nesting--;

    public SModule Parse()
    {
        var module = new SModule();
        bool directives = true;
        module.Identifiers.UnionWith(tokens.Where(t => t.Kind == TokenKind.Word).Select(t => t.Text));
        while (Current.Kind != TokenKind.End)
        {
            if (Eat(";")) { directives = false; continue; }
            if (Eat("enable"))
            {
                if (!directives) throw Error("Directives must precede global declarations.");
                do
                {
                    string name = Identifier();
                    if (!WgslExtensions.Enable(name)) throw Error($"Unknown or unimplemented enable extension '{name}'.");
                    module.Enables.Add(name);
                } while (Eat(",") && Current.Text != ";");
                Expect(";"); continue;
            }
            if (Eat("requires"))
            {
                if (!directives) throw Error("Directives must precede global declarations.");
                do
                {
                    string name = Identifier();
                    if (!WgslExtensions.Requirement(name)) throw Error($"Unknown or unimplemented language requirement '{name}'.");
                } while (Eat(",") && Current.Text != ";");
                Expect(";"); continue;
            }
            if (Eat("diagnostic"))
            {
                if (!directives) throw Error("Directives must precede global declarations.");
                var filter = DiagnosticFilter(); Expect(";");
                var previous = module.DiagnosticFilters.FirstOrDefault(f => f.Rule == filter.Rule && f.Namespace == filter.Namespace);
                if (previous is not null && previous.Severity != filter.Severity) throw Error("Conflicting diagnostic directives for the same rule.");
                if (previous is null) module.DiagnosticFilters.Add(filter);
                continue;
            }
            directives = false;
            if (Eat("const_assert")) { module.Assertions.Add(Expression()); Expect(";"); continue; }
            var attributes = Attributes();
            if (Eat("struct"))
            {
                if (attributes.Count != 0) throw Error("Attributes cannot be applied to structures.");
                string name = Identifier(); Expect("{"); var members = new List<SMember>();
                while (!Eat("}"))
                {
                    var memberAttributes = Attributes(); string memberName = Identifier(); Expect(":");
                    members.Add(new(memberName, Name(), memberAttributes));
                    if (!Eat(",")) { Expect("}"); break; }
                }
                module.Structures.Add(new(name, members)); continue;
            }
            if (Eat("alias"))
            {
                if (attributes.Count != 0) throw Error("Attributes cannot be applied to aliases.");
                string name = Identifier(); Expect("=");
                if (!module.Aliases.TryAdd(name, Name())) throw Error("Duplicate alias name.");
                Expect(";"); continue;
            }
            if (Eat("fn"))
            {
                string name = Identifier(); Expect("("); var arguments = new List<SArgument>();
                while (!Eat(")"))
                {
                    var argumentAttributes = Attributes(); string argument = Identifier(); Expect(":");
                    arguments.Add(new(argument, Name(), argumentAttributes));
                    if (!Eat(",")) { Expect(")"); break; }
                }
                SExpression.Name? result = null; IReadOnlyList<SAttribute> resultAttributes = [];
                if (Eat("->")) { resultAttributes = Attributes(); result = Name(); }
                module.Functions.Add(new(name, arguments, result, attributes, resultAttributes, Block())); continue;
            }
            if (Current.Text is "var" or "const" or "override") { module.Declarations.Add(Declaration(attributes)); Expect(";"); continue; }
            throw Error("Expected a WGSL module declaration.");
        }
        return module;
    }

    private List<SAttribute> Attributes()
    {
        var attributes = new List<SAttribute>();
        while (Eat("@"))
        {
            SourceSpan span = tokens[cursor - 1].Span; string name = Identifier(attribute: true);
            if (name == "diagnostic")
            {
                var filter = DiagnosticFilter();
                if (attributes.Any(a => a.DiagnosticFilter is { } f && f.Rule == filter.Rule && f.Namespace == filter.Namespace))
                    throw Error("Duplicate diagnostic attribute for the same rule.");
                attributes.Add(new(name, [], span) { DiagnosticFilter = filter });
                continue;
            }
            var arguments = new List<SExpression>();
            if (Eat("("))
            {
                while (!Eat(")"))
                {
                    arguments.Add(Expression());
                    if (!Eat(",")) { Expect(")"); break; }
                }
            }
            attributes.Add(new(name, arguments, span));
        }
        return attributes;
    }

    private IR.DiagnosticFilter DiagnosticFilter()
    {
        Expect("(");
        string severity = Identifier();
        IR.DiagnosticSeverity level = severity switch
        {
            "off" => IR.DiagnosticSeverity.Off, "info" => IR.DiagnosticSeverity.Info,
            "warning" => IR.DiagnosticSeverity.Warning, "error" => IR.DiagnosticSeverity.Error,
            _ => throw Error("Expected diagnostic severity off, info, warning or error.")
        };
        Expect(","); string rule = Identifier(); string? ns = null;
        if (Eat(".")) { ns = rule; rule = Identifier(); }
        Eat(","); Expect(")");
        return new(level, rule, ns);
    }

    private SDeclaration Declaration(IReadOnlyList<SAttribute> attributes)
    {
        var start = Take(); var qualifiers = new List<SExpression>();
        if (Current.Kind == TokenKind.TemplateStart)
        {
            Take();
            while (Current.Kind != TokenKind.TemplateEnd)
            {
                qualifiers.Add(Expression()); if (!Eat(",")) break;
            }
            if (Current.Kind != TokenKind.TemplateEnd) throw Error("Expected end of declaration qualifier list.");
            Take();
        }
        string name = Identifier(); SExpression.Name? type = Eat(":") ? Name() : null;
        SExpression? value = Eat("=") ? Expression() : null;
        if (start.Text is "let" or "const" && value is null) throw Error("Immutable declaration needs an initializer.");
        if (type is null && value is null) throw Error("Declaration needs a type or initializer.");
        return new(start.Text, name, type, value, attributes, qualifiers, start.Span);
    }

    private SExpression.Name Name()
    {
        SourceSpan span = Current.Span; string name = Identifier();
        var arguments = new List<SExpression>();
        if (Current.Kind == TokenKind.TemplateStart)
        {
            Take();
            while (Current.Kind != TokenKind.TemplateEnd)
            {
                arguments.Add(Expression()); if (!Eat(",")) break;
            }
            if (Current.Kind != TokenKind.TemplateEnd) throw Error("Expected end of template argument list.");
            Take();
        }
        return new(name, arguments, span);
    }

    private SBlock Block()
    {
        Enter(); Expect("{"); var block = new SBlock();
        while (!Eat("}"))
        {
            if (Current.Kind == TokenKind.End) throw Error("Unterminated statement block.");
            if (Eat(";")) continue;
            block.Statements.Add(Statement());
        }
        Leave(); return block;
    }

    private SStatement Statement()
    {
        Enter(); SStatement result;
        if (Current.Text == "{") result = new SStatement.Nested(Block());
        else if (Current.Text is "var" or "let" or "const") { result = new SStatement.Declaration(Declaration([])); Expect(";"); }
        else if (Eat("return")) { result = new SStatement.Return(Current.Text == ";" ? null : Expression()); Expect(";"); }
        else if (Eat("break")) { result = new SStatement.Break(Eat("if") ? Expression() : null); Expect(";"); }
        else if (Eat("continue")) { result = new SStatement.Continue(); Expect(";"); }
        else if (Eat("discard")) { result = new SStatement.Discard(); Expect(";"); }
        else if (Eat("const_assert")) { result = new SStatement.Assert(Expression()); Expect(";"); }
        else if (Eat("if"))
        {
            SExpression condition = Expression(); var accept = Block(); var reject = new SBlock();
            if (Eat("else"))
            {
                if (Current.Text == "if") reject.Statements.Add(Statement());
                else reject = Block();
            }
            result = new SStatement.If(condition, accept, reject);
        }
        else if (Eat("while")) { SExpression condition = Expression(); result = new SStatement.While(condition, Block()); }
        else if (Eat("for"))
        {
            Expect("("); SStatement? init = null, update = null;
            if (Current.Text != ";") init = Current.Text is "var" or "let" or "const" ? new SStatement.Declaration(Declaration([])) : SimpleStatement();
            Expect(";"); SExpression? condition = Current.Text == ";" ? null : Expression(); Expect(";");
            if (Current.Text != ")") update = SimpleStatement();
            Expect(")"); result = new SStatement.For(init, condition, update, Block());
        }
        else if (Eat("loop"))
        {
            Expect("{"); var body = new SBlock(); var continuing = new SBlock();
            while (!Eat("}"))
            {
                if (Eat("continuing")) { continuing = Block(); Expect("}"); break; }
                if (Eat(";")) continue;
                body.Statements.Add(Statement());
            }
            result = new SStatement.Loop(body, continuing);
        }
        else if (Eat("switch"))
        {
            SExpression selector = Expression(); Expect("{"); var cases = new List<SCase>();
            while (!Eat("}"))
            {
                bool isDefault = Eat("default"); var values = new List<SExpression>();
                if (!isDefault)
                {
                    Expect("case");
                    do
                    {
                        if (Eat("default")) isDefault = true;
                        else values.Add(Expression());
                    } while (Eat(",") && Current.Text is not (":" or "{"));
                }
                Eat(":"); cases.Add(new(values, isDefault, Block()));
            }
            result = new SStatement.Switch(selector, cases);
        }
        else { result = SimpleStatement(); Expect(";"); }
        Leave(); return result;
    }

    private SStatement SimpleStatement()
    {
        SExpression target = Expression();
        string op = Current.Text;
        if (op is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=" or "<<=" or ">>=")
        {
            Take(); return new SStatement.Assignment(op, target, Expression());
        }
        if (op is "++" or "--") { Take(); return new SStatement.Assignment(op, target, null); }
        if (target is not SExpression.Call) throw Error("Only a call can be used as an expression statement.");
        return new SStatement.Evaluate(target);
    }

    private SExpression Expression(int minimum = 0)
    {
        Enter(); SExpression left;
        var start = Current;
        if (Current.Text is "!" or "~" or "-" or "&" or "*") { Take(); left = new SExpression.Unary(start.Text, Expression(11), start.Span); }
        else if (Eat("(")) { left = Expression(); Expect(")"); }
        else if (Current.Kind == TokenKind.Number || Current.Text is "true" or "false") { Take(); left = new SExpression.Literal(start.Text, start.Span); }
        else if (Current.Text == "_") { Take(); left = new SExpression.Name("_", [], start.Span); }
        else if (Current.Kind == TokenKind.Word) left = Name();
        else throw Error("Expected expression.");
        while (true)
        {
            if (Eat("("))
            {
                var args = new List<SExpression>();
                while (!Eat(")")) { args.Add(Expression()); if (!Eat(",")) { Expect(")"); break; } }
                left = new SExpression.Call(left, args, start.Span); continue;
            }
            if (Eat("[")) { var subscript = Expression(); Expect("]"); left = new SExpression.Index(left, subscript, start.Span); continue; }
            if (Eat(".")) { left = new SExpression.Member(left, Identifier(), start.Span); continue; }
            int precedence = Current.Kind is TokenKind.TemplateStart or TokenKind.TemplateEnd ? -1 : Precedence(Current.Text);
            if (precedence < minimum) break;
            string op = Take().Text; left = new SExpression.Binary(op, left, Expression(precedence + 1), start.Span);
        }
        Leave(); return left;
    }

    private static int Precedence(string op) => op switch
    {
        "||" => 0, "&&" => 1, "|" => 2, "^" => 3, "&" => 4, "==" or "!=" => 5,
        "<" or ">" or "<=" or ">=" => 6, "<<" or ">>" => 7, "+" or "-" => 8, "*" or "/" or "%" => 9, _ => -1
    };
}
