using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Front;

public static partial class SpirvReader
{
    private sealed partial class Reader
    {
        private sealed record MatrixLayout(uint Stride, bool RowMajor);
        private readonly Dictionary<StructMember, MatrixLayout> matrixLayouts = new(ReferenceEqualityComparer.Instance);

        // Native matrix layout belongs to a structure member, not its value type.
        // Consume it before exposing the IR: memory uses strided vectors, while
        // computations keep ordinary column-major matrix values.
        private sealed class MatrixLayoutLowering(Module input, IReadOnlyDictionary<StructMember, MatrixLayout> layouts)
        {
            private sealed record Node(ShaderType Original, ShaderType Logical, ShaderType Physical,
                MatrixLayout? Matrix = null, IReadOnlyList<Node>? Members = null, Node? Element = null);
            private sealed record Projection(Expression Rows, ShaderType.Matrix Matrix, Expression Column);
            private sealed record Location(Expression Pointer, Node Node, Projection? Projection = null);
            private readonly Module output = new() { VulkanMemoryModel = input.VulkanMemoryModel, WorkgroupInitializationRequired = input.WorkgroupInitializationRequired };
            private readonly Dictionary<ShaderType, ShaderType> logicalTypes = [];
            private readonly Dictionary<ShaderType, Node> nodes = [];
            private readonly HashSet<ShaderType.Array> layoutArrays = [];
            private readonly HashSet<string> names = new(StringComparer.Ordinal);
            private Dictionary<string, Location> locations = new(StringComparer.Ordinal);
            private int nextName;
            private ShaderException Error(string message) => new(DiagnosticStage.SpirvParse, message);
            private string Name(string prefix)
            {
                string name;
                do name = prefix + nextName++; while (!names.Add(name));
                return name;
            }

            public Module Run()
            {
                foreach (var s in input.Structures) names.Add(s.Name);
                foreach (var g in input.Globals) names.Add(g.Name);
                foreach (var c in input.Constants) names.Add(c.Name);
                foreach (var f in input.Functions) { names.Add(f.Name); foreach (var a in f.Arguments) names.Add(a.Name); Reserve(f.Body); }
                foreach (var member in layouts.Keys)
                    for (ShaderType type = member.Type; type is ShaderType.Array array; type = array.Element) layoutArrays.Add(array);
                foreach (var s in input.Structures) Logical(s);
                output.Enables.UnionWith(input.Enables); output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
                foreach (var g in input.Globals)
                {
                    var node = Memory(g.Type);
                    output.Globals.Add(g with { Type = node.Physical, Initializer = g.Initializer is null ? null : PhysicalValue(node, Expr(g.Initializer)) });
                    locations.Add(g.Name, new(new Expression.Reference(g.Name, new ShaderType.Pointer(node.Physical, g.Space, g.Access)), node));
                }
                foreach (var c in input.Constants) output.Constants.Add(c with { Type = Logical(c.Type), Value = c.Value is null ? null : Expr(c.Value) });
                var globals = locations;
                foreach (var f in input.Functions)
                {
                    locations = new(globals, StringComparer.Ordinal);
                    var function = new ShaderFunction(f.Name)
                    {
                        ReturnType = Logical(f.ReturnType), ReturnBinding = f.ReturnBinding, Stage = f.Stage,
                        WorkgroupSize = f.WorkgroupSize.Select(Expr).ToArray(), EarlyDepthTest = f.EarlyDepthTest,
                        ConservativeDepth = f.ConservativeDepth, TaskPayload = f.TaskPayload, MeshOutput = f.MeshOutput
                    };
                    foreach (var a in f.Arguments)
                    {
                        ShaderType type = Logical(a.Type);
                        if (a.Type is ShaderType.Pointer pointer)
                        {
                            var node = Memory(pointer.Space == AddressSpace.Function ? Logical(pointer.Base) : pointer.Base); type = pointer with { Base = node.Physical };
                            locations[a.Name] = new(new Expression.Reference(a.Name, type), node);
                        }
                        function.Arguments.Add(a with { Type = type });
                    }
                    function.DiagnosticFilters.AddRange(f.DiagnosticFilters);
                    function.Body = Body(f.Body); output.Functions.Add(function);
                }
                return output;
            }

