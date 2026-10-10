using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Front;

public static partial class WgslReader
{
    private sealed partial class Lowerer
    {
        private Symbol Lookup(string name)
        {
            foreach (var scope in scopes) if (scope.TryGetValue(name, out var local)) return local;
            if (RayQueryTypes.TryConstant(name, out uint ray)) return new(Expression.U32(ray), false, false);
            return ResolveGlobal(name);
        }
        private Expression Snapshot(Expression expression, Block block)
        {
            string name = Fresh(); block.Statements.Add(new Statement.Declare(name, expression.Type, expression, false));
            return new Expression.Reference(name, expression.Type) { Span = expression.Span };
        }
        private Expression Read(Symbol symbol, Block block) => symbol.Place ? Snapshot(new Expression.Load(symbol.Value), block) : symbol.Value;
        private static ShaderType ValueType(ShaderType type) => type is ShaderType.Pointer pointer ? pointer.Base : type;

        private Expression Eval(SExpression source, Block block)
        {
            Expression result;
            switch (source)
            {
                case SExpression.Literal literal:
                    result = WgslNumbers.Parse(literal.Text, literal.Span);
                    if (result.Type == ShaderType.F16 && !syntax.Enables.Contains("f16")) throw Error("f16 literal requires enable f16.", source.Span);
                    break;
                case SExpression.Name name:
                    if (name.Templates.Count != 0) throw Error("Type names are not values.", source.Span);
                    result = Read(Lookup(name.Text), block); break;
                case SExpression.Index or SExpression.Member:
                    result = Read(Container(source, block), block); break;
                case SExpression.Unary unary:
                    if (unary.Operator == "&")
                    {
                        Symbol place = Place(unary.Operand, block);
                        if (place.Value is Expression.Access access && ValueType(access.Base.Type) is ShaderType.Vector)
                            throw Error("Cannot take the address of a vector component.", unary.Operand.Span);
                        result = new Expression.Unary("&", place.Value, place.Value.Type); break;
                    }
                    if (unary.Operator == "*") { result = Read(Place(source, block), block); break; }
                    Expression operand = Eval(unary.Operand, block);
                    if (unary.Operator == "-" && operand.Type is ShaderType.CooperativeMatrix)
                        throw Error("Cooperative matrix negation requires scalar multiplication in WGSL.", source.Span);
                    result = new Expression.Unary(unary.Operator, operand, operand.Type); break;
                case SExpression.Binary binary:
                    Expression left = Eval(binary.Left, block);
                    var rightPrelude = new Block(); Expression right = Eval(binary.Right, rightPrelude);
                    if (binary.Operator is "&&" or "||")
                    {
                        left = Materialize(left, ShaderType.Bool); right = Materialize(right, ShaderType.Bool);
                        if (rightPrelude.Statements.Count != 0)
                        {
                            string name = Fresh(); block.Statements.Add(new Statement.Declare(name, ShaderType.Bool, left));
                            var reference = new Expression.Reference(name, new ShaderType.Pointer(ShaderType.Bool, AddressSpace.Function));
                            rightPrelude.Statements.Add(new Statement.Store(reference, right));
                            Expression condition = binary.Operator == "&&" ? left : new Expression.Unary("!", left, ShaderType.Bool);
                            block.Statements.Add(new Statement.If(condition, rightPrelude, new()));
                            result = Snapshot(new Expression.Load(reference), block); break;
                        }
                    }
                    block.Statements.AddRange(rightPrelude.Statements);
                    result = Binary(binary.Operator, left, right, binary.Span); break;
                case SExpression.Call call: result = Call(call, block); break;
                default: throw Error("Unsupported WGSL expression.", source.Span);
            }
            return result with { Span = source.Span };
        }

        private Symbol Place(SExpression source, Block block)
        {
            Symbol symbol = source switch
            {
                SExpression.Name n when n.Templates.Count == 0 => Lookup(n.Text),
                SExpression.Index or SExpression.Member => Container(source, block),
                SExpression.Unary { Operator: "*" } u => Dereference(Eval(u.Operand, block)),
                _ => throw Error("Expression is not a reference.", source.Span)
            };
            if (!symbol.Place) throw Error("Expression is not addressable.", source.Span);
            return symbol;
        }

