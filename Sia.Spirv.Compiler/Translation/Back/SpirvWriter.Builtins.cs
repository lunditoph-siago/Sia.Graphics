using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class SpirvWriter
{
    private sealed partial class Writer
    {
        private sealed partial class FunctionEmitter
        {
            private uint Call(Expression.Call call)
            {
                if (call.Binding != CallBinding.Builtin && owner.functions.TryGetValue(call.Function, out var function))
                {
                    owner.functionCalls[functionId].Add(function.Id);
                    // Target legalization expands calls that need pointer adaptation.
                    // Serialization must retain the supplied address identity.
                    return Result(Op.FunctionCall, call.Type, new uint[] { function.Id }.Concat(call.Arguments.Select(Value)).ToArray());
                }
                string name = call.Function;
                if (call.MemoryAccess is { } memory && name is "atomicLoad" or "atomicStore")
                {
                    uint pointer = Value(call.Arguments[0]);
                    if (name == "atomicLoad") return MemoryLoad(pointer, call.Type, memory);
                    MemoryStore(pointer, Value(call.Arguments[1]), memory); return 0;
                }
                if (name is "coopLoad" or "coopLoadT" or "coopStore" or "coopStoreT" or "coopMultiplyAdd") return Cooperative(call);
                if (name.StartsWith("spirvRayQuery", StringComparison.Ordinal))
                {
                    Op operation = Enum.Parse<Op>(name[5..]);
                    if (operation == Op.RayQueryGetIntersectionTriangleVertexPositionsKHR)
                    { owner.capabilities.Add(5391); owner.extensions.Add("SPV_KHR_ray_tracing_position_fetch"); }
                    uint[] raw = call.Arguments.Select(Value).ToArray();
                    if (call.Type is not ShaderType.Void) return Result(operation, call.Type, raw);
                    Add(operation, raw); return 0;
                }
                if (name.StartsWith("rayQuery", StringComparison.Ordinal) || name is "getCommittedHitVertexPositions" or "getCandidateHitVertexPositions") return RayQuery(call);
                if (name.StartsWith("texture", StringComparison.Ordinal) && name != "textureBarrier") return Texture(call);
                if (name.StartsWith("atomic", StringComparison.Ordinal) || name == "spirvAtomicCompareExchange") return Atomic(call);
                if (name == "arrayLength") return ArrayLength(call);
                uint[] args = call.Arguments.Select(Value).ToArray();
                if (SubgroupBuiltins.Contains(name)) return Subgroup(call, args);
                if (name is "pack4xI8" or "pack4xU8" or "pack4xI8Clamp" or "pack4xU8Clamp" or "unpack4xI8" or "unpack4xU8" or "dot4I8Packed" or "dot4U8Packed") return PackedInteger(name, call.Type, args);
                if (name is "storageBarrier" or "workgroupBarrier" or "textureBarrier" or "subgroupBarrier" or "workgroupUniformLoad")
                    throw owner.Error("Synchronization builtin was not legalized.", call.Span);
                if (name == "select")
                {
                    if (call.Type is ShaderType.Vector v && call.Arguments[2].Type is ShaderType.Scalar) args[2] = Splat(args[2], new ShaderType.Vector(v.Size, ShaderType.Bool));
                    return Result(Op.Select, call.Type, args[2], args[1], args[0]);
                }
                if (name is "all" or "any") return call.Arguments[0].Type is ShaderType.Scalar ? args[0] : Result(name == "all" ? Op.All : Op.Any, call.Type, args);
                if (name == "dot")
                {
                    if (Scalar(call.Type).Kind == ScalarKind.Float) return Result(Op.Dot, call.Type, args);
                    int count = ((ShaderType.Vector)call.Arguments[0].Type).Size; uint sum = owner.Null(call.Type);
                    for (int i = 0; i < count; i++) sum = Result(Op.IAdd, call.Type, sum, Result(Op.IMul, call.Type, Result(Op.CompositeExtract, call.Type, args[0], (uint)i), Result(Op.CompositeExtract, call.Type, args[1], (uint)i)));
                    return sum;
                }
                if (name == "outerProduct") return Result(Op.OuterProduct, call.Type, args);
                if (name == "transpose") return Result(Op.Transpose, call.Type, args);
                if (name == "quantizeToF16") return Result(Op.QuantizeToF16, call.Type, args);
                if (name is "isNan" or "isInf") return Result(name == "isNan" ? Op.IsNan : Op.IsInf, call.Type, args);
                if (name is "countOneBits" or "reverseBits") return Scalar(call.Type).Width == 4
                    ? Result(name == "countOneBits" ? Op.BitCount : Op.BitReverse, call.Type, args)
                    : CountOrReverseBits(name, call.Type, args[0]);
                if (name is "countLeadingZeros" or "countTrailingZeros" or "firstLeadingBit" or "firstTrailingBit" && Scalar(call.Type).Width != 4)
                    return BitPosition(name, call.Type, args[0]);
                if (name is "extractBits" or "insertBits") return BitField(name, call.Type, args);
                Op derivative = name switch
                {
                    "dpdx" => Op.DPdx, "dpdy" => Op.DPdy, "fwidth" => Op.Fwidth,
                    "dpdxFine" => Op.DPdxFine, "dpdyFine" => Op.DPdyFine, "fwidthFine" => Op.FwidthFine,
                    "dpdxCoarse" => Op.DPdxCoarse, "dpdyCoarse" => Op.DPdyCoarse, "fwidthCoarse" => Op.FwidthCoarse, _ => Op.Nop
                };
                if (derivative != Op.Nop) { if (derivative >= Op.DPdxFine) owner.capabilities.Add(51); return Result(derivative, call.Type, args); }
                if (name is "countLeadingZeros" or "countTrailingZeros")
                {
                    uint found = Glsl(name == "countLeadingZeros" ? 75u : 73u, call.Type, args);
                    ShaderType boolean = call.Type is ShaderType.Vector v ? new ShaderType.Vector(v.Size, ShaderType.Bool) : ShaderType.Bool;
                    uint zero = Result(Op.IEqual, boolean, args[0], owner.Null(call.Type));
                    int bits = Scalar(call.Type).Width * 8;
                    uint bitCount = LiteralSplat(Scalar(call.Type).Kind == ScalarKind.Uint ? (object)(uint)bits : bits, call.Type);
                    if (name == "countLeadingZeros") found = Result(Op.ISub, call.Type, LiteralSplat(Scalar(call.Type).Kind == ScalarKind.Uint ? (object)(uint)(bits - 1) : bits - 1, call.Type), found);
                    return Result(Op.Select, call.Type, zero, bitCount, found);
                }
                if (name == "saturate") return Glsl(43, call.Type, args[0], owner.Null(call.Type), One(call.Type));
                bool signed = call.Arguments.Count != 0 && ScalarForBuiltin(call.Arguments[0].Type)?.Kind == ScalarKind.Sint;
                bool integer = call.Arguments.Count != 0 && ScalarForBuiltin(call.Arguments[0].Type)?.Kind is ScalarKind.Sint or ScalarKind.Uint;
                if (name == "abs" && integer && !signed) return args[0];
                uint instruction = name switch
                {
                    "round" => 2, "trunc" => 3, "abs" => signed ? 5u : 4u, "sign" => signed ? 7u : 6u,
                    "floor" => 8, "ceil" => 9, "fract" => 10, "radians" => 11, "degrees" => 12,
                    "sin" => 13, "cos" => 14, "tan" => 15, "asin" => 16, "acos" => 17, "atan" => 18,
                    "sinh" => 19, "cosh" => 20, "tanh" => 21, "asinh" => 22, "acosh" => 23, "atanh" => 24,
                    "atan2" => 25, "pow" => 26, "exp" => 27, "log" => 28, "exp2" => 29, "log2" => 30,
                    "sqrt" => 31, "inverseSqrt" => 32, "determinant" => 33, "modf" => 36,
                    "min" => integer ? signed ? 39u : 38u : 37u, "max" => integer ? signed ? 42u : 41u : 40u,
                    "clamp" => integer ? signed ? 45u : 44u : 43u,
                    "mix" => 46, "step" => 48, "smoothstep" => 49, "fma" => 50, "frexp" => 52, "ldexp" => 53,
                    "pack4x8snorm" => 54, "pack4x8unorm" => 55, "pack2x16snorm" => 56, "pack2x16unorm" => 57, "pack2x16float" => 58,
                    "unpack2x16snorm" => 60, "unpack2x16unorm" => 61, "unpack2x16float" => 62, "unpack4x8snorm" => 63, "unpack4x8unorm" => 64,
                    "length" => 66, "distance" => 67, "cross" => 68, "normalize" => 69, "faceForward" => 70, "reflect" => 71, "refract" => 72,
                    "firstTrailingBit" => 73, "firstLeadingBit" => signed ? 74u : 75u,
                    _ => throw owner.Error($"Unsupported builtin '{name}'.", call.Span)
                };
                if (name == "mix" && call.Type is ShaderType.Vector vector && call.Arguments[2].Type is ShaderType.Scalar) args[2] = Splat(args[2], vector);
                return Glsl(instruction, call.Type, args);
            }
            private static ShaderType.Scalar? ScalarForBuiltin(ShaderType type) => type switch { ShaderType.Scalar s => s, ShaderType.Vector v => v.Component, ShaderType.Matrix m => m.Component, _ => null };

            private uint PackedInteger(string name, ShaderType resultType, uint[] args)
            {
                bool signed = name.Contains("I8", StringComparison.Ordinal);
                ShaderType.Scalar component = signed ? ShaderType.I32 : ShaderType.U32;
                uint Count(uint value) => owner.Constant(Expression.U32(value));
                uint Unpack(uint word, int index)
                {
                    if (!signed) return Result(Op.BitwiseAnd, ShaderType.U32, Result(Op.ShiftRightLogical, ShaderType.U32, word, Count((uint)index * 8)), Count(255));
                    uint shifted = Result(Op.ShiftLeftLogical, ShaderType.U32, word, Count(24u - (uint)index * 8));
                    return Result(Op.ShiftRightArithmetic, ShaderType.I32, Result(Op.Bitcast, ShaderType.I32, shifted), Count(24));
                }
                if (name.StartsWith("unpack", StringComparison.Ordinal))
                    return Result(Op.CompositeConstruct, resultType, Enumerable.Range(0, 4).Select(i => Unpack(args[0], i)).ToArray());
                if (name.StartsWith("dot", StringComparison.Ordinal))
                {
                    uint sum = owner.Null(component);
                    for (int i = 0; i < 4; i++) sum = Result(Op.IAdd, component, sum, Result(Op.IMul, component, Unpack(args[0], i), Unpack(args[1], i)));
                    return sum;
                }
                uint packed = Count(0);
                for (int i = 0; i < 4; i++)
                {
                    uint value = Result(Op.CompositeExtract, component, args[0], (uint)i);
                    if (name.EndsWith("Clamp", StringComparison.Ordinal))
                        value = Glsl(signed ? 45u : 44u, component, value, owner.Constant(signed ? Expression.I32(-128) : Expression.U32(0)), owner.Constant(signed ? Expression.I32(127) : Expression.U32(255)));
                    if (signed) value = Result(Op.Bitcast, ShaderType.U32, value);
                    value = Result(Op.BitwiseAnd, ShaderType.U32, value, Count(255));
                    packed = Result(Op.BitwiseOr, ShaderType.U32, packed, Result(Op.ShiftLeftLogical, ShaderType.U32, value, Count((uint)i * 8)));
                }
                return packed;
            }
            private uint Glsl(uint instruction, ShaderType type, params uint[] operands) => Result(Op.ExtInst, type,
                new uint[] { owner.GlslImport(), instruction }.Concat(operands).ToArray());

            private uint ArrayLength(Expression.Call call)
            {
                owner.capabilities.Add(1);
                if (call.Arguments is [Expression.Unary { Operator: "&", Operand: Expression.Member member }])
                    return Result(Op.ArrayLength, call.Type, Place(member.Base), MemberIndex(DataType(member.Base.Type), member.Name));
                if (call.Arguments is [Expression.Unary { Operator: "&", Operand: Expression.Reference reference }])
                {
                    var symbol = Lookup(reference.Name);
                    if (symbol.BufferWrapper) return Result(Op.ArrayLength, call.Type, symbol.Id, 0);
                }
                throw owner.Error("arrayLength needs an address of a storage buffer's runtime-array member.", call.Span);
            }

            private uint Atomic(Expression.Call call)
            {
                if (call.Arguments[0].Type is not ShaderType.Pointer pointer || pointer.Base is not ShaderType.Atomic atomic) throw owner.Error("Atomic builtin needs an atomic pointer.");
                uint address = Value(call.Arguments[0]);
                var (scope, semantics, unequal) = AtomicOperands(call);
                uint[] args = call.Arguments.Skip(1).Select(Value).ToArray();
                if (call.Function == "atomicStore") { Add(Op.AtomicStore, address, scope, semantics, args[0]); return 0; }
                if (call.Function == "atomicCompareExchangeWeak")
                {
                    uint old = Result(Op.AtomicCompareExchange, atomic.Component, address, scope, semantics, unequal, args[1], args[0]);
                    uint exchanged = Result(Op.IEqual, ShaderType.Bool, old, args[0]);
                    return Result(Op.CompositeConstruct, call.Type, old, exchanged);
                }
                if (call.Function == "spirvAtomicCompareExchange")
                    return Result(Op.AtomicCompareExchange, atomic.Component, address, scope, semantics, unequal, args[1], args[0]);
                if (atomic.Component.Kind == ScalarKind.Float && call.Function is "atomicAdd" or "atomicSub")
                {
                    uint value = call.Function == "atomicSub" ? Result(Op.FNegate, atomic.Component, args[0]) : args[0];
                    return Result(Op.AtomicFAddEXT, call.Type, address, scope, semantics, value);
                }
                Op op = call.Function switch
                {
                    "atomicLoad" => Op.AtomicLoad, "atomicExchange" => Op.AtomicExchange, "atomicAdd" => Op.AtomicIAdd, "atomicSub" => Op.AtomicISub,
                    "atomicMin" => atomic.Component.Kind == ScalarKind.Sint ? Op.AtomicSMin : Op.AtomicUMin,
                    "atomicMax" => atomic.Component.Kind == ScalarKind.Sint ? Op.AtomicSMax : Op.AtomicUMax,
                    "atomicAnd" => Op.AtomicAnd, "atomicOr" => Op.AtomicOr, "atomicXor" => Op.AtomicXor, _ => throw owner.Error("Unsupported atomic operation.")
                };
                return Result(op, call.Type, new uint[] { address, scope, semantics }.Concat(args).ToArray());
            }
            private (uint Scope, uint Semantics, uint Unequal) AtomicOperands(Expression.Call call)
            {
                var memory = call.AtomicMemory ?? throw owner.Error("Atomic operands were not prepared.", call.Span);
                uint scope = memory.Scope;
                if (scope == 1) owner.usesDeviceScope = true;
                uint semantics = memory.Semantics;
                uint unequal = memory.UnequalSemantics ?? semantics;
                if (((semantics | (memory.UnequalSemantics ?? 0)) & 16) != 0) owner.usesSequentialMemoryOrder = true;
                return (owner.Constant(Expression.U32(scope)), owner.Constant(Expression.U32(semantics)),
                    owner.Constant(Expression.U32(unequal)));
            }
        }
    }
}