            private void Reserve(Block block)
            {
                foreach (var s in block.Statements) switch (s)
                {
                    case Statement.Declare d: names.Add(d.Name); break;
                    case Statement.Nested n: Reserve(n.Body); break;
                    case Statement.If i: Reserve(i.Accept); Reserve(i.Reject); break;
                    case Statement.Loop l: Reserve(l.Body); Reserve(l.Continuing); break;
                    case Statement.Switch selection: foreach (var c in selection.Cases) Reserve(c.Body); break;
                }
            }

            private bool HasLayout(ShaderType type) => type switch
            {
                ShaderType.Array a => layoutArrays.Contains(a) || HasLayout(a.Element),
                ShaderType.Structure s => s.Members.Any(m => layouts.ContainsKey(m) || HasLayout(m.Type)),
                _ => false
            };
            private ShaderType Logical(ShaderType type)
            {
                if (logicalTypes.TryGetValue(type, out var result)) return result;
                result = type switch
                {
                    ShaderType.Structure s when HasLayout(s) => s with { Members = s.Members.Select(m => m with
                        { Type = Logical(m.Type), Offset = null, Alignment = null, Size = null, MemoryDecorations = MemoryDecorations.None }).ToArray() },
                    ShaderType.Array a when HasLayout(a) => a with { Element = Logical(a.Element), Stride = null },
                    ShaderType.BindingArray a => a with { Element = Logical(a.Element) },
                    ShaderType.Pointer p => p with { Base = Logical(p.Base) },
                    _ => type
                };
                logicalTypes.Add(type, result);
                if (result != type) logicalTypes.TryAdd(result, result);
                if (result is ShaderType.Structure structure) output.Structures.Add(structure);
                return result;
            }

            private Node Memory(ShaderType type, MatrixLayout? matrix = null)
            {
                if (matrix is null && nodes.TryGetValue(type, out var cached)) return cached;
                ShaderType physical = type; IReadOnlyList<Node>? members = null; Node? element = null;
                if (type is ShaderType.Matrix m && matrix is not null)
                {
                    var vector = new ShaderType.Vector(matrix.RowMajor ? m.Columns : m.Rows, m.Component);
                    var layout = TypeLayout.Of(vector);
                    if (matrix.Stride < layout.Size || matrix.Stride % layout.Alignment != 0)
                        throw Error("Native matrix stride is incompatible with its vector layout.");
                    physical = new ShaderType.Array(vector, (uint)(matrix.RowMajor ? m.Rows : m.Columns), matrix.Stride);
                }
                else if (type is ShaderType.Array a)
                {
                    element = Memory(a.Element, matrix); physical = a with { Element = element.Physical };
                }
                else if (type is ShaderType.BindingArray b)
                {
                    element = Memory(b.Element); physical = b with { Element = element.Physical };
                }
                else if (type is ShaderType.Structure s)
                {
                    members = s.Members.Select(m => Memory(m.Type, layouts.GetValueOrDefault(m))).ToArray();
                    if (members.Where((n, i) => n.Physical != s.Members[i].Type).Any())
                    {
                        physical = s with { Name = Name("SiaMatrixMemory"), Members = s.Members.Select((m, i) => m with { Type = members[i].Physical }).ToArray() };
                        output.Structures.Add((ShaderType.Structure)physical);
                    }
                }
                var node = new Node(type, Logical(type), physical, type is ShaderType.Matrix ? matrix : null, members, element);
                if (matrix is null) nodes.Add(type, node);
                return node;
            }

            private static Expression Child(Expression pointer, ShaderType type, string member)
            {
                var p = (ShaderType.Pointer)pointer.Type;
                return new Expression.Member(pointer, member, p with { Base = type });
            }

