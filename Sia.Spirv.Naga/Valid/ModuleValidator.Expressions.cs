using Sia.Spirv.Naga.IR;

namespace Sia.Spirv.Naga.Valid;

public static partial class ModuleValidator
{
    private sealed partial class Validator
    {
        private ShaderType Expr(Expression expression)
        {
            Require(++expressionDepth <= 512, "Expression nesting limit exceeded.", expression.Span);
            try
            {
                Type(expression.Type);
                ShaderType result = expression switch
                {
                    Expression.Literal literal => Literal(literal),
                    Expression.Reference reference => Reference(reference),
                    Expression.Load load => Load(load),
                    Expression.Unary unary => Unary(unary),
                    Expression.Binary binary => Binary(binary),
                    Expression.Call call => Call(call),
                    Expression.Construct construct => Construct(construct),
                    Expression.Convert convert => Convert(convert),
                    Expression.Access access => Access(access),
                    Expression.Member member => Member(member),
                    Expression.Swizzle swizzle => Swizzle(swizzle),
                    Expression.Select select => Select(select),
                    _ => throw Error("Unknown expression variant.", expression.Span)
                };
                Same(result, expression.Type, "Expression result type mismatch.", expression.Span);
                return result;
            }
            finally { expressionDepth--; }
        }

        private ShaderType Literal(Expression.Literal literal)
        {
            Require(literal.Type is ShaderType.Scalar, "Literal must be scalar.", literal.Span);
            bool valid = (((ShaderType.Scalar)literal.Type).Kind, ((ShaderType.Scalar)literal.Type).Width) switch
            {
                (ScalarKind.Bool, 1) => literal.Value is bool,
                (ScalarKind.Sint, 2) => literal.Value is short,
                (ScalarKind.Sint, 4) => literal.Value is int,
                (ScalarKind.Sint, 8) => literal.Value is long,
                (ScalarKind.Uint, 2) => literal.Value is ushort,
                (ScalarKind.Uint, 4) => literal.Value is uint,
                (ScalarKind.Uint, 8) => literal.Value is ulong,
                (ScalarKind.Float, 2) => literal.Value is Half,
                (ScalarKind.Float, 4) => literal.Value is float,
                (ScalarKind.Float, 8) => literal.Value is double,
                (ScalarKind.AbstractInt, 8) => literal.Value is long or int or System.Numerics.BigInteger,
                (ScalarKind.AbstractFloat, 8) => literal.Value is double,
                _ => false
            };
            Require(valid, "Literal payload does not match its scalar kind.", literal.Span);
            return literal.Type;
        }
        private ShaderType Reference(Expression.Reference reference)
        {
            var symbol = Lookup(reference.Name);
            if (reference.Type is ShaderType.Pointer p && symbol.Place)
            {
                Same(p.Base, symbol.Type, "Reference pointee type mismatch.", reference.Span);
                Require(p.Space == symbol.Space && (!symbol.Writable ? (p.Access & StorageAccess.Write) == 0 : true), "Reference address space or access mismatch.", reference.Span);
            }
            else Same(reference.Type, symbol.Type, "Reference type mismatch.", reference.Span);
            return reference.Type;
        }
        private ShaderType Load(Expression.Load load)
        {
            Expr(load.Pointer); var place = Place(load.Pointer);
            MemoryAccess(load.MemoryAccess, place.Space, true, load.Span);
            Require(place.Place && place.Type is not (ShaderType.Atomic or ShaderType.RayQuery), "Load requires non-atomic data; ray queries cannot be copied.", load.Span);
            if (load.Pointer.Type is ShaderType.Pointer pointer) Require((pointer.Access & StorageAccess.Read) != 0, "Cannot load write-only memory.", load.Span);
            return place.Type;
        }
        private Variable Place(Expression expression) => expression switch
        {
            Expression.Reference r => Lookup(r.Name),
            Expression.Unary { Operator: "*" } u when u.Operand.Type is ShaderType.Pointer p => new(p.Base, true, (p.Access & StorageAccess.Write) != 0, p.Space),
            Expression.Access a => Place(a.Base) with { Type = DataType(a.Type) },
            Expression.Member m => Place(m.Base) with { Type = DataType(m.Type) },
            _ => new(DataType(expression.Type), false, false)
        };
        private ShaderType Unary(Expression.Unary unary)
        {
            ShaderType operand = Expr(unary.Operand);
            switch (unary.Operator)
            {
                case "&":
                    var place = Place(unary.Operand); Require(place.Place, "Address-of requires a place.", unary.Span);
                    Require(unary.Type is ShaderType.Pointer p && p.Base == place.Type && p.Space == place.Space && (place.Writable || (p.Access & StorageAccess.Write) == 0), "Invalid address-of pointer type.", unary.Span);
                    return unary.Type;
                case "*":
                    Require(operand is ShaderType.Pointer, "Dereference requires a pointer.", unary.Span);
                    // Places retain pointer types in the shared IR, unlike pointer values.
                    return operand;
                case "!": Require(Scalar(operand)?.Kind == ScalarKind.Bool && operand is not ShaderType.Matrix, "Logical not requires boolean scalar/vector.", unary.Span); break;
                case "~": Require(Integer(operand) && operand is not ShaderType.Matrix, "Bitwise not requires integer scalar/vector.", unary.Span); break;
                case "-": Require(Numeric(operand) && Scalar(operand)?.Kind != ScalarKind.Uint, "Negation requires signed numeric data.", unary.Span); break;
                default: throw Error("Unknown unary operator.", unary.Span);
            }
            return operand;
        }
        private ShaderType Binary(Expression.Binary binary)
        {
            ShaderType left = Expr(binary.Left), right = Expr(binary.Right);
            string op = binary.Operator;
            if (left is ShaderType.CooperativeMatrix || right is ShaderType.CooperativeMatrix)
            {
                if (op is "+" or "-" && left == right) return left;
                if (op == "*" && left is ShaderType.CooperativeMatrix cooperativeLeft && right == cooperativeLeft.Component) return left;
                if (op == "*" && right is ShaderType.CooperativeMatrix cooperativeRight && left == cooperativeRight.Component) return right;
                throw Error("Invalid cooperative matrix arithmetic; use coopMultiplyAdd for a matrix product.", binary.Span);
            }
            bool matrices = left is ShaderType.Matrix || right is ShaderType.Matrix;
            if (op is "/" or "%" && Integer(left) && ConstantEvaluator.TryEvaluate(binary.Right, out var divisor))
            {
                bool ContainsZero(Expression value) => value is Expression.Literal l ? System.Convert.ToDouble(l.Value) == 0
                    : value is Expression.Construct c && c.Components.Any(ContainsZero);
                Require(!ContainsZero(divisor), "Integer division or remainder has a constant zero divisor.", binary.Span);
            }
            ShaderType result;
            if (op == "*" && left is ShaderType.Matrix a && right is ShaderType.Vector b)
            { Require(a.Columns == b.Size && a.Component == b.Component, "Invalid matrix/vector product.", binary.Span); result = new ShaderType.Vector(a.Rows, a.Component); }
            else if (op == "*" && left is ShaderType.Vector c && right is ShaderType.Matrix d)
            { Require(c.Size == d.Rows && c.Component == d.Component, "Invalid vector/matrix product.", binary.Span); result = new ShaderType.Vector(d.Columns, d.Component); }
            else if (op == "*" && left is ShaderType.Matrix e && right is ShaderType.Matrix f)
            { Require(e.Columns == f.Rows && e.Component == f.Component, "Invalid matrix product.", binary.Span); result = new ShaderType.Matrix(f.Columns, e.Rows, e.Component); }
            else if (op is "+" or "-" or "*" or "/" or "%" && left is ShaderType.Scalar && right is ShaderType.Vector && left == Scalar(right)) result = right;
            else if (op is "+" or "-" or "*" or "/" or "%" && right is ShaderType.Scalar && left is ShaderType.Vector && right == Scalar(left)) result = left;
            else if (op == "*" && left is ShaderType.Scalar && right is ShaderType.Matrix && left == Scalar(right)) result = right;
            else if (op == "*" && right is ShaderType.Scalar && left is ShaderType.Matrix && right == Scalar(left)) result = left;
            else if (op is "<<" or ">>")
            {
                Require(Integer(left) && right == (left is ShaderType.Vector v ? new ShaderType.Vector(v.Size, ShaderType.U32) : ShaderType.U32), "Shift requires integer data and matching unsigned shift count.", binary.Span);
                result = left;
            }
            else
            {
                Same(left, right, "Binary operand types differ.", binary.Span);
                result = left;
                if (op is "==" or "!=" or "<" or ">" or "<=" or ">=") result = left is ShaderType.Vector v ? new ShaderType.Vector(v.Size, ShaderType.Bool) : ShaderType.Bool;
            }
            bool valid = op switch
            {
                "+" or "-" => Numeric(left) && Numeric(right),
                "*" => Numeric(left) && Numeric(right),
                "/" or "%" => Numeric(left) && Numeric(right) && !matrices,
                "==" or "!=" => !matrices && (Numeric(left) || Scalar(left)?.Kind == ScalarKind.Bool),
                "<" or ">" or "<=" or ">=" => !matrices && Numeric(left),
                "&" or "|" => !matrices && (Integer(left) || Scalar(left)?.Kind == ScalarKind.Bool),
                "^" or "<<" or ">>" => Integer(left),
                "&&" or "||" => left == ShaderType.Bool && right == ShaderType.Bool,
                _ => false
            };
            Require(valid, "Operator is not defined for these operand types.", binary.Span); return result;
        }
        private ShaderType Construct(Expression.Construct construct)
        {
            ShaderType type = construct.Type; var args = construct.Components.Select(Expr).ToArray();
            // SPIR-V OpConstantNull may zero an array whose length is specialized.
            // WGSL rejects its constructor in the frontend; resolve this IR zero
            // value before writing either source constructors or SPIR-V.
            Require(Data(type) && type is not ShaderType.Atomic && !RuntimeSized(type) && (!PipelineSized(type) || args.Length == 0) && !ContainsAtomic(type), "Type cannot be constructed.", construct.Span);
            if (args.Length == 0) return type;
            switch (type)
            {
                case ShaderType.Scalar: Require(args.Length == 1 && args[0] == type, "Invalid scalar construction.", construct.Span); break;
                case ShaderType.Vector v:
                    Require(args.All(a => a == v.Component || a is ShaderType.Vector w && w.Component == v.Component), "Vector component type mismatch.", construct.Span);
                    Require(args.Length == 1 && args[0] == v.Component || args.Sum(a => a is ShaderType.Vector w ? w.Size : 1) == v.Size, "Vector component count mismatch.", construct.Span); break;
                case ShaderType.Matrix m:
                    Require(args.Length == m.Columns && args.All(a => a == new ShaderType.Vector(m.Rows, m.Component)) || args.Length == m.Columns * m.Rows && args.All(a => a == m.Component), "Matrix component shape mismatch.", construct.Span); break;
                case ShaderType.CooperativeMatrix m: Require(args.Length == 1 && args[0] == m.Component, "Cooperative matrix splat requires one scalar component.", construct.Span); break;
                case ShaderType.Array a: Require(args.Length == a.Length && args.All(t => t == a.Element), "Array constructor type/count mismatch.", construct.Span); break;
                case ShaderType.Structure s: Require(args.Length == s.Members.Count && args.Select((a, i) => a == s.Members[i].Type).All(v => v), "Structure constructor type/count mismatch.", construct.Span); break;
            }
            return type;
        }
        private ShaderType Convert(Expression.Convert convert)
        {
            ShaderType from = Expr(convert.Operand), to = convert.Type;
            Require(Scalar(from) is not null && Scalar(to) is not null, "Conversion requires scalar, vector, or matrix data.", convert.Span);
            if (convert.Bitcast)
            {
                int Bits(ShaderType t) => Scalar(t)!.Width * (t is ShaderType.Vector v ? v.Size : 1);
                Require(from is not (ShaderType.Matrix or ShaderType.CooperativeMatrix) && to is not (ShaderType.Matrix or ShaderType.CooperativeMatrix) && Numeric(from) && Numeric(to) && Bits(from) == Bits(to), "Bitcast requires numeric scalar/vector data of equal bit size.", convert.Span);
            }
            else Require((from, to) switch
            {
                (ShaderType.Scalar, ShaderType.Scalar) => true,
                (ShaderType.Vector a, ShaderType.Vector b) => a.Size == b.Size,
                (ShaderType.Matrix a, ShaderType.Matrix b) => a.Columns == b.Columns && a.Rows == b.Rows,
                _ => false
            }, "Conversion shape mismatch.", convert.Span);
            return to;
        }
        private ShaderType Access(Expression.Access access)
        {
            ShaderType parent = Expr(access.Base), index = Expr(access.Index);
            Require(index is ShaderType.Scalar && Integer(index), "Index must be an integer scalar.", access.Span);
            ShaderType element = DataType(parent) switch
            {
                ShaderType.Array a => a.Element, ShaderType.BindingArray a => a.Element,
                ShaderType.Vector v => v.Component, ShaderType.Matrix m => new ShaderType.Vector(m.Rows, m.Component),
                _ => throw Error("Value cannot be indexed.", access.Span)
            };
            if (ConstantEvaluator.TryEvaluate(access.Index, out var constant) && constant is Expression.Literal literal)
            {
                long i = System.Convert.ToInt64(literal.Value);
                uint? count = DataType(parent) switch { ShaderType.Array a => a.Length, ShaderType.BindingArray a => a.Length, ShaderType.Vector v => (uint)v.Size, ShaderType.Matrix m => (uint)m.Columns, _ => null };
                Require(i >= 0 && (!count.HasValue || (ulong)i < count.Value), "Constant index is out of bounds.", access.Span);
            }
            return parent is ShaderType.Pointer pointer ? new ShaderType.Pointer(element, pointer.Space, pointer.Access) : element;
        }
        private ShaderType Member(Expression.Member member)
        {
            ShaderType parent = Expr(member.Base);
            Require(DataType(parent) is ShaderType.Structure, "Member access requires a structure.", member.Span);
            var field = ((ShaderType.Structure)DataType(parent)).Members.FirstOrDefault(m => m.Name == member.Name) ?? throw Error("Unknown structure member.", member.Span);
            return parent is ShaderType.Pointer pointer ? new ShaderType.Pointer(field.Type, pointer.Space, pointer.Access) : field.Type;
        }
        private ShaderType Swizzle(Expression.Swizzle swizzle)
        {
            ShaderType type = Expr(swizzle.Vector); Require(type is ShaderType.Vector, "Swizzle requires a vector.", swizzle.Span);
            var vector = (ShaderType.Vector)type;
            string alphabet = swizzle.Components.All("xyzw".Contains) ? "xyzw" : "rgba";
            Require(swizzle.Components.Length is >= 1 and <= 4 && swizzle.Components.All(c => alphabet.IndexOf(c) is int i && i >= 0 && i < vector.Size), "Invalid swizzle components.", swizzle.Span);
            return swizzle.Components.Length == 1 ? vector.Component : new ShaderType.Vector(swizzle.Components.Length, vector.Component);
        }
        private ShaderType Select(Expression.Select select)
        {
            ShaderType condition = Expr(select.Condition), accept = Expr(select.Accept), reject = Expr(select.Reject);
            Same(accept, reject, "Select alternatives have different types.", select.Span);
            Require(condition == ShaderType.Bool || accept is ShaderType.Vector v && condition == new ShaderType.Vector(v.Size, ShaderType.Bool), "Select condition shape mismatch.", select.Span);
            Require(accept is ShaderType.Scalar or ShaderType.Vector, "Select requires scalar/vector alternatives.", select.Span); return accept;
        }
    }
}
