namespace Sia.Spirv.Naga.Front;

internal sealed record SAttribute(string Name, IReadOnlyList<SExpression> Arguments, SourceSpan Span)
{
    public IR.DiagnosticFilter? DiagnosticFilter { get; init; }
}
internal sealed record SDeclaration(string Kind, string Name, SExpression.Name? Type, SExpression? Value,
    IReadOnlyList<SAttribute> Attributes, IReadOnlyList<SExpression> Qualifiers, SourceSpan Span);
internal sealed record SMember(string Name, SExpression.Name Type, IReadOnlyList<SAttribute> Attributes);
internal sealed record SStructure(string Name, IReadOnlyList<SMember> Members);
internal sealed record SArgument(string Name, SExpression.Name Type, IReadOnlyList<SAttribute> Attributes);
internal sealed record SFunction(string Name, IReadOnlyList<SArgument> Arguments, SExpression.Name? Result,
    IReadOnlyList<SAttribute> Attributes, IReadOnlyList<SAttribute> ResultAttributes, SBlock Body);
internal sealed class SModule
{
    public List<SStructure> Structures { get; } = [];
    public List<SDeclaration> Declarations { get; } = [];
    public List<SFunction> Functions { get; } = [];
    public Dictionary<string, SExpression.Name> Aliases { get; } = new(StringComparer.Ordinal);
    public List<SExpression> Assertions { get; } = [];
    public HashSet<string> Enables { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Identifiers { get; } = new(StringComparer.Ordinal);
    public List<IR.DiagnosticFilter> DiagnosticFilters { get; } = [];
}
internal abstract record SExpression(SourceSpan Span)
{
    public sealed record Name(string Text, IReadOnlyList<SExpression> Templates, SourceSpan Source) : SExpression(Source);
    public sealed record Literal(string Text, SourceSpan Source) : SExpression(Source);
    public sealed record Unary(string Operator, SExpression Operand, SourceSpan Source) : SExpression(Source);
    public sealed record Binary(string Operator, SExpression Left, SExpression Right, SourceSpan Source) : SExpression(Source);
    public sealed record Call(SExpression Target, IReadOnlyList<SExpression> Arguments, SourceSpan Source) : SExpression(Source);
    public sealed record Index(SExpression Base, SExpression Subscript, SourceSpan Source) : SExpression(Source);
    public sealed record Member(SExpression Base, string Field, SourceSpan Source) : SExpression(Source);
}
internal sealed class SBlock
{
    public List<SStatement> Statements { get; } = [];
}
internal abstract record SStatement
{
    public sealed record Nested(SBlock Body) : SStatement;
    public sealed record Declaration(SDeclaration Value) : SStatement;
    public sealed record Assignment(string Operator, SExpression Target, SExpression? Value) : SStatement;
    public sealed record Evaluate(SExpression Value) : SStatement;
    public sealed record If(SExpression Condition, SBlock Accept, SBlock Reject) : SStatement;
    public sealed record Loop(SBlock Body, SBlock Continuing) : SStatement;
    public sealed record While(SExpression Condition, SBlock Body) : SStatement;
    public sealed record For(SStatement? Initializer, SExpression? Condition, SStatement? Update, SBlock Body) : SStatement;
    public sealed record Switch(SExpression Selector, IReadOnlyList<SCase> Cases) : SStatement;
    public sealed record Return(SExpression? Value) : SStatement;
    public sealed record Break(SExpression? Condition = null) : SStatement;
    public sealed record Continue : SStatement;
    public sealed record Discard : SStatement;
    public sealed record Assert(SExpression Condition) : SStatement;
}
internal sealed record SCase(IReadOnlyList<SExpression> Values, bool IsDefault, SBlock Body);