            // Keep module initialization a constant expression. Reading from a
            // temporary variable here would change initialization order and
            // cannot represent specialization constants.
            private Expression PhysicalValue(Node node, Expression value)
            {
                if (node.Physical == node.Logical) return value;
                if (value is Expression.Construct { Components.Count: 0 }) return new Expression.Construct(node.Physical, []);
                if (node.Matrix is { } layout && node.Original is ShaderType.Matrix matrix)
                {
                    var column = new ShaderType.Vector(matrix.Rows, matrix.Component);
                    Expression Column(int c) => new Expression.Access(value, Expression.U32((uint)c), column);
                    Expression[] vectors = layout.RowMajor
                        ? Enumerable.Range(0, matrix.Rows).Select(r => (Expression)new Expression.Construct(
                            new ShaderType.Vector(matrix.Columns, matrix.Component), Enumerable.Range(0, matrix.Columns)
                                .Select(c => (Expression)new Expression.Access(Column(c), Expression.U32((uint)r), matrix.Component)).ToArray())).ToArray()
                        : Enumerable.Range(0, matrix.Columns).Select(Column).ToArray();
                    return new Expression.Construct(node.Physical, vectors);
                }
                if (node.Original is ShaderType.Structure structure && node.Members is { } members)
                    return new Expression.Construct(node.Physical, structure.Members.Select((m, i) =>
                        PhysicalValue(members[i], new Expression.Member(value, m.Name, members[i].Logical))).ToArray());
                if (node.Original is ShaderType.Array { Length: uint length } && node.Element is { } element)
                    return new Expression.Construct(node.Physical, Enumerable.Range(0, checked((int)length)).Select(i =>
                        PhysicalValue(element, new Expression.Access(value, Expression.U32((uint)i), element.Logical))).ToArray());
                throw Error("Unresolved array layout in a module initializer requires constant physical reconstruction.");
            }
            private static Expression Child(Expression pointer, ShaderType type, Expression index)
            {
                var p = (ShaderType.Pointer)pointer.Type;
                return new Expression.Access(pointer, index, p with { Base = type });
            }
            private Location Member(Location parent, string name)
            {
                if (parent.Node.Original is not ShaderType.Structure s || parent.Node.Members is null) throw Error("Invalid matrix-layout structure access.");
                int index = s.Members.ToList().FindIndex(m => m.Name == name);
                if (index < 0) throw Error("Unknown matrix-layout structure member.");
                var child = parent.Node.Members[index];
                return new(Child(parent.Pointer, child.Physical, name), child);
            }
            private Location Index(Location parent, Expression index)
            {
                if (parent.Projection is { } projection)
                {
                    var row = new ShaderType.Vector(projection.Matrix.Columns, projection.Matrix.Component);
                    var pointer = Child(Child(projection.Rows, row, index), row.Component, projection.Column);
                    return new(pointer, Memory(row.Component));
                }
                if (parent.Node.Matrix is { } layout && parent.Node.Original is ShaderType.Matrix matrix)
                {
                    var column = new ShaderType.Vector(matrix.Rows, matrix.Component);
                    if (layout.RowMajor) return new(parent.Pointer, Memory(column), new(parent.Pointer, matrix, index));
                    return new(Child(parent.Pointer, column, index), Memory(column));
                }
                if (parent.Node.Element is { } element) return new(Child(parent.Pointer, element.Physical, index), element);
                ShaderType child = parent.Node.Original switch
                {
                    ShaderType.Matrix m => new ShaderType.Vector(m.Rows, m.Component), ShaderType.Vector v => v.Component,
                    _ => throw Error("Invalid matrix-layout index access.")
                };
                return new(Child(parent.Pointer, child, index), Memory(child));
            }
            private Location Place(Expression expression) => expression switch
            {
                Expression.Reference r when locations.TryGetValue(r.Name, out var location) => location,
                Expression.Reference { Type: ShaderType.Pointer p } r => new(new Expression.Reference(r.Name, p with { Base = Logical(p.Base) }), Memory(p.Base)),
                Expression.Member m => Member(Place(m.Base), m.Name),
                Expression.Access a => Index(Place(a.Base), Expr(a.Index)),
                Expression.Unary { Operator: "&" or "*" } u => Place(u.Operand),
                _ => throw Error("Unsupported matrix-layout pointer expression.")
            };

