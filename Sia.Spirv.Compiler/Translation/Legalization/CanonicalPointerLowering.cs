using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Finite logical addresses carried by pointer block arguments become scalar
/// tags and captured index slots. Memory operations still execute only the selected address.</summary>
internal sealed class CanonicalPointerLowering
{
    private sealed record Part(string? Member, ShaderType? IndexType);
    private sealed record Shape(SsaValue Root, Part[] Parts, uint Tag, string Key);
    private sealed record Choice(Shape Shape, Expression Condition, Expression[] Indices);
    private readonly Dictionary<int, ControlFlowInstruction> definitions;
    private readonly Dictionary<int, Dictionary<string, Shape>> origins = [];
    private readonly Dictionary<string, Shape> shapes = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Expression.Reference> tags = [];
    private readonly Dictionary<(int Value, string Shape, int Part), Expression.Reference> indices = [];
    private readonly Func<SsaValue, Expression> use;
    private readonly string function;

    public CanonicalPointerLowering(ControlFlowFunction graph, Block declarations, Func<string> fresh, Func<SsaValue, Expression> use)
    {
        this.use = use; function = graph.Signature.Name;
        definitions = graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null).ToDictionary(i => i.Result!.Value.Id);
        if (!graph.Blocks.Any(b => b.Parameters.Any(p => p.Type is ShaderType.Pointer))) return;
        var blocks = graph.Blocks.ToDictionary(b => b.Id);
        foreach (var value in graph.Blocks.SelectMany(b => b.Parameters.Concat(b.Instructions.Where(i => i.Result is not null).Select(i => i.Result!.Value))))
            if (value.Type is ShaderType.Pointer) origins.Add(value.Id, new(StringComparer.Ordinal));
        Shape ShapeFor(SsaValue root, Part[] parts) {
            string key = root.Id + ":" + string.Join('/', parts.Select(p => p.Member is { } member ? "m:" + member : "i:" + p.IndexType));
            if (!shapes.TryGetValue(key, out var shape)) {
                shape = new(root, parts, checked((uint)shapes.Count + 1), key); shapes.Add(key, shape);
            }
            return shape;
        }
        bool changed;
        do {
            changed = false;
            foreach (var instruction in definitions.Values) {
                if (instruction.Result is not { Type: ShaderType.Pointer } result) continue;
                IEnumerable<Shape> incoming = instruction.Operation switch {
                    ValueOperation.Local or ValueOperation.Symbol => [ShapeFor(result, [])],
                    ValueOperation.Let let => origins[let.Value.Id].Values.ToArray(),
                    ValueOperation.Access access => origins[access.Base.Id].Values.Select(s => ShapeFor(s.Root, [.. s.Parts, new(null, access.Index.Type)])).ToArray(),
                    ValueOperation.Member member => origins[member.Base.Id].Values.Select(s => ShapeFor(s.Root, [.. s.Parts, new(member.Name, null)])).ToArray(),
                    _ => []
                };
                foreach (var shape in incoming) {
                    if (shape.Parts.Length > definitions.Count) throw Error("recursive address projection");
                    changed |= origins[result.Id].TryAdd(shape.Key, shape);
                }
            }
            foreach (var edge in graph.Blocks.SelectMany(b => b.Terminator!.Edges))
                for (int i = 0; i < edge.Arguments.Count; i++) {
                    var target = blocks[edge.Target].Parameters[i];
                    if (target.Type is not ShaderType.Pointer) continue;
                    foreach (var shape in origins[edge.Arguments[i].Id].Values.ToArray()) changed |= origins[target.Id].TryAdd(shape.Key, shape);
                }
        } while (changed);
        Expression.Reference Slot(ShaderType type) {
            string name = fresh(); declarations.Statements.Add(new Statement.Declare(name, type, null));
            return new(name, new ShaderType.Pointer(type, AddressSpace.Function));
        }
        foreach (var parameter in graph.Blocks.SelectMany(b => b.Parameters).Where(p => p.Type is ShaderType.Pointer)) {
            if (origins[parameter.Id].Count == 0) throw Error("pointer block argument has no finite address origin");
            tags.Add(parameter.Id, Slot(ShaderType.U32));
            foreach (var shape in origins[parameter.Id].Values)
                for (int i = 0; i < shape.Parts.Length; i++)
                    if (shape.Parts[i].IndexType is { } type) indices.Add((parameter.Id, shape.Key, i), Slot(type));
        }
    }
    private ShaderException Error(string message) => new(DiagnosticStage.Validation, "Canonical pointer target in " + function + ": " + message);
    public bool IsParameter(SsaValue value) => tags.ContainsKey(value.Id);
    private Choice[] Choices(SsaValue value)
    {
        if (tags.TryGetValue(value.Id, out var tag))
            return origins[value.Id].Values.Select(shape => new Choice(shape,
                new Expression.Binary("==", new Expression.Load(tag), Expression.U32(shape.Tag), ShaderType.Bool),
                shape.Parts.Select((part, i) => part.IndexType is null ? Expression.U32(0) : (Expression)new Expression.Load(indices[(value.Id, shape.Key, i)])).ToArray())).ToArray();
        var instruction = definitions[value.Id];
        switch (instruction.Operation) {
            case ValueOperation.Local or ValueOperation.Symbol:
                return [new(origins[value.Id].Values.Single(), Expression.Bool(true), [])];
            case ValueOperation.Let let: return Choices(let.Value);
            case ValueOperation.Access access:
                return Choices(access.Base).Select(c => new Choice(origins[value.Id].Values.Single(s => s.Root == c.Shape.Root
                    && s.Parts.SequenceEqual(c.Shape.Parts.Append(new Part(null, access.Index.Type)))), c.Condition, [.. c.Indices, use(access.Index)])).ToArray();
            case ValueOperation.Member member:
                return Choices(member.Base).Select(c => new Choice(origins[value.Id].Values.Single(s => s.Root == c.Shape.Root
                    && s.Parts.SequenceEqual(c.Shape.Parts.Append(new Part(member.Name, null)))), c.Condition, [.. c.Indices, Expression.U32(0)])).ToArray();
            default: throw Error("unsupported address producer " + instruction.Operation.GetType().Name);
        }
    }
    private static Expression Select(Choice[] choices, Func<Choice, Expression> value)
    {
        Expression result = value(choices[^1]);
        foreach (var choice in choices.Reverse().Skip(1)) result = new Expression.Select(choice.Condition, value(choice), result);
        return result;
    }
    public Expression Address(SsaValue value)
    {
        var choices = Choices(value);
        Expression Leaf(Choice choice) {
            Expression root = use(choice.Shape.Root);
            for (int i = 0; i < choice.Shape.Parts.Length; i++) {
                var pointer = (ShaderType.Pointer)root.Type; var part = choice.Shape.Parts[i];
                if (part.Member is { } member) {
                    var type = ((ShaderType.Structure)pointer.Base).Members.Single(m => m.Name == member).Type;
                    root = new Expression.Member(root, member, pointer with { Base = type });
                }
                else {
                    ShaderType type = pointer.Base switch {
                        ShaderType.Array a => a.Element, ShaderType.Vector v => v.Component,
                        ShaderType.Matrix m => new ShaderType.Vector(m.Rows, m.Component), ShaderType.BindingArray b => b.Element,
                        _ => throw Error("invalid address index projection")
                    };
                    root = new Expression.Access(root, choice.Indices[i], pointer with { Base = type });
                }
            }
            return root;
        }
        return Select(choices, Leaf);
    }
    public IEnumerable<(Expression.Reference Target, Expression Value)> EdgeValues(SsaValue argument, SsaValue parameter)
    {
        var choices = Choices(argument);
        yield return (tags[parameter.Id], Select(choices, c => Expression.U32(c.Shape.Tag)));
        foreach (var shape in origins[parameter.Id].Values)
            for (int i = 0; i < shape.Parts.Length; i++) {
                if (shape.Parts[i].IndexType is not { } type) continue;
                int part = i;
                yield return (indices[(parameter.Id, shape.Key, i)], Select(choices,
                    c => c.Shape.Key == shape.Key ? c.Indices[part] : new Expression.Construct(type, [])));
            }
    }
}
