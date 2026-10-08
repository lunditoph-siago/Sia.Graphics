using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class SpirvWriter
{
    private sealed partial class Writer
    {
        private readonly Dictionary<Expression, uint> specializationExpressions = new(ReferenceEqualityComparer.Instance);
        private readonly Func<string, Expression> specializationDefaults = Proc.PipelineConstantResolver.DefaultValues(module);

        private uint SpecializationLength(string name)
        {
            if (!globals.TryGetValue(name, out var length) || length.Place) throw Error("Array length specialization must precede its type.");
            if (specializationDefaults(name) is not Expression.Literal value) throw Error("Array length specialization default must be an integer.");
            try
            {
                if (System.Convert.ToUInt32(value.Value) == 0) throw Error("Array length default must be positive; supply PipelineConstants to replace it.");
            }
            catch (OverflowException) { throw Error("Array length default must be positive and fit in u32; supply PipelineConstants to replace it."); }
            return length.Id;
        }

        private uint SpecializationLengthU32(string name)
        {
            if (!globals.TryGetValue(name, out var length) || length.Place) throw Error("Array length specialization must precede its type.");
            return SpecializationConvert(ShaderType.U32, new Expression.Reference(name, length.Type));
        }

        private uint Specialization(Expression expression)
        {
            if (specializationExpressions.TryGetValue(expression, out uint known)) return known;
            if (ConstantEvaluator.TryEvaluateRuntime(expression, out var value)) return Constant(value);
            uint result;
            switch (expression)
            {
                case Expression.Reference reference:
                    if (!globals.TryGetValue(reference.Name, out var symbol) || symbol.Place) throw Error("Specialization reference must denote a preceding constant.", reference.Span);
                    result = symbol.Id; break;
                case Expression.Unary unary:
                    result = SpecializationResult(unary.Operator switch
                    {
                        "!" => Op.LogicalNot, "~" => Op.Not,
                        "-" when IntegerSpecialization(unary.Type) => Op.SNegate,
                        _ => throw Error("This unary specialization operation requires PipelineConstants resolution.", unary.Span)
                    }, unary.Type, Specialization(unary.Operand)); break;
                case Expression.Binary binary:
                    result = SpecializationBinary(binary); break;
                case Expression.Convert convert:
                    result = SpecializationConvert(convert.Type, convert.Operand, convert.Bitcast); break;
                case Expression.Construct construct:
                    result = SpecializationConstruct(construct); break;
                case Expression.Access access:
                    if (!ConstantEvaluator.TryEvaluate(access.Index, out var index) || index is not Expression.Literal literal)
                        throw Error("Specialization composite index must be constant.", access.Span);
                    result = SpecializationResult(Op.CompositeExtract, access.Type, Specialization(access.Base), System.Convert.ToUInt32(literal.Value)); break;
                case Expression.Member member:
                    if (member.Base.Type is not ShaderType.Structure structure) throw Error("Specialization member requires a structure.", member.Span);
                    int field = structure.Members.ToList().FindIndex(m => m.Name == member.Name);
                    if (field < 0) throw Error("Unknown specialization member.", member.Span);
                    result = SpecializationResult(Op.CompositeExtract, member.Type, Specialization(member.Base), (uint)field); break;
                case Expression.Swizzle swizzle:
                    var indices = swizzle.Components.Select(c => (uint)"xyzw".IndexOf(c)).ToArray();
                    uint input = Specialization(swizzle.Vector);
                    result = indices.Length == 1 ? SpecializationResult(Op.CompositeExtract, swizzle.Type, input, indices[0])
                        : SpecializationResult(Op.VectorShuffle, swizzle.Type, new[] { input, input }.Concat(indices).ToArray()); break;
                case Expression.Select select:
                    uint condition = Specialization(select.Condition);
                    if (select.Type is ShaderType.Vector vector && select.Condition.Type == ShaderType.Bool)
                        condition = SpecializationComposite(new ShaderType.Vector(vector.Size, ShaderType.Bool), Enumerable.Repeat(condition, vector.Size).ToArray());
                    result = SpecializationResult(Op.Select, select.Type, condition, Specialization(select.Accept), Specialization(select.Reject)); break;
                case Expression.Call { Function: "quantizeToF16", Arguments.Count: 1 } call:
                    result = SpecializationResult(Op.QuantizeToF16, call.Type, Specialization(call.Arguments[0])); break;
                default: throw Error("This specialization expression requires PipelineConstants resolution before SPIR-V writing.", expression.Span);
            }
            specializationExpressions.Add(expression, result); return result;
        }

        private static ShaderType.Scalar SpecializationComponent(ShaderType type) => type switch
        {
            ShaderType.Scalar s => s, ShaderType.Vector v => v.Component,
            _ => throw new ShaderException(DiagnosticStage.SpirvWrite, "Specialization operation requires scalar/vector data.")
        };
        private static bool IntegerSpecialization(ShaderType type) => SpecializationComponent(type).Kind is ScalarKind.Sint or ScalarKind.Uint;

        private uint SpecializationBinary(Expression.Binary expression)
        {
            bool signed = SpecializationComponent(expression.Left.Type).Kind == ScalarKind.Sint;
            bool boolean = SpecializationComponent(expression.Left.Type).Kind == ScalarKind.Bool;
            if (!boolean && !IntegerSpecialization(expression.Left.Type))
                throw Error("Floating-point arithmetic specialization requires PipelineConstants resolution.", expression.Span);
            Op op = expression.Operator switch
            {
                "+" => Op.IAdd, "-" => Op.ISub, "*" => Op.IMul,
                "/" => signed ? Op.SDiv : Op.UDiv, "%" => signed ? Op.SRem : Op.UMod,
                "&" => boolean ? Op.LogicalAnd : Op.BitwiseAnd, "|" => boolean ? Op.LogicalOr : Op.BitwiseOr,
                "^" => boolean ? Op.LogicalNotEqual : Op.BitwiseXor, "&&" => Op.LogicalAnd, "||" => Op.LogicalOr,
                "<<" => Op.ShiftLeftLogical, ">>" => signed ? Op.ShiftRightArithmetic : Op.ShiftRightLogical,
                "==" => boolean ? Op.LogicalEqual : Op.IEqual, "!=" => boolean ? Op.LogicalNotEqual : Op.INotEqual,
                "<" => signed ? Op.SLessThan : Op.ULessThan, "<=" => signed ? Op.SLessThanEqual : Op.ULessThanEqual,
                ">" => signed ? Op.SGreaterThan : Op.UGreaterThan, ">=" => signed ? Op.SGreaterThanEqual : Op.UGreaterThanEqual,
                _ => throw Error("Unsupported specialization binary operation.", expression.Span)
            };
            return SpecializationResult(op, expression.Type, Specialization(expression.Left), Specialization(expression.Right));
        }

        private uint SpecializationConvert(ShaderType destination, Expression operand, bool bitcast = false)
        {
            ShaderType source = operand.Type; uint value = Specialization(operand);
            if (source == destination) return value;
            var from = SpecializationComponent(source); var to = SpecializationComponent(destination);
            if (IntegerSpecialization(source) && IntegerSpecialization(destination))
            {
                if (from.Width == to.Width)
                    // Integer signedness reinterpretation; Bitcast is Kernel-only in SpecConstantOp.
                    return SpecializationResult(Op.IAdd, destination, value, Constant(new Expression.Construct(destination, [])));
                if (bitcast) throw Error("Specialization bitcast cannot change scalar width.", operand.Span);
                value = SpecializationResult(Op.SConvert, destination, value);
                if (from.Kind == ScalarKind.Uint && from.Width < to.Width)
                {
                    // SPIR-V 1.3 UConvert specialization needs an extension. SConvert
                    // followed by a low-bit mask provides portable zero extension.
                    ulong mask = (1UL << (from.Width * 8)) - 1;
                    Expression maskValue = ConstantEvaluator.Convert(new Expression.Literal(mask, new ShaderType.Scalar(ScalarKind.Uint, 8)), to);
                    if (destination is ShaderType.Vector) maskValue = new Expression.Construct(destination, [maskValue]);
                    value = SpecializationResult(Op.BitwiseAnd, destination, value, Constant(maskValue));
                }
                return value;
            }
            if (!bitcast && from.Kind == ScalarKind.Float && to.Kind == ScalarKind.Float && from.Width != to.Width)
                return SpecializationResult(Op.FConvert, destination, value);
            throw Error("This specialization conversion requires PipelineConstants resolution.", operand.Span);
        }

        private uint SpecializationConstruct(Expression.Construct construct)
        {
            if (construct.Components.Count == 0) return Null(construct.Type);
            if (construct.Type is ShaderType.Scalar) return SpecializationConvert(construct.Type, construct.Components[0]);
            var components = new List<uint>();
            foreach (var part in construct.Components)
            {
                if (construct.Type is ShaderType.Vector && part.Type is ShaderType.Vector vector)
                {
                    uint composite = Specialization(part);
                    for (uint i = 0; i < vector.Size; i++) components.Add(SpecializationResult(Op.CompositeExtract, vector.Component, composite, i));
                }
                else components.Add(Specialization(part));
            }
            if (construct.Type is ShaderType.Vector destination && components.Count == 1)
                components.AddRange(Enumerable.Repeat(components[0], destination.Size - 1));
            return SpecializationComposite(construct.Type, components.ToArray());
        }

        private uint SpecializationComposite(ShaderType type, params uint[] operands) => SpecializationInstruction(Op.SpecConstantComposite, type, operands);
        private uint SpecializationResult(Op operation, ShaderType type, params uint[] operands) => SpecializationInstruction(Op.SpecConstantOp, type, new[] { (uint)operation }.Concat(operands).ToArray());
        private uint SpecializationInstruction(Op instruction, ShaderType type, uint[] operands)
        {
            uint typeId = Type(type); string key = $"{(uint)instruction}:{typeId}:" + string.Join(',', operands);
            if (constantIds.TryGetValue(key, out uint known)) return known;
            uint id = Id(); declarations.Add(I(instruction, new[] { typeId, id }.Concat(operands).ToArray()));
            constantIds.Add(key, id); return id;
        }
    }
}