            private Expression Read(Location place, SpirvMemoryAccess? memory)
            {
                if (place.Projection is { } p)
                    return new Expression.Construct(place.Node.Logical, Enumerable.Range(0, p.Matrix.Rows)
                        .Select(r => Read(Index(place, Expression.U32((uint)r)), memory?.Leaf())).ToArray());
                if (place.Node.Matrix is not null && place.Node.Original is ShaderType.Matrix matrix)
                    return new Expression.Construct(matrix, Enumerable.Range(0, matrix.Columns)
                        .Select(c => Read(Index(place, Expression.U32((uint)c)), memory?.Leaf())).ToArray());
                if (place.Node.Physical == place.Node.Logical) return new Expression.Load(place.Pointer) { MemoryAccess = memory };
                if (place.Node.Original is ShaderType.Structure structure)
                    return new Expression.Construct(place.Node.Logical, structure.Members.Select(m => Read(Member(place, m.Name), memory?.Leaf())).ToArray());
                if (place.Node.Original is ShaderType.Array { Length: uint length })
                    return new Expression.Construct(place.Node.Logical, Enumerable.Range(0, checked((int)length)).Select(i => Read(Index(place, Expression.U32((uint)i)), memory?.Leaf())).ToArray());
                throw Error("Whole explicit matrix-layout array access requires a resolved finite length.");
            }

            private void Write(Block block, Location place, Expression value, SpirvMemoryAccess? memory)
            {
                if (place.Projection is { } p)
                {
                    for (int r = 0; r < p.Matrix.Rows; r++) Write(block, Index(place, Expression.U32((uint)r)), new Expression.Access(value, Expression.U32((uint)r), p.Matrix.Component), memory?.Leaf());
                    return;
                }
                if (place.Node.Matrix is not null && place.Node.Original is ShaderType.Matrix matrix)
                {
                    for (int c = 0; c < matrix.Columns; c++) Write(block, Index(place, Expression.U32((uint)c)), new Expression.Access(value, Expression.U32((uint)c), new ShaderType.Vector(matrix.Rows, matrix.Component)), memory?.Leaf());
                    return;
                }
                if (place.Node.Physical == place.Node.Logical) { block.Statements.Add(new Statement.Store(place.Pointer, value) { MemoryAccess = memory }); return; }
                if (place.Node.Original is ShaderType.Structure structure)
                {
                    foreach (var m in structure.Members) Write(block, Member(place, m.Name), new Expression.Member(value, m.Name, Logical(m.Type)), memory?.Leaf());
                    return;
                }
                if (place.Node.Original is ShaderType.Array { Length: uint length } array)
                {
                    for (uint i = 0; i < length; i++) Write(block, Index(place, Expression.U32(i)), new Expression.Access(value, Expression.U32(i), Logical(array.Element)), memory?.Leaf());
                    return;
                }
                throw Error("Whole explicit matrix-layout array access requires a resolved finite length.");
            }

            private Expression Pointer(Expression input)
            {
                var location = Place(input);
                if (location.Projection is not null || location.Node.Physical != Memory(location.Node.Original).Physical)
                    throw Error("Explicit matrix-layout pointer escapes require helper specialization.");
                return input is Expression.Unary { Operator: "&" } ? new Expression.Unary("&", location.Pointer, location.Pointer.Type) : location.Pointer;
            }
            private Expression Expr(Expression expression)
            {
                if (expression.Type is ShaderType.Pointer) return Pointer(expression);
                ShaderType type = Logical(expression.Type);
                return expression switch
                {
                    Expression.Load l => Read(Place(l.Pointer), l.MemoryAccess),
                    Expression.Reference r when locations.TryGetValue(r.Name, out var location) && location.Node.Physical != location.Node.Logical => Read(location, null),
                    Expression.Reference r => new Expression.Reference(r.Name, type) { Span = r.Span },
                    Expression.Literal l => new Expression.Literal(l.Value, type) { Span = l.Span },
                    Expression.Construct c => new Expression.Construct(type, c.Components.Select(Expr).ToArray()) { Span = c.Span },
                    Expression.Convert c => new Expression.Convert(type, Expr(c.Operand), c.Bitcast) { Span = c.Span },
                    Expression.Unary u => new Expression.Unary(u.Operator, Expr(u.Operand), type) { Span = u.Span },
                    Expression.Binary b => new Expression.Binary(b.Operator, Expr(b.Left), Expr(b.Right), type) { Span = b.Span },
                    Expression.Member m => new Expression.Member(Expr(m.Base), m.Name, type) { Span = m.Span },
                    Expression.Access a => new Expression.Access(Expr(a.Base), Expr(a.Index), type) { Span = a.Span },
                    Expression.Swizzle s => new Expression.Swizzle(Expr(s.Vector), s.Components, type) { Span = s.Span },
                    Expression.Select s => new Expression.Select(Expr(s.Condition), Expr(s.Accept), Expr(s.Reject)) { Span = s.Span },
                    Expression.Call c => new Expression.Call(c.Function, c.Arguments.Select(Expr).ToArray(), type) { Binding = c.Binding, Span = c.Span, AtomicMemory = c.AtomicMemory, MemoryAccess = c.MemoryAccess },
                    _ => throw Error("Unsupported explicit matrix-layout expression.")
                };
            }

