using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class SpirvWriter
{
    private sealed partial class Writer
    {
        private void EmitFunction(uint id, ShaderFunction function)
        {
            functionGlobalUses[id] = []; functionCalls[id] = [];
            new FunctionEmitter(this, id, function).Emit();
        }

        private sealed partial class FunctionEmitter(Writer owner, uint functionId, ShaderFunction function)
        {
            private readonly List<SpirvInstruction> code = [], variables = [];
            private readonly Stack<Dictionary<string, Symbol>> scopes = new();
            private readonly Stack<uint> breakTargets = [], continueTargets = [];
            private bool terminated;
            private void Add(Op op, params uint[] args) => code.Add(I(op, args));
            private uint Result(Op op, ShaderType type, params uint[] args)
            {
                uint id = owner.Id(); Add(op, new uint[] { owner.Type(type), id }.Concat(args).ToArray()); return id;
            }
            private void Label(uint label) { Add(Op.Label, label); terminated = false; }
            private void Branch(uint label) { if (!terminated) { Add(Op.Branch, label); terminated = true; } }
            private Symbol Lookup(string name)
            {
                foreach (var scope in scopes) if (scope.TryGetValue(name, out var symbol)) return symbol;
                if (!owner.globals.TryGetValue(name, out var global)) throw owner.Error($"Undefined reference '{name}'.");
                if (owner.moduleVariableIds.Contains(global.Id)) owner.functionGlobalUses[functionId].Add(global.Id);
                return global;
            }
            private uint Variable(ShaderType type, string? name = null)
            {
                uint id = owner.Id(); variables.Add(I(Op.Variable, owner.Pointer(owner.Type(type), 7), id, 7));
                if (name is not null) owner.Name(id, name); return id;
            }
            public void Emit()
            {
                if (owner.physicalLayout.ControlFlow.TryGetValue(function.Name, out var graph)) { EmitCanonical(graph); return; }
                uint signature = owner.FunctionType(function.ReturnType, function.Arguments.Select(a => a.Type));
                var header = new List<SpirvInstruction> { I(Op.Function, owner.Type(function.ReturnType), functionId, 0, signature) };
                owner.Name(functionId, function.Name); scopes.Push(new(StringComparer.Ordinal));
                foreach (var argument in function.Arguments)
                {
                    uint id = owner.Id(); header.Add(I(Op.FunctionParameter, owner.Type(argument.Type), id));
                    scopes.Peek().Add(argument.Name, new(id, argument.Type, false)); owner.Name(id, argument.Name);
                }
                uint entry = owner.Id();
                Body(function.Body, false);
                if (!terminated)
                {
                    if (function.ReturnType is ShaderType.Void) Add(Op.Return);
                    else Add(Op.Unreachable); // A validator rejects a reachable missing return.
                }
                owner.bodies.AddRange(header); owner.bodies.Add(I(Op.Label, entry));
                owner.bodies.AddRange(variables); owner.bodies.AddRange(code); owner.bodies.Add(I(Op.FunctionEnd));
                scopes.Pop();
            }

            private void Body(Block block, bool scope = true)
            {
                if (scope) scopes.Push(new(StringComparer.Ordinal));
                foreach (var statement in block.Statements)
                {
                    // The module validator checks unreachable source too; it has no emitted instructions.
                    if (terminated) break;
                    switch (statement)
                    {
                        case Statement.Nested n: Body(n.Body); break;
                        case Statement.Declare d:
                            uint id;
                            if (d.Mutable)
                            {
                                id = Variable(d.Type, d.Name);
                                if (d.Initialize && d.Type is not ShaderType.RayQuery)
                                {
                                    uint initializer = d.Initializer is null ? owner.Null(d.Type) : Value(d.Initializer);
                                    Add(Op.Store, id, initializer);
                                }
                            }
                            else id = Value(d.Initializer ?? throw owner.Error("let has no initializer."));
                            if (!scopes.Peek().TryAdd(d.Name, new(id, d.Type, d.Mutable))) throw owner.Error("Duplicate local declaration.");
                            break;
                        case Statement.Store s:
                            uint pointer = Place(s.Target); uint value = Value(s.Value);
                            MemoryStore(pointer, value, s.MemoryAccess); break;
                        case Statement.Evaluate e: Value(e.Value); break;
                        case Statement.MeshStore store:
                            uint output = owner.MeshOutput(store.Field);
                            owner.functionGlobalUses[functionId].Add(output);
                            uint outputIndex = Value(store.Index), outputValue = Value(store.Value);
                            uint outputPointer = owner.Id();
                            Add(Op.AccessChain, owner.Pointer(owner.Type(store.Field.Type), 3), outputPointer, output, outputIndex);
                            Add(Op.Store, outputPointer, outputValue); break;
                        case Statement.MeshSetOutputs counts: Add(Op.SetMeshOutputsEXT, Value(counts.Vertices), Value(counts.Primitives)); break;
                        case Statement.TaskDispatch dispatch: TaskDispatch(dispatch); break;
                        case Statement.If i: If(i); break;
                        case Statement.Loop l: Loop(l); break;
                        case Statement.Switch s: Switch(s); break;
                        case Statement.Return r:
                            if (r.Value is null) Add(Op.Return); else Add(Op.ReturnValue, Value(r.Value));
                            terminated = true; break;
                        case Statement.Break:
                            if (!breakTargets.TryPeek(out uint end)) throw owner.Error("break outside a loop or switch.");
                            Branch(end); break;
                        case Statement.Continue:
                            if (!continueTargets.TryPeek(out uint next)) throw owner.Error("continue outside a loop.");
                            Branch(next); break;
                        case Statement.Kill:
                            owner.capabilities.Add(5379);
                            if (owner.OutputVersion < 0x10600) owner.extensions.Add("SPV_EXT_demote_to_helper_invocation");
                            Add(Op.DemoteToHelperInvocation); break;
                        case Statement.InvocationKill kill:
                            if (kill.ExplicitTermination && owner.OutputVersion < 0x10600) owner.extensions.Add("SPV_KHR_terminate_invocation");
                            Add(kill.ExplicitTermination ? Op.TerminateInvocation : Op.Kill); terminated = true; break;
                        case Statement.Unreachable: Add(Op.Unreachable); terminated = true; break;
                        case Statement.Barrier b: NativeBarrier(b.NativeMemory ?? throw owner.Error("Control barrier operands were not prepared.", b.Span)); break;
                        case Statement.MemoryBarrier b: NativeBarrier(b.NativeMemory ?? throw owner.Error("Memory barrier operands were not prepared.", b.Span)); break;
                        default: throw owner.Error("Unsupported statement.", statement.Span);
                    }
                }
                if (scope) scopes.Pop();
            }

            private void If(Statement.If statement)
            {
                uint condition = Value(statement.Condition), accept = owner.Id(), reject = owner.Id(), merge = owner.Id();
                Add(Op.SelectionMerge, merge, 0); Add(Op.BranchConditional, condition, accept, reject);
                Label(accept); Body(statement.Accept); bool acceptTerminates = terminated; Branch(merge);
                Label(reject); Body(statement.Reject); bool rejectTerminates = terminated; Branch(merge);
                Label(merge);
                if (acceptTerminates && rejectTerminates) { Add(Op.Unreachable); terminated = true; }
            }
            private void Loop(Statement.Loop statement)
            {
                uint header = owner.Id(), body = owner.Id(), continuing = owner.Id(), merge = owner.Id();
                Branch(header); Label(header); Add(Op.LoopMerge, merge, continuing, 0); Add(Op.Branch, body);
                breakTargets.Push(merge); continueTargets.Push(continuing); scopes.Push(new(StringComparer.Ordinal));
                Label(body); Body(statement.Body, false); Branch(continuing);
                Label(continuing); scopes.Push(new(StringComparer.Ordinal)); Body(statement.Continuing, false);
                if (!terminated)
                {
                    if (statement.BreakIf is null) Branch(header);
                    else { Add(Op.BranchConditional, Value(statement.BreakIf), merge, header); terminated = true; }
                }
                scopes.Pop(); scopes.Pop(); continueTargets.Pop(); breakTargets.Pop(); Label(merge);
            }
            private void Switch(Statement.Switch statement)
            {
                uint selector = Value(statement.Selector), merge = owner.Id();
                uint[] labels = statement.Cases.Select(_ => owner.Id()).ToArray();
                int defaultIndex = statement.Cases.ToList().FindIndex(c => c.IsDefault);
                uint defaultLabel = defaultIndex >= 0 ? labels[defaultIndex] : merge;
                var operands = new List<uint> { selector, defaultLabel };
                for (int i = 0; i < labels.Length; i++) foreach (var value in statement.Cases[i].Values)
                {
                    uint literal = value.Value switch { uint u => u, int n => unchecked((uint)n), _ => throw owner.Error("Switch needs 32-bit integer cases.") };
                    operands.Add(literal); operands.Add(labels[i]);
                }
                Add(Op.SelectionMerge, merge, 0); Add(Op.Switch, operands.ToArray()); breakTargets.Push(merge);
                for (int i = 0; i < labels.Length; i++) { Label(labels[i]); Body(statement.Cases[i].Body); Branch(merge); }
                breakTargets.Pop(); Label(merge);
            }

            private bool IsPlace(Expression expression) => expression switch
            {
                Expression.Reference r => Lookup(r.Name).Place,
                Expression.Unary { Operator: "*" } => true,
                Expression.Access a => IsPlace(a.Base), Expression.Member m => IsPlace(m.Base), _ => false
            };
            private static ShaderType DataType(ShaderType type) => type is ShaderType.Pointer p ? p.Base : type;
            private uint Place(Expression expression)
            {
                switch (expression)
                {
                    case Expression.Reference reference:
                        var symbol = Lookup(reference.Name);
                        if (!symbol.Place) throw owner.Error("Value is not an addressable place.", expression.Span);
                        return symbol.BufferWrapper ? Result(Op.AccessChain, new ShaderType.Pointer(symbol.Type, Address(symbol.Storage)), symbol.Id, owner.Constant(Expression.U32(0))) : symbol.Id;
                    case Expression.Unary { Operator: "*" } dereference: return Value(dereference.Operand);
                    case Expression.Access access:
                        uint container = Place(access.Base), index = Value(access.Index);
                        if (DataType(access.Base.Type) is ShaderType.BindingArray bindingArray)
                        {
                            uint bindingPointer = Result(Op.AccessChain, PointerType(access.Type, access.Base), container, index);
                            // Conservative until uniformity analysis proves the index dynamically uniform.
                            ShaderType resource = bindingArray.Element is ShaderType.Image or ShaderType.Sampler or ShaderType.AccelerationStructure ? bindingArray.Element : PointerType(bindingArray.Element, access.Base);
                            if (!ConstantEvaluator.TryEvaluate(access.Index, out _)) { owner.NonUniform(index, resource); owner.NonUniform(bindingPointer, resource); }
                            return bindingPointer;
                        }
                        uint elementPointer = Result(Op.AccessChain, PointerType(access.Type, access.Base), container, index);
                        if (owner.nonUniformResources.TryGetValue(container, out var arrayResource)) owner.NonUniform(elementPointer, arrayResource);
                        return elementPointer;
                    case Expression.Member member:
                        uint structure = Place(member.Base), offset = MemberIndex(DataType(member.Base.Type), member.Name);
                        uint memberPointer = Result(Op.AccessChain, PointerType(member.Type, member.Base), structure, owner.Constant(Expression.U32(offset)));
                        if (owner.nonUniformResources.TryGetValue(structure, out var structResource)) owner.NonUniform(memberPointer, structResource);
                        return memberPointer;
                    default: throw owner.Error("Expression is not an addressable place.", expression.Span);
                }
            }
            private ShaderType.Pointer PointerType(ShaderType type, Expression parent)
            {
                if (type is ShaderType.Pointer p) return p;
                if (parent.Type is ShaderType.Pointer pointer) return pointer with { Base = type };
                if (parent is Expression.Reference reference) return new(type, Address(Lookup(reference.Name).Storage));
                if (parent is Expression.Access a) return PointerType(type, a.Base);
                if (parent is Expression.Member m) return PointerType(type, m.Base);
                return new(type, AddressSpace.Function);
            }
            private AddressSpace Address(uint storage) => storage switch
            {
                0 => AddressSpace.Handle, 2 => AddressSpace.Uniform, 4 => AddressSpace.Workgroup,
                6 => AddressSpace.Private, 7 => AddressSpace.Function, 9 => AddressSpace.Immediate, 12 => AddressSpace.Storage,
                _ => throw owner.Error("Unsupported pointer storage.")
            };
            private uint MemberIndex(ShaderType type, string name)
            {
                if (type is not ShaderType.Structure structure) throw owner.Error("Member access needs a structure.");
                int index = structure.Members.ToList().FindIndex(m => m.Name == name);
                return index >= 0 ? (uint)index : throw owner.Error("Unknown structure member.");
            }

            private uint Value(Expression expression)
            {
                // Preserve aggregate zero as OpConstantNull. Expanding it to a fixed
                // list of components would bind a subsequently specialized array
                // to its default length instead of retaining its zero-value meaning.
                if (expression is Expression.Construct { Components.Count: 0 } zero && zero.Type is ShaderType.Array or ShaderType.Structure)
                    return owner.Null(zero.Type);
                if (ConstantEvaluator.TryEvaluateRuntime(expression, out var constant)) return owner.Constant(constant);
                switch (expression)
                {
                    case Expression.HelperInvocation:
                        owner.capabilities.Add(5379);
                        if (owner.OutputVersion < 0x10600) owner.extensions.Add("SPV_EXT_demote_to_helper_invocation");
                        return Result(Op.IsHelperInvocation, ShaderType.Bool);
                    case Expression.Reference r:
                        var symbol = Lookup(r.Name);
                        return symbol.Place ? MemoryLoad(r, symbol.Type) : symbol.Id;
                    case Expression.Load load:
                        return MemoryLoad(load.Pointer, load.Type, load.MemoryAccess);
                    case Expression.Unary { Operator: "&" } address:
                        return Place(address.Operand);
                    case Expression.Unary { Operator: "*" } dereference:
                        return MemoryLoad(Value(dereference.Operand), DataType(dereference.Type), null);
                    case Expression.Unary unary:
                        if (unary.Type is ShaderType.Matrix unaryMatrix)
                        {
                            uint matrixValue = Value(unary.Operand);
                            var column = new ShaderType.Vector(unaryMatrix.Rows, unaryMatrix.Component);
                            return Result(Op.CompositeConstruct, unary.Type, Enumerable.Range(0, unaryMatrix.Columns).Select(i => Result(Op.FNegate, column, Result(Op.CompositeExtract, column, matrixValue, (uint)i))).ToArray());
                        }
                        return Result(unary.Operator switch
                        {
                            "!" => Op.LogicalNot, "~" => Op.Not, "-" => Scalar(unary.Type).Kind == ScalarKind.Float ? Op.FNegate : Op.SNegate,
                            _ => throw owner.Error("Unknown unary operation.")
                        }, unary.Type, Value(unary.Operand));
                    case Expression.Binary binary: return Binary(binary);
                    case Expression.Convert conversion: return Convert(conversion);
                    case Expression.Construct construct:
                        if (construct.Components.Count == 0) return owner.Null(construct.Type);
                        var elements = construct.Components.Select(Value).ToArray();
                        if (construct.Type is ShaderType.Vector vector)
                        {
                            var components = new List<uint>();
                            for (int i = 0; i < elements.Length; i++)
                            {
                                if (construct.Components[i].Type is ShaderType.Vector part)
                                    for (int j = 0; j < part.Size; j++) components.Add(Result(Op.CompositeExtract, part.Component, elements[i], (uint)j));
                                else components.Add(elements[i]);
                            }
                            if (components.Count == 1) components = Enumerable.Repeat(components[0], vector.Size).ToList();
                            elements = components.ToArray();
                        }
                        return Result(Op.CompositeConstruct, construct.Type, elements);
                    case Expression.Access access:
                        if (DataType(access.Base.Type) is ShaderType.BindingArray { Element: ShaderType.Image or ShaderType.Sampler or ShaderType.AccelerationStructure } bindingArray)
                        {
                            uint bindingPointer = Place(access), bindingValue = Result(Op.Load, bindingArray.Element, bindingPointer);
                            if (owner.nonUniformValues.Contains(bindingPointer)) owner.NonUniform(bindingValue, bindingArray.Element);
                            return bindingValue;
                        }
                        if (IsPlace(access)) return MemoryLoad(access, DataType(access.Type));
                        uint aggregate = Value(access.Base), index = Value(access.Index);
                        if (access.Base.Type is ShaderType.Vector) return Result(Op.VectorExtractDynamic, access.Type, aggregate, index);
                        if (ConstantEvaluator.TryEvaluate(access.Index, out var indexValue) && indexValue is Expression.Literal indexLiteral)
                            return Result(Op.CompositeExtract, access.Type, aggregate, System.Convert.ToUInt32(indexLiteral.Value));
                        uint scratch = Variable(access.Base.Type); Add(Op.Store, scratch, aggregate);
                        uint ptr = Result(Op.AccessChain, new ShaderType.Pointer(access.Type, AddressSpace.Function), scratch, index);
                        return Result(Op.Load, access.Type, ptr);
                    case Expression.Member member:
                        if (IsPlace(member)) return MemoryLoad(member, DataType(member.Type));
                        return Result(Op.CompositeExtract, member.Type, Value(member.Base), MemberIndex(member.Base.Type, member.Name));
                    case Expression.Swizzle swizzle:
                        uint source = Value(swizzle.Vector);
                        var selectors = swizzle.Components.Select(c => (uint)("xyzw".IndexOf(c) >= 0 ? "xyzw".IndexOf(c) : "rgba".IndexOf(c))).ToArray();
                        if (selectors.Length == 1) return Result(Op.CompositeExtract, swizzle.Type, source, selectors[0]);
                        return Result(Op.VectorShuffle, swizzle.Type, new uint[] { source, source }.Concat(selectors).ToArray());
                    case Expression.Select select:
                        uint condition = Value(select.Condition), accept = Value(select.Accept), reject = Value(select.Reject);
                        if (select.Type is ShaderType.Vector selectedVector && select.Condition.Type is ShaderType.Scalar) condition = Splat(condition, new ShaderType.Vector(selectedVector.Size, ShaderType.Bool));
                        return Result(Op.Select, select.Type, condition, accept, reject);
                    case Expression.Call call: return Call(call);
                    default: throw owner.Error($"Unsupported expression {expression.GetType().Name}.", expression.Span);
                }
            }

            private ShaderType.Scalar Scalar(ShaderType type) => type switch
            {
                ShaderType.Scalar s => s, ShaderType.Vector v => v.Component, ShaderType.Matrix m => m.Component, ShaderType.CooperativeMatrix m => m.Component,
                _ => throw owner.Error("Expected numeric type.")
            };
            private uint Splat(uint scalar, ShaderType.Vector vector) => Result(Op.CompositeConstruct, vector, Enumerable.Repeat(scalar, vector.Size).ToArray());
            private uint Binary(Expression.Binary binary)
            {
                uint left = Value(binary.Left), right = Value(binary.Right);
                ShaderType a = binary.Left.Type, b = binary.Right.Type;
                bool floating = Scalar(a).Kind == ScalarKind.Float, signed = Scalar(a).Kind == ScalarKind.Sint, boolean = Scalar(a).Kind == ScalarKind.Bool;
                Op op;
                if (a is ShaderType.Matrix leftMatrix && b is ShaderType.Matrix && binary.Operator is "+" or "-")
                {
                    var column = new ShaderType.Vector(leftMatrix.Rows, leftMatrix.Component);
                    return Result(Op.CompositeConstruct, binary.Type, Enumerable.Range(0, leftMatrix.Columns).Select(i => Result(binary.Operator == "+" ? Op.FAdd : Op.FSub, column, Result(Op.CompositeExtract, column, left, (uint)i), Result(Op.CompositeExtract, column, right, (uint)i))).ToArray());
                }
                if (binary.Operator == "*" && a is ShaderType.Matrix && b is ShaderType.Matrix) op = Op.MatrixTimesMatrix;
                else if (binary.Operator == "*" && a is ShaderType.Matrix && b is ShaderType.Vector) op = Op.MatrixTimesVector;
                else if (binary.Operator == "*" && a is ShaderType.Vector && b is ShaderType.Matrix) op = Op.VectorTimesMatrix;
                else if (binary.Operator == "*" && a is ShaderType.Matrix or ShaderType.CooperativeMatrix && b is ShaderType.Scalar) op = Op.MatrixTimesScalar;
                else if (binary.Operator == "*" && b is ShaderType.Matrix or ShaderType.CooperativeMatrix && a is ShaderType.Scalar) { (left, right) = (right, left); op = Op.MatrixTimesScalar; }
                else
                {
                    if (a is ShaderType.Vector av && b is ShaderType.Scalar) right = Splat(right, av);
                    if (b is ShaderType.Vector bv && a is ShaderType.Scalar) { left = Splat(left, bv); a = b; }
                    op = binary.Operator switch
                    {
                        "+" => floating ? Op.FAdd : Op.IAdd, "-" => floating ? Op.FSub : Op.ISub, "*" => floating ? Op.FMul : Op.IMul,
                        "/" => floating ? Op.FDiv : signed ? Op.SDiv : Op.UDiv, "%" => floating ? Op.FRem : signed ? Op.SRem : Op.UMod,
                        "==" => boolean ? Op.LogicalEqual : floating ? Op.FOrdEqual : Op.IEqual,
                        "!=" => boolean ? Op.LogicalNotEqual : floating ? Op.FUnordNotEqual : Op.INotEqual,
                        "<" => floating ? Op.FOrdLessThan : signed ? Op.SLessThan : Op.ULessThan,
                        "<=" => floating ? Op.FOrdLessThanEqual : signed ? Op.SLessThanEqual : Op.ULessThanEqual,
                        ">" => floating ? Op.FOrdGreaterThan : signed ? Op.SGreaterThan : Op.UGreaterThan,
                        ">=" => floating ? Op.FOrdGreaterThanEqual : signed ? Op.SGreaterThanEqual : Op.UGreaterThanEqual,
                        "&" => boolean ? Op.LogicalAnd : Op.BitwiseAnd, "|" => boolean ? Op.LogicalOr : Op.BitwiseOr,
                        "^" => boolean ? Op.LogicalNotEqual : Op.BitwiseXor, "&&" => Op.LogicalAnd, "||" => Op.LogicalOr,
                        "<<" => Op.ShiftLeftLogical, ">>" => signed ? Op.ShiftRightArithmetic : Op.ShiftRightLogical,
                        _ => throw owner.Error("Unsupported binary operator.", binary.Span)
                    };
                }
                return Result(op, binary.Type, left, right);
            }
            private uint LiteralSplat(object value, ShaderType type)
            {
                if (Scalar(type) is { Width: 2, Kind: ScalarKind.Sint or ScalarKind.Uint } narrow)
                    value = narrow.Kind == ScalarKind.Sint ? (object)System.Convert.ToInt16(value) : System.Convert.ToUInt16(value);
                if (Scalar(type) is { Width: 8 } scalar)
                    value = scalar.Kind switch { ScalarKind.Sint => System.Convert.ToInt64(value), ScalarKind.Uint => System.Convert.ToUInt64(value), ScalarKind.Float => System.Convert.ToDouble(value), _ => value };
                var literal = new Expression.Literal(value, Scalar(type));
                return owner.Constant(type is ShaderType.Vector vector ? new Expression.Construct(type, Enumerable.Repeat<Expression>(literal, vector.Size).ToArray()) : literal);
            }
            private uint One(ShaderType type) => LiteralSplat(Scalar(type).Kind switch { ScalarKind.Uint => (object)1u, ScalarKind.Sint => 1, ScalarKind.Float when Scalar(type).Width == 2 => (Half)1, _ => 1f }, type);
            private uint Convert(Expression.Convert conversion)
            {
                uint value = Value(conversion.Operand); ShaderType from = conversion.Operand.Type, to = conversion.Type;
                if (from == to) return value;
                if (conversion.Bitcast) return Result(Op.Bitcast, to, value);
                if (from is ShaderType.Matrix fromMatrix && to is ShaderType.Matrix toMatrix)
                {
                    var sourceColumn = new ShaderType.Vector(fromMatrix.Rows, fromMatrix.Component);
                    var resultColumn = new ShaderType.Vector(toMatrix.Rows, toMatrix.Component);
                    return Result(Op.CompositeConstruct, to, Enumerable.Range(0, toMatrix.Columns).Select(i => Result(Op.FConvert, resultColumn, Result(Op.CompositeExtract, sourceColumn, value, (uint)i))).ToArray());
                }
                var a = Scalar(from); var b = Scalar(to);
                if (b.Kind == ScalarKind.Bool) return Result(a.Kind == ScalarKind.Float ? Op.FUnordNotEqual : Op.INotEqual, to, value, owner.Null(from));
                if (a.Kind == ScalarKind.Bool) return Result(Op.Select, to, value, One(to), owner.Null(to));
                if (a.Kind == b.Kind && a.Width == b.Width) return value;
                if (a.Kind is ScalarKind.Sint or ScalarKind.Uint && b.Kind is ScalarKind.Sint or ScalarKind.Uint
                    && a.Kind != b.Kind && a.Width != b.Width)
                {
                    var component = new ShaderType.Scalar(a.Kind, b.Width);
                    ShaderType intermediate = to is ShaderType.Vector vector ? new ShaderType.Vector(vector.Size, component) : component;
                    return Result(Op.Bitcast, to, Result(a.Kind == ScalarKind.Sint ? Op.SConvert : Op.UConvert, intermediate, value));
                }
                if (a.Kind == ScalarKind.Float && b.Kind is ScalarKind.Sint or ScalarKind.Uint)
                {
                    var bounds = NumericConversions.IntegerFloatBounds(a, b);
                    value = Glsl(43, from, value, LiteralSplat(NumericConversions.FloatValue(bounds.Minimum, a), from), LiteralSplat(NumericConversions.FloatValue(bounds.Maximum, a), from));
                }
                Op op = (a.Kind, b.Kind) switch
                {
                    (ScalarKind.Float, ScalarKind.Float) => Op.FConvert,
                    (ScalarKind.Float, ScalarKind.Uint) => Op.ConvertFToU, (ScalarKind.Float, ScalarKind.Sint) => Op.ConvertFToS,
                    (ScalarKind.Uint, ScalarKind.Float) => Op.ConvertUToF, (ScalarKind.Sint, ScalarKind.Float) => Op.ConvertSToF,
                    _ when a.Width == b.Width => Op.Bitcast,
                    (_, ScalarKind.Uint) => Op.UConvert, (_, ScalarKind.Sint) => Op.SConvert,
                    _ => throw owner.Error("Unsupported conversion.")
                };
                return Result(op, to, value);
            }

            private void NativeBarrier(SpirvBarrierMemory barrier)
            {
                if (barrier.Scope == 1) owner.usesDeviceScope = true;
                if (barrier.ExecutionScope == 3) owner.capabilities.Add(61);
                if ((barrier.Semantics & 16) != 0) owner.usesSequentialMemoryOrder = true;
                uint scope = owner.Constant(Expression.U32(barrier.Scope)), semantics = owner.Constant(Expression.U32(barrier.Semantics));
                if (barrier.ExecutionScope is uint execution) Add(Op.ControlBarrier, owner.Constant(Expression.U32(execution)), scope, semantics);
                else Add(Op.MemoryBarrier, scope, semantics);
            }
        }
    }
}
