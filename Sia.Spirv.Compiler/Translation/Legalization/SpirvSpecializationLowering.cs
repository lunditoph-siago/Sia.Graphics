using System.Globalization;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Legalization;

// Target constant values retain type and operation identity before numeric IDs
// exist. Interning makes shared workgroup ABI checks independent of serialization.
internal abstract record SpirvSpecializationValue(int Identity, ShaderType Type)
{
    internal sealed record Constant(int Id, Expression Value) : SpirvSpecializationValue(Id, Value.Type);
    internal sealed record Reference(int Id, string Name, ShaderType ValueType) : SpirvSpecializationValue(Id, ValueType);
    internal sealed record Instruction(int Id, ShaderType ValueType, Op? Operation,
        IReadOnlyList<SpirvSpecializationValue> Values, IReadOnlyList<uint> Literals)
        : SpirvSpecializationValue(Id, ValueType);
}

internal sealed class SpirvSpecializationLowering(Module module)
{
    internal Dictionary<Expression, SpirvSpecializationValue> Expressions { get; } = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, SpirvSpecializationValue> values = new(StringComparer.Ordinal);
    private readonly Dictionary<ShaderType, int> types = [];
    private readonly Dictionary<string, ShaderConstant> constants = module.Constants.ToDictionary(c => c.Name, StringComparer.Ordinal);
    private readonly HashSet<string> resolving = new(StringComparer.Ordinal);
    private int TypeId(ShaderType type) { if (!types.TryGetValue(type, out int id)) types.Add(type, id = types.Count); return id; }
    private SpirvSpecializationValue Intern(string key, Func<int, SpirvSpecializationValue> create)
    {
        if (!values.TryGetValue(key, out var value)) values.Add(key, value = create(values.Count));
        return value;
    }
    private string ConstantKey(Expression expression) => expression switch {
        Expression.Literal l => TypeId(l.Type) + ":" + (l.Value switch {
            float f => BitConverter.SingleToUInt32Bits(f).ToString(CultureInfo.InvariantCulture),
            double d => BitConverter.DoubleToUInt64Bits(d).ToString(CultureInfo.InvariantCulture),
            Half h => BitConverter.HalfToUInt16Bits(h).ToString(CultureInfo.InvariantCulture),
            _ => System.Convert.ToString(l.Value, CultureInfo.InvariantCulture)
        }),
        Expression.Construct c => TypeId(c.Type) + ":[" + string.Join(';', c.Components.Select(ConstantKey)) + "]",
        _ => throw Error("Expected a literal or composite constant.", expression.Span)
    };
    private SpirvSpecializationValue Constant(Expression expression)
    {
        if (!ConstantEvaluator.TryEvaluate(expression, out var value)) throw Error("Expected a constant value.", expression.Span);
        return Intern("constant:" + ConstantKey(value), id => new SpirvSpecializationValue.Constant(id, value));
    }
    internal SpirvSpecializationValue Composite(ShaderType type, params SpirvSpecializationValue[] operands)
        => Instruction(null, type, operands, []);
    private SpirvSpecializationValue Instruction(Op? op, ShaderType type, SpirvSpecializationValue[] operands, params uint[] literals)
        => Intern("instruction:" + TypeId(type) + ":" + op + ":" + string.Join(',', operands.Select(v => v.Identity)) + ":" + string.Join(',', literals),
            id => new SpirvSpecializationValue.Instruction(id, type, op, Array.AsReadOnly(operands), Array.AsReadOnly(literals)));
    private static ShaderType.Scalar Component(ShaderType type) => type switch {
        ShaderType.Scalar scalar => scalar, ShaderType.Vector vector => vector.Component,
        _ => throw Error("Specialization operation requires scalar/vector data.")
    };
    private static bool Integer(ShaderType type) => Component(type).Kind is ScalarKind.Sint or ScalarKind.Uint;
    internal SpirvSpecializationValue Convert(ShaderType destination, Expression operand, bool bitcast = false)
    {
        ShaderType source = operand.Type; var value = Lower(operand);
        if (source == destination) return value;
        var from = Component(source); var to = Component(destination);
        if (Integer(source) && Integer(destination)) {
            if (from.Width == to.Width)
                return Instruction(Op.IAdd, destination, [value, Constant(new Expression.Construct(destination, []))]);
            if (bitcast) throw Error("Specialization bitcast cannot change scalar width.", operand.Span);
            value = Instruction(Op.SConvert, destination, [value]);
            if (from.Kind == ScalarKind.Uint && from.Width < to.Width) {
                ulong mask = (1UL << (from.Width * 8)) - 1;
                Expression maskValue = ConstantEvaluator.Convert(new Expression.Literal(mask, new ShaderType.Scalar(ScalarKind.Uint, 8)), to);
                if (destination is ShaderType.Vector) maskValue = new Expression.Construct(destination, [maskValue]);
                value = Instruction(Op.BitwiseAnd, destination, [value, Constant(maskValue)]);
            }
            return value;
        }
        if (!bitcast && from.Kind == ScalarKind.Float && to.Kind == ScalarKind.Float && from.Width != to.Width)
            return Instruction(Op.FConvert, destination, [value]);
        throw Error("This specialization conversion requires PipelineConstants resolution.", operand.Span);
    }
    internal SpirvSpecializationValue Lower(Expression expression)
    {
        if (!Expressions.TryGetValue(expression, out var value)) Expressions.Add(expression, value = LowerCore(expression));
        return value;
    }
    private SpirvSpecializationValue LowerCore(Expression expression)
    {
        if (ConstantEvaluator.TryEvaluateRuntime(expression, out var value)) return Constant(value);
        switch (expression) {
            case Expression.Reference reference:
                if (!constants.TryGetValue(reference.Name, out var constant)) throw Error("Specialization reference must denote a preceding constant.", reference.Span);
                if (constant.IsOverride) return Intern("reference:" + constant.Name,
                    id => new SpirvSpecializationValue.Reference(id, constant.Name, constant.Type));
                if (!resolving.Add(constant.Name)) throw Error("Recursive specialization constant.", reference.Span);
                try { return Lower(constant.Value ?? throw Error("Constant has no initializer.")); }
                finally { resolving.Remove(constant.Name); }
            case Expression.Unary unary:
                return Instruction(unary.Operator switch {
                    "!" => Op.LogicalNot, "~" => Op.Not, "-" when Integer(unary.Type) => Op.SNegate,
                    _ => throw Error("This unary specialization operation requires PipelineConstants resolution.", unary.Span)
                }, unary.Type, [Lower(unary.Operand)]);
            case Expression.Binary binary:
                bool signed = Component(binary.Left.Type).Kind == ScalarKind.Sint;
                bool boolean = Component(binary.Left.Type).Kind == ScalarKind.Bool;
                if (!boolean && !Integer(binary.Left.Type)) throw Error("Floating-point arithmetic specialization requires PipelineConstants resolution.", binary.Span);
                Op op = binary.Operator switch {
                    "+" => Op.IAdd, "-" => Op.ISub, "*" => Op.IMul, "/" => signed ? Op.SDiv : Op.UDiv, "%" => signed ? Op.SRem : Op.UMod,
                    "&" => boolean ? Op.LogicalAnd : Op.BitwiseAnd, "|" => boolean ? Op.LogicalOr : Op.BitwiseOr,
                    "^" => boolean ? Op.LogicalNotEqual : Op.BitwiseXor, "&&" => Op.LogicalAnd, "||" => Op.LogicalOr,
                    "<<" => Op.ShiftLeftLogical, ">>" => signed ? Op.ShiftRightArithmetic : Op.ShiftRightLogical,
                    "==" => boolean ? Op.LogicalEqual : Op.IEqual, "!=" => boolean ? Op.LogicalNotEqual : Op.INotEqual,
                    "<" => signed ? Op.SLessThan : Op.ULessThan, "<=" => signed ? Op.SLessThanEqual : Op.ULessThanEqual,
                    ">" => signed ? Op.SGreaterThan : Op.UGreaterThan, ">=" => signed ? Op.SGreaterThanEqual : Op.UGreaterThanEqual,
                    _ => throw Error("Unsupported specialization binary operation.", binary.Span)
                };
                return Instruction(op, binary.Type, [Lower(binary.Left), Lower(binary.Right)]);
            case Expression.Convert convert: return Convert(convert.Type, convert.Operand, convert.Bitcast);
            case Expression.Construct construct:
                if (construct.Components.Count == 0) return Constant(construct);
                if (construct.Type is ShaderType.Scalar) return Convert(construct.Type, construct.Components[0]);
                var components = new List<SpirvSpecializationValue>();
                foreach (var part in construct.Components) {
                    if (construct.Type is ShaderType.Vector && part.Type is ShaderType.Vector vector) {
                        var composite = Lower(part);
                        for (uint i = 0; i < vector.Size; i++) components.Add(Instruction(Op.CompositeExtract, vector.Component, [composite], i));
                    } else components.Add(Lower(part));
                }
                if (construct.Type is ShaderType.Vector destination && components.Count == 1)
                    components.AddRange(Enumerable.Repeat(components[0], destination.Size - 1));
                return Composite(construct.Type, components.ToArray());
            case Expression.Access access:
                if (!ConstantEvaluator.TryEvaluate(access.Index, out var index) || index is not Expression.Literal literal)
                    throw Error("Specialization composite index must be constant.", access.Span);
                return Instruction(Op.CompositeExtract, access.Type, [Lower(access.Base)], System.Convert.ToUInt32(literal.Value));
            case Expression.Member member:
                if (member.Base.Type is not ShaderType.Structure structure) throw Error("Specialization member requires a structure.", member.Span);
                int field = structure.Members.ToList().FindIndex(m => m.Name == member.Name);
                if (field < 0) throw Error("Unknown specialization member.", member.Span);
                return Instruction(Op.CompositeExtract, member.Type, [Lower(member.Base)], (uint)field);
            case Expression.Swizzle swizzle:
                var indices = swizzle.Components.Select(c => (uint)"xyzw".IndexOf(c)).ToArray(); var input = Lower(swizzle.Vector);
                return indices.Length == 1 ? Instruction(Op.CompositeExtract, swizzle.Type, [input], indices[0])
                    : Instruction(Op.VectorShuffle, swizzle.Type, [input, input], indices);
            case Expression.Select select:
                var condition = Lower(select.Condition);
                if (select.Type is ShaderType.Vector v && select.Condition.Type == ShaderType.Bool)
                    condition = Composite(new ShaderType.Vector(v.Size, ShaderType.Bool), Enumerable.Repeat(condition, v.Size).ToArray());
                return Instruction(Op.Select, select.Type, [condition, Lower(select.Accept), Lower(select.Reject)]);
            case Expression.Call { Function: "quantizeToF16", Arguments.Count: 1 } call:
                return Instruction(Op.QuantizeToF16, call.Type, [Lower(call.Arguments[0])]);
            default: throw Error("This specialization expression requires PipelineConstants resolution before SPIR-V writing.", expression.Span);
        }
    }
    private static ShaderException Error(string message, SourceSpan span = default) => new(DiagnosticStage.SpirvWrite, message, span);
}