            private void Store(Block block, Expression target, Expression value, SpirvMemoryAccess? memory)
            {
                var place = Place(target); value = Expr(value);
                if (place.Projection is not null || place.Node.Physical != place.Node.Logical)
                {
                    // A split write must snapshot all reads before any store,
                    // including an overlapping matrix/structure source.
                    string name = Name("sia_matrix_value");
                    block.Statements.Add(new Statement.Declare(name, place.Node.Logical, value, false));
                    value = new Expression.Reference(name, place.Node.Logical);
                }
                Write(block, place, value, memory);
            }
            private Block Body(Block source)
            {
                var saved = locations; locations = new(saved, StringComparer.Ordinal); var block = new Block();
                foreach (var statement in source.Statements) switch (statement)
                {
                    case Statement.Declare d:
                        if (d.Type is ShaderType.Pointer && d.Initializer is not null) { locations[d.Name] = Place(d.Initializer); break; }
                        if (d.Mutable)
                        {
                            // Function locals, including native SSA registers,
                            // hold logical values. Their byte layout is not a
                            // host ABI; do not carry buffer member qualifiers
                            // into newly introduced snapshots.
                            var node = Memory(Logical(d.Type));
                            block.Statements.Add(d with { Type = node.Physical, Initializer = null, Initialize = false });
                            var target = new Expression.Reference(d.Name, new ShaderType.Pointer(d.Type, AddressSpace.Function));
                            locations[d.Name] = new(new Expression.Reference(d.Name, new ShaderType.Pointer(node.Physical, AddressSpace.Function)), node);
                            if (d.Initializer is not null) Store(block, target, d.Initializer, null);
                        }
                        else block.Statements.Add(d with { Type = Logical(d.Type), Initializer = d.Initializer is null ? null : Expr(d.Initializer), Mutable = false });
                        break;
                    case Statement.Store s: Store(block, s.Target, s.Value, s.MemoryAccess); break;
                    case Statement.Nested n: block.Statements.Add(new Statement.Nested(Body(n.Body))); break;
                    case Statement.Evaluate e: block.Statements.Add(new Statement.Evaluate(Expr(e.Value))); break;
                    case Statement.If i: block.Statements.Add(new Statement.If(Expr(i.Condition), Body(i.Accept), Body(i.Reject))); break;
                    case Statement.Loop l: block.Statements.Add(new Statement.Loop(Body(l.Body), Body(l.Continuing), l.BreakIf is null ? null : Expr(l.BreakIf))); break;
                    case Statement.Switch s: block.Statements.Add(new Statement.Switch(Expr(s.Selector), s.Cases.Select(c => c with { Body = Body(c.Body) }).ToArray())); break;
                    case Statement.Return r: block.Statements.Add(new Statement.Return(r.Value is null ? null : Expr(r.Value))); break;
                    default: block.Statements.Add(statement); break;
                }
                locations = saved; return block;
            }
        }
    }
}