        private Symbol Dereference(Expression pointer)
        {
            if (pointer.Type is not ShaderType.Pointer p) throw Error("Dereference needs a pointer.", pointer.Span);
            return new(new Expression.Unary("*", pointer, p), true, (p.Access & StorageAccess.Write) != 0);
        }

        private Symbol Container(SExpression source, Block block)
        {
            if (source is SExpression.Name n) return Lookup(n.Text);
            if (source is SExpression.Unary { Operator: "*" } dereference) return Dereference(Eval(dereference.Operand, block));
            SExpression parent = source switch { SExpression.Index i => i.Base, SExpression.Member m => m.Base, _ => throw Error("Invalid access.") };
            Symbol container = parent is SExpression.Name or SExpression.Index or SExpression.Member or SExpression.Unary { Operator: "*" }
                ? Container(parent, block) : new(Eval(parent, block), false, false);
            if (!container.Place && container.Value.Type is ShaderType.Pointer) container = Dereference(container.Value);
            ShaderType type = container.Place ? ValueType(container.Value.Type) : container.Value.Type;
            ShaderType Wrap(ShaderType value) => container.Place && container.Value.Type is ShaderType.Pointer p ? new ShaderType.Pointer(value, p.Space, p.Access) : value;
            if (source is SExpression.Member member)
            {
                if (type is ShaderType.Structure structure)
                {
                    var field = structure.Members.FirstOrDefault(m => m.Name == member.Field) ?? throw Error($"Unknown member '{member.Field}'.", source.Span);
                    return new(new Expression.Member(container.Value, field.Name, Wrap(field.Type)), container.Place, container.Writable);
                }
                if (type is ShaderType.Vector vector)
                {
                    string components = member.Field;
                    string alphabet = components.All("xyzw".Contains) ? "xyzw" : components.All("rgba".Contains) ? "rgba" : throw Error("Invalid vector swizzle.", source.Span);
                    if (components.Length is < 1 or > 4 || components.Any(c => alphabet.IndexOf(c) >= vector.Size)) throw Error("Swizzle component out of range.", source.Span);
                    if (components.Length == 1) return new(new Expression.Access(container.Value, Expression.U32((uint)alphabet.IndexOf(components[0])), Wrap(vector.Component)), container.Place, container.Writable);
                    Expression value = Read(container, block);
                    return new(new Expression.Swizzle(value, components, new ShaderType.Vector(components.Length, vector.Component)), false, false);
                }
                throw Error("Member access needs a structure or vector.", source.Span);
            }
            var index = (SExpression.Index)source;
            Expression subscript = Eval(index.Subscript, block);
            if (subscript.Type is ShaderType.Scalar { Kind: ScalarKind.AbstractInt }) subscript = Materialize(subscript, ShaderType.I32);
            if (subscript.Type is not ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint }) throw Error("Index must be an integer scalar.", source.Span);
            if (!container.Place && !ConstantEvaluator.TryEvaluate(subscript, out _))
            {
                var concrete = DefaultType(type);
                container = container with { Value = Materialize(container.Value, concrete) }; type = concrete;
            }
            ShaderType element = type switch
            {
                ShaderType.Array a => a.Element, ShaderType.BindingArray a => a.Element, ShaderType.Vector v => v.Component,
                ShaderType.Matrix m => new ShaderType.Vector(m.Rows, m.Component), _ => throw Error("This type cannot be indexed.", source.Span)
            };
            return new(new Expression.Access(container.Value, subscript, Wrap(element)), container.Place, container.Writable);
        }

