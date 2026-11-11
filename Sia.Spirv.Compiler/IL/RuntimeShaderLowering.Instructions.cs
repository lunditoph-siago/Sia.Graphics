using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.IL;

internal sealed partial class RuntimeShaderLowering
{
    private void Instruction(CilInstruction instruction, Stack<Value> stack, Value[] arguments, Value[] locals, Block body,
        Action<int, Value, Block, int> storeLocal)
    {
        string op = instruction.OpCode.Name!; int at = instruction.Offset;
        int Index(string prefix) => op.Length > prefix.Length+1 && int.TryParse(op[(prefix.Length+1)..], out int index) ? index : instruction.Operand.GetInt32(at);
        Expression Pop() => Read(stack.Pop(), body);
        void Push(Expression expression) => stack.Push(Snapshot(expression, body));
        if (op.StartsWith("ldarg", StringComparison.Ordinal)) {
            int index = Index(op.StartsWith("ldarga", StringComparison.Ordinal) ? "ldarga" : "ldarg");
            var value = arguments[index];
            if (op.StartsWith("ldarga", StringComparison.Ordinal)) {
                if (!value.Place && value.Expression.Type is not (ShaderType.Image or ShaderType.Sampler)) {
                    var slot = Place(Name("argument"), value.Expression.Type);
                    body.Statements.Add(new Statement.Declare(slot.Name, value.Expression.Type, value.Expression));
                    value = arguments[index] = new(slot, true, true);
                }
                stack.Push(value);
            }
            else if (value.ArgumentSlot) Push(Read(value, body));
            else stack.Push(value);
            return;
        }
        if (op.StartsWith("starg", StringComparison.Ordinal)) {
            var target = arguments[Index("starg")];
            if (!target.ArgumentSlot) throw Error(at, "Assigning resource handles or by-reference arguments is unsupported.");
            Store(target.Expression, Pop(), body); return;
        }
        if (op.StartsWith("ldloc", StringComparison.Ordinal)) {
            int index = Index(op.StartsWith("ldloca", StringComparison.Ordinal) ? "ldloca" : "ldloc");
            if (op.StartsWith("ldloca", StringComparison.Ordinal)) stack.Push(locals[index]);
            else {
                var value = Snapshot(new Expression.Load(locals[index].Expression), body);
                stack.Push(value with { Place = value.Expression.Type is ShaderType.Pointer });
            }
            return;
        }
        if (op.StartsWith("stloc", StringComparison.Ordinal)) { storeLocal(Index("stloc"), stack.Pop(), body, at); return; }
        if (op.StartsWith("ldc.i4", StringComparison.Ordinal)) {
            int value = op == "ldc.i4.m1" ? -1 : op is "ldc.i4" or "ldc.i4.s" ? instruction.Operand.GetInt32(at) : op[^1]-'0';
            stack.Push(new(Expression.I32(value))); return;
        }
        if (op == "ldc.r4") { stack.Push(new(new Expression.Literal(instruction.Operand.GetSingle(at), ShaderType.F32))); return; }
        if (op is "call" or "callvirt" or "newobj") { Call(instruction, stack, body); return; }
        if (op.StartsWith("ldind", StringComparison.Ordinal) || op == "ldobj") { Push(Read(stack.Pop(), body)); return; }
        if (op.StartsWith("stind", StringComparison.Ordinal) || op == "stobj") { var value = Pop(); var pointer = stack.Pop(); if (!pointer.Place) throw Error(at, "Store requires a CIL place."); Store(pointer.Expression, value, body); return; }
        if (op is "ldfld" or "ldflda" or "stfld") {
            Expression? stored = op == "stfld" ? Pop() : null;
            var value = stack.Pop();
            var handle = MetadataTokens.EntityHandle(instruction.Operand.GetInt32(at));
            string field = handle.Kind == HandleKind.FieldDefinition ? metadata.GetString(metadata.GetFieldDefinition((FieldDefinitionHandle)handle).Name)
                : metadata.GetString(metadata.GetMemberReference((MemberReferenceHandle)handle).Name);
            Expression projection;
            if (ValueType(value.Expression) is ShaderType.Structure structure) {
                var member = structure.Members.SingleOrDefault(m => m.Name == field) ?? throw Error(at, $"Unknown structure field {field}.");
                projection = new Expression.Member(value.Expression, field, value.Place ? Pointer(value.Expression, member.Type) : member.Type);
            }
            else {
                var vector = ValueType(value.Expression) as ShaderType.Vector ?? throw Error(at, $"Field {field} needs structure projection lowering.");
                int component = field switch { "x" or "X" => 0, "y" or "Y" => 1, "z" or "Z" => 2, "w" or "W" => 3, _ => throw Error(at, $"Unsupported vector field {field}.") };
                projection = new Expression.Access(value.Expression, Expression.U32((uint)component), value.Place ? Pointer(value.Expression, vector.Component) : vector.Component);
            }
            if (stored is not null) { Store(projection, stored, body); return; }
            if (op == "ldflda") { if (!value.Place) throw Error(at, "Field address needs a mutable place."); stack.Push(new(projection, true)); }
            else Push(value.Place ? new Expression.Load(projection) : projection);
            return;
        }
        if (op == "initobj") { var place = stack.Pop(); Store(place.Expression, Zero(ValueType(place.Expression)), body); return; }
        if (op is "conv.i4" or "conv.u4" or "conv.r4" or "conv.r.un") {
            Push(LowerConversion(op, Pop()));
            return;
        }
        if (op is "ceq" or "cgt" or "cgt.un" or "clt" or "clt.un") {
            var right = Pop();
            var left = Pop();
            var comparison = op switch {
                "ceq" => "==",
                "cgt" or "cgt.un" => ">",
                _ => "<"
            };
            Push(Compare(comparison, left, right, op.EndsWith(".un", StringComparison.Ordinal)));
            return;
        }
        if (BinaryOperator(op) is { } symbol) {
            var right = Pop();
            var left = Pop();
            Push(LowerBinary(op, symbol, left, right, at));
            return;
        }
        if (op is "neg" or "not") { var value = Pop(); Push(new Expression.Unary(op == "neg" ? "-" : value.Type == ShaderType.Bool ? "!" : "~", value, value.Type)); return; }
        if (op == "dup") { stack.Push(stack.Peek()); return; }
        if (op is "pop" or "nop") { if (op == "pop") stack.Pop(); return; }
        throw Error(at, $"Unsupported CIL instruction {op}.");
    }

