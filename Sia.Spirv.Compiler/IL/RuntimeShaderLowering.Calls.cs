using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.IL;

internal sealed partial class RuntimeShaderLowering
{
    private void Call(CilInstruction instruction, Stack<Value> stack, Block body)
    {
        int at = instruction.Offset;
        var call = calls.Resolve(instruction.Operand.GetInt32(at));
        var args = new Value[call.ParameterCount];
        for (int a = args.Length-1; a >= 0; a--) args[a] = stack.Pop();
        Value? receiver = call.IsInstance && instruction.OpCode.Name != "newobj" ? stack.Pop() : null;
        void Push(Expression value) => stack.Push(Snapshot(value, body));
        if (call.DeclaringType == "Sia.Spirv.UInt3" && call.Name.StartsWith("get_", StringComparison.Ordinal)) {
            var vector = Read(receiver ?? throw Error(at, "Missing vector receiver."), body);
            uint component = call.Name switch { "get_X" => 0, "get_Y" => 1, "get_Z" => 2, _ => throw Error(at, "Invalid UInt3 component.") };
            Push(new Expression.Access(vector, Expression.U32(component), ShaderType.U32)); return;
        }
        if (call.Intrinsic is IntrinsicKind.GlobalInvocationId or IntrinsicKind.LocalInvocationId or IntrinsicKind.WorkGroupId) {
            if (!builtins.TryGetValue(call.Intrinsic.Value, out var expression)) {
                string name = call.Intrinsic.Value switch { IntrinsicKind.GlobalInvocationId => "global_invocation_id", IntrinsicKind.LocalInvocationId => "local_invocation_id", _ => "workgroup_id" };
                var type = new ShaderType.Vector(3, ShaderType.U32);
                string parameter = "sia_"+name;
                entry.Arguments.Add(new(parameter, type, new(Builtin: name)));
                expression = new Expression.Reference(parameter, type); builtins.Add(call.Intrinsic.Value, expression);
            }
            Push(expression); return;
        }
        if (call.Intrinsic == IntrinsicKind.Barrier) { body.Statements.Add(new Statement.Barrier(false, true)); return; }
        if (call.Intrinsic == IntrinsicKind.BufferIndex) {
            var array = receiver ?? throw Error(at, "Missing buffer receiver.");
            var type = ValueType(array.Expression) as ShaderType.Array ?? throw Error(at, "Buffer receiver is not an array.");
            var index = Read(args[0], body);
            stack.Push(new(new Expression.Access(array.Expression, index, Pointer(array.Expression, type.Element)), true)); return;
        }
        if (call.Intrinsic is IntrinsicKind.AtomicAdd or IntrinsicKind.AtomicExchange) {
            var array = receiver ?? throw Error(at, "Missing atomic buffer receiver.");
            var type = ValueType(array.Expression) as ShaderType.Array;
            var atomic = type?.Element as ShaderType.Atomic ?? throw Error(at, "Atomic receiver requires an integer writable array.");
            var pointer = new Expression.Access(array.Expression, Read(args[0], body), Pointer(array.Expression, atomic));
            var value = Convert(Read(args[1], body), atomic.Component, true);
            Push(new Expression.Call(call.Intrinsic == IntrinsicKind.AtomicAdd ? "atomicAdd" : "atomicExchange", [Address(pointer), value], atomic.Component, CallBinding.Builtin)); return;
        }
        if (call.Intrinsic == IntrinsicKind.MathGetComponent) {
            var vector = Read(receiver ?? throw Error(at, "Missing math receiver."), body);
            int component = "xyzw".IndexOf(call.Name[^1]);
            if (component < 0 || vector.Type is not ShaderType.Vector shape) throw Error(at, "Invalid math component.");
            Push(new Expression.Access(vector, Expression.U32((uint)component), shape.Component)); return;
        }
        var values = args.Select(a => Read(a, body)).ToArray();
        if (call.Intrinsic is IntrinsicKind.SetOutput or IntrinsicKind.SetFlatOutput or IntrinsicKind.SetPosition) {
            bool position = call.Intrinsic == IntrinsicKind.SetPosition;
            uint location = position ? uint.MaxValue : ConstantIndex(values[0], at);
            bool flat = call.Intrinsic == IntrinsicKind.SetFlatOutput;
            if (!outputs.TryGetValue((location, flat), out var target)) {
                var type = new ShaderType.Vector(4, ShaderType.F32);
                target = Place(Name("output"), type);
                outputs.Add((location, flat), target);
                entry.Body.Statements.Insert(0, new Statement.Declare(((Expression.Reference)target).Name, type, null));
            }
            Store(target, new Expression.Construct(ValueType(target), values.Skip(position ? 0 : 1).ToArray()), body); return;
        }
        if (call.Intrinsic == IntrinsicKind.Discard) { body.Statements.Add(new Statement.Kill()); return; }
        if (call.Intrinsic is IntrinsicKind.Texture2DLoad or IntrinsicKind.Texture2DArrayLoad or IntrinsicKind.Texture2DSampleLevel or IntrinsicKind.Texture2DArraySampleLevel) {
            var texture = Read(receiver ?? throw Error(at, "Missing texture receiver."), body);
            bool arrayed = call.Intrinsic is IntrinsicKind.Texture2DArrayLoad or IntrinsicKind.Texture2DArraySampleLevel;
            bool sampled = call.Intrinsic is IntrinsicKind.Texture2DSampleLevel or IntrinsicKind.Texture2DArraySampleLevel;
            int coordinate = sampled ? 1 : 0;
            var coordinateType = sampled ? ShaderType.F32 : ShaderType.I32;
            var coordinates = new Expression.Construct(new ShaderType.Vector(2, coordinateType), values.Skip(coordinate).Take(2).Select(v => Convert(v, coordinateType)).ToArray());
            var operands = new List<Expression> { texture };
            if (sampled) operands.Add(values[0]);
            operands.Add(coordinates);
            if (arrayed) operands.Add(Convert(values[coordinate+2], ShaderType.I32));
            int levelIndex = coordinate + (arrayed ? 3 : 2);
            operands.Add(levelIndex < values.Length-1 ? Convert(values[levelIndex], sampled ? ShaderType.F32 : ShaderType.I32) : Zero(sampled ? ShaderType.F32 : ShaderType.I32));
            var texel = new Expression.Call(sampled ? "textureSampleLevel" : "textureLoad", operands, new ShaderType.Vector(4, ShaderType.F32), CallBinding.Builtin);
            Push(new Expression.Access(texel, Expression.U32(ConstantIndex(values[^1], at)), ShaderType.F32)); return;
        }
        if (call.Intrinsic == IntrinsicKind.UnpackHalf) {
            Expression HalfValue(Expression value) => new Expression.Access(new Expression.Call("unpack2x16float", [Convert(value, ShaderType.U32, true)], new ShaderType.Vector(2, ShaderType.F32), CallBinding.Builtin), Expression.U32(0), ShaderType.F32);
            if (values[0].Type is ShaderType.Vector vector) Push(new Expression.Construct(new ShaderType.Vector(vector.Size, ShaderType.F32), Enumerable.Range(0, vector.Size).Select(i => HalfValue(new Expression.Access(values[0], Expression.U32((uint)i), vector.Component))).ToArray()));
            else Push(HalfValue(values[0]));
            return;
        }
        if (call.Intrinsic == IntrinsicKind.MathConstruct) {
            var type = instruction.OpCode.Name == "newobj" || call.Name == ".ctor" ? MathType(call.DeclaringType[9..]) : Type(call.Signature.ReturnType);
            var components = values;
            if (type is ShaderType.Matrix matrix && values.Length == matrix.Rows * matrix.Columns)
                components = Enumerable.Range(0, matrix.Columns).Select(column => (Expression)new Expression.Construct(new ShaderType.Vector(matrix.Rows, matrix.Component),
                    Enumerable.Range(0, matrix.Rows).Select(row => values[row*matrix.Columns+column]).ToArray())).ToArray();
            if (type is ShaderType.Vector vector) components = components.Select(v => v.Type is ShaderType.Scalar ? Convert(v, vector.Component) : v).ToArray();
            var value = new Expression.Construct(type, components);
            if (receiver is not null) Store(receiver.Expression, value, body); else Push(value);
            return;
        }
        if (call.Intrinsic is IntrinsicKind.MathAdd or IntrinsicKind.MathSubtract or IntrinsicKind.MathMultiply or IntrinsicKind.MathDivide or IntrinsicKind.MathMul) {
            string op = call.Intrinsic switch { IntrinsicKind.MathAdd => "+", IntrinsicKind.MathSubtract => "-", IntrinsicKind.MathDivide => "/", _ => "*" };
            var resultType = Type(call.Signature.ReturnType);
            if (call.Intrinsic != IntrinsicKind.MathMul && resultType is ShaderType.Matrix matrix) {
                var columnType = new ShaderType.Vector(matrix.Rows, matrix.Component);
                Expression Column(Expression value, int column) => value.Type is ShaderType.Matrix
                    ? new Expression.Access(value, Expression.U32((uint)column), columnType)
                    : new Expression.Construct(columnType, [Convert(value, matrix.Component)]);
                Push(new Expression.Construct(matrix, Enumerable.Range(0, matrix.Columns).Select(column =>
                    (Expression)new Expression.Binary(op, Column(values[0], column), Column(values[1], column), columnType)).ToArray()));
            }
            else Push(new Expression.Binary(op, values[0], values[1], resultType));
            return;
        }
        if (call.Intrinsic == IntrinsicKind.MathNegate) { Push(new Expression.Unary("-", values[0], values[0].Type)); return; }
        if (call.Intrinsic == IntrinsicKind.AsFloat) { Push(Convert(values[0], values[0].Type is ShaderType.Vector vector ? new ShaderType.Vector(vector.Size, ShaderType.F32) : ShaderType.F32, true)); return; }
        if (call.Intrinsic == IntrinsicKind.Select) {
            // Gpu.Select uses condition,true,false; math.select uses false,true,condition.
            var value = call.DeclaringType == "Sia.Math.math" ? new Expression.Select(values[2], values[1], values[0])
                : new Expression.Select(Truth(values[0]), values[1], values[2]);
            Push(value); return;
        }
        string? builtin = call.Intrinsic switch {
            IntrinsicKind.InverseSqrt => "inverseSqrt", IntrinsicKind.Sqrt => "sqrt", IntrinsicKind.Sin => "sin", IntrinsicKind.Cos => "cos",
            IntrinsicKind.Pow => "pow", IntrinsicKind.Abs => "abs", IntrinsicKind.MathDot => "dot", IntrinsicKind.MathCross => "cross",
            IntrinsicKind.MathNormalize => "normalize", IntrinsicKind.MathMin => "min", IntrinsicKind.MathMax => "max",
            IntrinsicKind.MathClamp => "clamp", IntrinsicKind.MathSaturate => "saturate", IntrinsicKind.MathReflect => "reflect",
            IntrinsicKind.MathAny => "any", IntrinsicKind.MathAll => "all", IntrinsicKind.MathTranspose => "transpose", _ => null
        };
        if (builtin is not null) { Push(new Expression.Call(builtin, values, Type(call.Signature.ReturnType), CallBinding.Builtin)); return; }
        if (call.Intrinsic is not null) throw Error(at, $"Runtime intrinsic {call.Intrinsic} requires further lowering.");
        if (MetadataTokens.EntityHandle(call.MetadataToken).Kind != HandleKind.MethodDefinition)
            throw Error(at, $"Runtime helper {call.DeclaringType}.{call.Name} needs explicit supported shader metadata.");
        if (call.Name == ".ctor" && structures.TryGetValue(call.DeclaringType, out var constructed)) {
            var constructor = new ShaderFunction(Name("constructor")) { ReturnType = constructed };
            var parameters = call.Signature.ParameterTypes.Select((type, i) => new FunctionArgument("p"+i, Type(type))).ToArray();
            constructor.Arguments.AddRange(parameters);
            var place = Place(Name("constructed"), constructed);
            constructor.Body.Statements.Add(new Statement.Declare(place.Name, constructed, null));
            var inputs = new[] { new Value(place, true) }.Concat(parameters.Select(p => new Value(new Expression.Reference(p.Name, p.Type)))).ToArray();
            Function(constructor, call.MetadataToken, inputs, returnOverride: new Expression.Load(place));
            module.Functions.Insert(0, constructor);
            var construction = new Expression.Call(constructor.Name, values, constructed, CallBinding.Function);
            if (receiver is not null) Store(receiver.Expression, construction, body); else Push(construction);
            return;
        }
        if (call.IsInstance) throw Error(at, "Instance helpers require a supported shader intrinsic or constructor.");
        if (!helpers.TryGetValue(call.MetadataToken, out var helper)) {
            if (!activeHelpers.Add(call.MetadataToken)) throw Error(at, "Recursive shader helpers are unsupported.");
            helper = new ShaderFunction(Name("helper")) { ReturnType = Type(call.Signature.ReturnType) };
            var parameters = call.Signature.ParameterTypes.Select((type, i) => new FunctionArgument("p"+i, Type(type))).ToArray();
            helper.Arguments.AddRange(parameters);
            var inputs = parameters.Select(p => new Value(new Expression.Reference(p.Name, p.Type), p.Type is ShaderType.Pointer)).ToArray();
            Function(helper, call.MetadataToken, inputs);
            activeHelpers.Remove(call.MetadataToken); helpers.Add(call.MetadataToken, helper); module.Functions.Insert(0, helper);
        }
        var invocation = new Expression.Call(helper.Name, values.Select((value, i) => Convert(value, helper.Arguments[i].Type, true)).ToArray(), helper.ReturnType, CallBinding.Function);
        if (helper.ReturnType is ShaderType.Void) body.Statements.Add(new Statement.Evaluate(invocation)); else Push(invocation);
    }
    private static uint ConstantIndex(Expression value, int offset) {
        if (!ConstantEvaluator.TryEvaluate(value, out var constant) || constant is not Expression.Literal literal)
            throw Error(offset, "Shader IO location and texture component must be constant.");
        return System.Convert.ToUInt32(literal.Value);
    }
}