        private Expression Materialize(Expression expression, ShaderType target)
        {
            if (expression.Type == target) return expression;
            if (!Implicit(expression.Type, target)) throw Error($"Cannot implicitly convert {expression.Type} to {target}.", expression.Span);
            if (ConstantEvaluator.TryEvaluate(expression, out var value))
            {
                if (value is Expression.Literal || target is ShaderType.Vector) return ConstantEvaluator.Convert(value, target);
                if (value is Expression.Construct c && target is ShaderType.Array array)
                    return new Expression.Construct(target, c.Components.Select(e => Materialize(e, array.Element)).ToArray());
                if (value is Expression.Construct matrix && target is ShaderType.Matrix m)
                    return new Expression.Construct(target, matrix.Components.Select(e => Materialize(e, new ShaderType.Vector(m.Rows, m.Component))).ToArray());
            }
            throw Error("An abstract value must be constant before materialization.", expression.Span);
        }

        private static bool Implicit(ShaderType from, ShaderType to) => (from, to) switch
        {
            _ when from == to => true,
            (ShaderType.Scalar { Kind: ScalarKind.AbstractInt }, ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint or ScalarKind.Float or ScalarKind.AbstractFloat }) => true,
            (ShaderType.Scalar { Kind: ScalarKind.AbstractFloat }, ShaderType.Scalar { Kind: ScalarKind.Float }) => true,
            (ShaderType.Vector a, ShaderType.Vector b) => a.Size == b.Size && Implicit(a.Component, b.Component),
            (ShaderType.Matrix a, ShaderType.Matrix b) => a.Columns == b.Columns && a.Rows == b.Rows && Implicit(a.Component, b.Component),
            (ShaderType.Array a, ShaderType.Array b) => a.Length == b.Length && Implicit(a.Element, b.Element), _ => false
        };
        private (Expression Left, Expression Right) Unify(Expression left, Expression right)
        {
            if (left.Type == right.Type) return (left, right);
            if (Implicit(left.Type, right.Type)) return (Materialize(left, right.Type), right);
            if (Implicit(right.Type, left.Type)) return (left, Materialize(right, left.Type));
            throw Error("Operands have incompatible types.", left.Span);
        }

        private Expression Binary(string operation, Expression left, Expression right, SourceSpan span = default)
        {
            ShaderType type;
            if (operation is "<<" or ">>")
            {
                if (left.Type is ShaderType.Scalar { Kind: ScalarKind.AbstractInt } && !ConstantEvaluator.TryEvaluate(right, out _)) left = Materialize(left, ShaderType.I32);
                right = Materialize(right, right.Type is ShaderType.Vector v ? new ShaderType.Vector(v.Size, ShaderType.U32) : ShaderType.U32);
                type = left.Type;
            }
            else if (operation == "*" && left.Type is ShaderType.Matrix a && right.Type is ShaderType.Vector b)
            {
                if (a.Columns != b.Size) throw Error("Matrix/vector dimensions do not match.", span);
                right = Materialize(right, new ShaderType.Vector(b.Size, a.Component)); type = new ShaderType.Vector(a.Rows, a.Component);
            }
            else if (operation == "*" && left.Type is ShaderType.Vector c && right.Type is ShaderType.Matrix d)
            {
                if (c.Size != d.Rows) throw Error("Vector/matrix dimensions do not match.", span);
                left = Materialize(left, new ShaderType.Vector(c.Size, d.Component)); type = new ShaderType.Vector(d.Columns, d.Component);
            }
            else if (operation == "*" && left.Type is ShaderType.Matrix e && right.Type is ShaderType.Matrix f)
            {
                if (e.Columns != f.Rows || e.Component != f.Component) throw Error("Matrix dimensions do not match.", span);
                type = new ShaderType.Matrix(f.Columns, e.Rows, e.Component);
            }
            else if (operation is "+" or "-" or "*" or "/" or "%" && left.Type is ShaderType.Vector vector && right.Type is ShaderType.Scalar)
            { right = Materialize(right, vector.Component); type = left.Type; }
            else if (operation is "+" or "-" or "*" or "/" or "%" && left.Type is ShaderType.Scalar && right.Type is ShaderType.Vector vector2)
            { left = Materialize(left, vector2.Component); type = right.Type; }
            else if (operation == "*" && left.Type is ShaderType.CooperativeMatrix coop && right.Type is ShaderType.Scalar)
            { right = Materialize(right, coop.Component); type = left.Type; }
            else if (operation == "*" && right.Type is ShaderType.CooperativeMatrix coop2 && left.Type is ShaderType.Scalar)
            { left = Materialize(left, coop2.Component); type = right.Type; }
            else if (operation == "*" && left.Type is ShaderType.Matrix matrix && right.Type is ShaderType.Scalar)
            { right = Materialize(right, matrix.Component); type = left.Type; }
            else if (operation == "*" && right.Type is ShaderType.Matrix matrix2 && left.Type is ShaderType.Scalar)
            { left = Materialize(left, matrix2.Component); type = right.Type; }
            else
            {
                (left, right) = Unify(left, right); type = left.Type;
                if (operation is "==" or "!=" or "<" or ">" or "<=" or ">=") type = type is ShaderType.Vector v ? new ShaderType.Vector(v.Size, ShaderType.Bool) : ShaderType.Bool;
            }
            return new Expression.Binary(operation, left, right, type) { Span = span };
        }

        private Expression Call(SExpression.Call source, Block block)
        {
            if (source.Target is not SExpression.Name name) throw Error("Call target must be a function or type name.", source.Span);
            var arguments = source.Arguments.Select(a => Eval(a, block)).ToArray();
            if (name.Text == "bitcast")
            {
                if (arguments.Length != 1 || name.Templates is not [SExpression.Name target]) throw Error("Invalid bitcast.", source.Span);
                return new Expression.Convert(ResolveType(target), arguments[0], true);
            }
            if (functions.TryGetValue(name.Text, out var function))
            {
                if (name.Templates.Count != 0 || arguments.Length != function.Arguments.Count || function.Stage is not null) throw Error("Invalid function call.", source.Span);
                arguments = arguments.Select((e, i) => Materialize(e, function.Arguments[i].Type)).ToArray();
                var call = new Expression.Call(name.Text, arguments, function.ReturnType) { Binding = CallBinding.Function, Span = source.Span };
                return function.ReturnType is ShaderType.Void ? call : Snapshot(call, block);
            }
            if (IsTypeName(name.Text)) return Constructor(name, arguments);
            return Builtin(name, arguments, block);
        }

        private bool IsTypeName(string name) => name is "bool" or "i16" or "u16" or "i32" or "u32" or "f32" or "f16" or "i64" or "u64" or "f64" or "array" or "RayDesc" or "RayIntersection" or "coop_mat8x8" or "coop_mat16x16"
            || name.StartsWith("vec", StringComparison.Ordinal) || name.StartsWith("mat", StringComparison.Ordinal) || namedTypes.ContainsKey(name) || syntax.Aliases.ContainsKey(name);

        private Expression Constructor(SExpression.Name name, Expression[] arguments)
        {
            ShaderType type;
            if (name.Templates.Count == 0 && name.Text.Length == 4 && name.Text.StartsWith("vec", StringComparison.Ordinal) && name.Text[3] is >= '2' and <= '4')
            {
                ShaderType.Scalar scalar = arguments.Length == 0 ? WgslNumbers.AbstractInt : arguments[0].Type switch { ShaderType.Scalar s => s, ShaderType.Vector v => v.Component, _ => throw Error("Invalid vector component.") };
                foreach (var argument in arguments.Skip(1))
                {
                    var other = argument.Type is ShaderType.Vector v ? v.Component : argument.Type;
                    if (scalar != other) scalar = Implicit(scalar, other) && other is ShaderType.Scalar s ? s : Implicit(other, scalar) ? scalar : throw Error("Vector components have incompatible types.");
                }
                type = new ShaderType.Vector(name.Text[3] - '0', scalar);
            }
            else if (name.Templates.Count == 0 && name.Text.Length == 6 && name.Text.StartsWith("mat", StringComparison.Ordinal) && name.Text[3] is >= '2' and <= '4' && name.Text[4] == 'x' && name.Text[5] is >= '2' and <= '4')
            {
                ShaderType.Scalar scalar = WgslNumbers.AbstractFloat;
                foreach (var argument in arguments)
                {
                    ShaderType.Scalar component = argument.Type switch { ShaderType.Scalar s => s, ShaderType.Vector v => v.Component, ShaderType.Matrix m => m.Component, _ => throw Error("Invalid matrix component.", name.Span) };
                    if (component.Kind == ScalarKind.AbstractInt) component = WgslNumbers.AbstractFloat;
                    if (Implicit(scalar, component)) scalar = component;
                    else if (!Implicit(component, scalar)) throw Error("Matrix components have incompatible types.", name.Span);
                }
                type = new ShaderType.Matrix(name.Text[3] - '0', name.Text[5] - '0', scalar);
            }
            else if (name.Text == "array" && name.Templates.Count == 0)
            {
                if (arguments.Length == 0) throw Error("Inferred array needs elements.");
                ShaderType element = arguments[0].Type;
                foreach (var argument in arguments.Skip(1)) if (Implicit(element, argument.Type)) element = argument.Type; else if (!Implicit(argument.Type, element)) throw Error("Array elements have incompatible types.");
                type = new ShaderType.Array(element, (uint)arguments.Length);
            }
            else type = ResolveType(name);
            switch (type)
            {
                case ShaderType.Scalar:
                    if (arguments.Length > 1) throw Error("Scalar constructor needs at most one argument.");
                    if (arguments.Length == 1)
                    {
                        if (arguments[0].Type is not ShaderType.Scalar) throw Error("Invalid scalar conversion.");
                        return new Expression.Convert(type, arguments[0]);
                    }
                    break;
                case ShaderType.Vector vector:
                    int components = arguments.Sum(a => a.Type is ShaderType.Vector v ? v.Size : a.Type is ShaderType.Scalar ? 1 : 100);
                    if (arguments.Length != 0 && components != vector.Size && !(arguments.Length == 1 && arguments[0].Type is ShaderType.Scalar)) throw Error("Wrong vector component count.");
                    if (arguments.Length == 1 && arguments[0].Type is ShaderType.Vector conversion)
                        return new Expression.Convert(type, arguments[0]);
                    arguments = arguments.Select(e => Materialize(e, e.Type is ShaderType.Vector v ? new ShaderType.Vector(v.Size, vector.Component) : vector.Component)).ToArray(); break;
                case ShaderType.Matrix matrix:
                    if (arguments.Length == 1 && arguments[0].Type is ShaderType.Matrix) return new Expression.Convert(type, arguments[0]);
                    if (arguments.Length == matrix.Columns * matrix.Rows && arguments.All(a => a.Type is ShaderType.Scalar))
                    {
                        var columns = new List<Expression>();
                        for (int i = 0; i < matrix.Columns; i++) columns.Add(new Expression.Construct(new ShaderType.Vector(matrix.Rows, matrix.Component), arguments.Skip(i * matrix.Rows).Take(matrix.Rows).Select(e => Materialize(e, matrix.Component)).ToArray()));
                        arguments = columns.ToArray();
                    }
                    else if (arguments.Length != 0)
                    {
                        if (arguments.Length != matrix.Columns) throw Error("Wrong matrix component count.");
                        arguments = arguments.Select(e => Materialize(e, new ShaderType.Vector(matrix.Rows, matrix.Component))).ToArray();
                    }
                    break;
                case ShaderType.CooperativeMatrix:
                    if (arguments.Length != 0) throw Error("Cooperative matrix constructors require no arguments.", name.Span);
                    break;
                case ShaderType.Array array:
                    if (array.Length is null || arguments.Length != 0 && arguments.Length != array.Length) throw Error("Wrong array element count.");
                    arguments = arguments.Select(e => Materialize(e, array.Element)).ToArray(); break;
                case ShaderType.Structure structure:
                    if (arguments.Length != 0 && arguments.Length != structure.Members.Count) throw Error("Wrong structure member count.");
                    arguments = arguments.Select((e, i) => Materialize(e, structure.Members[i].Type)).ToArray(); break;
                default: throw Error("This type cannot be constructed.", name.Span);
            }
            return new Expression.Construct(type, arguments);
        }
    }
}