    private static Expression LowerConversion(string op, Expression value)
        => op switch {
            "conv.r.un" => Convert(Convert(value, ShaderType.U32, bitcast: true), ShaderType.F32),
            "conv.r4" => Convert(value, ShaderType.F32),
            _ => Convert(value, op == "conv.u4" ? ShaderType.U32 : ShaderType.I32,
                bitcast: value.Type is ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint })
        };

    private static string? BinaryOperator(string op)
        => op switch {
            "add" => "+",
            "sub" => "-",
            "mul" => "*",
            "div" or "div.un" => "/",
            "rem" or "rem.un" => "%",
            "and" => "&",
            "or" => "|",
            "xor" => "^",
            "shl" => "<<",
            "shr" or "shr.un" => ">>",
            _ => null
        };

    private static Expression LowerBinary(string op, string symbol, Expression left, Expression right, int offset)
    {
        if (op.EndsWith(".un", StringComparison.Ordinal)) {
            left = Convert(left, ShaderType.U32, bitcast: true);
        }
        if (op is "shl" or "shr" or "shr.un") {
            var shift = new Expression.Binary("&", Convert(right, ShaderType.U32, bitcast: true),
                Expression.U32(31), ShaderType.U32);
            return new Expression.Binary(symbol, left, shift, left.Type);
        }

        if (left.Type != right.Type) {
            var type = (left.Type, right.Type) switch {
                var (a, b) when a == ShaderType.F32 || b == ShaderType.F32 => ShaderType.F32,
                var (a, b) when a == ShaderType.U32 || b == ShaderType.U32 => ShaderType.U32,
                _ => left.Type
            };
            left = Convert(left, type, bitcast: type != ShaderType.F32);
            right = Convert(right, type, bitcast: type != ShaderType.F32);
        }
        if (left.Type == ShaderType.Bool) {
            symbol = op switch {
                "and" => "&&",
                "or" => "||",
                "xor" => "!=",
                _ => throw Error(offset, "Invalid Boolean arithmetic.")
            };
        }
        return new Expression.Binary(symbol, left, right, left.Type);
    }

    private void Store(Expression pointer, Expression value, Block body) {
        var type = ValueType(pointer);
        if (type is ShaderType.Atomic atomic) body.Statements.Add(new Statement.Evaluate(new Expression.Call("atomicStore", [Address(pointer), Convert(value, atomic.Component, true)], new ShaderType.Void(), CallBinding.Builtin)));
        else body.Statements.Add(new Statement.Store(pointer, Convert(value, type, true)));
    }
    private static ShaderType.Pointer Pointer(Expression place, ShaderType type) => place.Type is ShaderType.Pointer p ? p with { Base = type }
        : place switch { Expression.Member m => Pointer(m.Base, type), Expression.Access a => Pointer(a.Base, type), _ => new(type, AddressSpace.Function) };
    private Expression Branch(CilInstruction instruction, Stack<Value> stack, Block body) {
        string op = instruction.OpCode.Name!;
        var right = Read(stack.Pop(), body);
        if (op.StartsWith("brtrue", StringComparison.Ordinal)) return Truth(right);
        if (op.StartsWith("brfalse", StringComparison.Ordinal)) return new Expression.Unary("!", Truth(right), ShaderType.Bool);
        var left = Read(stack.Pop(), body);
        string symbol = op.Split('.')[0] switch { "beq" => "==", "bne" => "!=", "blt" => "<", "ble" => "<=", "bgt" => ">", "bge" => ">=", _ => throw Error(instruction.Offset, $"Unsupported conditional branch {op}.") };
        return Compare(symbol, left, right, op.Contains(".un", StringComparison.Ordinal));
    }
    private static Expression Truth(Expression value) => value.Type == ShaderType.Bool ? value : new Expression.Binary("!=", value, Zero(value.Type), ShaderType.Bool);
    private static Expression Compare(string symbol, Expression left, Expression right, bool unsigned) {
        ShaderType type = left.Type == ShaderType.F32 || right.Type == ShaderType.F32 ? ShaderType.F32 : unsigned ? ShaderType.U32 : left.Type;
        left = Convert(left, type, type != ShaderType.F32); right = Convert(right, type, type != ShaderType.F32);
        Expression result = new Expression.Binary(symbol, left, right, ShaderType.Bool);
        if (unsigned && type == ShaderType.F32) result = new Expression.Binary("||", result,
            new Expression.Binary("||", new Expression.Call("isNan", [left], ShaderType.Bool, CallBinding.Builtin), new Expression.Call("isNan", [right], ShaderType.Bool, CallBinding.Builtin), ShaderType.Bool), ShaderType.Bool);
        return result;
    }
}
