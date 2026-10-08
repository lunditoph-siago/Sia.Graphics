using System.Globalization;
using System.Text;
using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class WgslWriter
{
    public static string Write(Module module)
    {
        Valid.ModuleValidator.Validate(module);
        module = Proc.QueryHelperInliner.Run(module);
        module = Proc.QueryStateLowering.Run(module);
        module = WgslMemoryLowering.Run(module);
        Valid.ModuleValidator.Validate(module);
        if (module.Constants.Any(c => (c.IsOverride || c.IsSpecialization) && c.Type is ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint, Width: 8 }))
            throw new ShaderException(DiagnosticStage.WgslWrite, "64-bit integer specialization constants require PipelineConstantResolver resolution before WGSL writing.");
        return new Writer().Write(WgslLayoutLowering.Run(module));
    }
    private static bool IsAbstract(ShaderType type) => type switch
    {
        ShaderType.Scalar { Kind: ScalarKind.AbstractInt or ScalarKind.AbstractFloat } => true,
        ShaderType.Vector v => IsAbstract(v.Component), ShaderType.Matrix m => IsAbstract(m.Component),
        ShaderType.Array a => IsAbstract(a.Element), _ => false
    };

    public static string TypeName(ShaderType type) => type switch
    {
        ShaderType.Void => "void",
        ShaderType.Scalar { Kind: ScalarKind.Bool } => "bool",
        ShaderType.Scalar { Kind: ScalarKind.Sint, Width: 2 } => "i16",
        ShaderType.Scalar { Kind: ScalarKind.Uint, Width: 2 } => "u16",
        ShaderType.Scalar { Kind: ScalarKind.Sint, Width: 4 } => "i32",
        ShaderType.Scalar { Kind: ScalarKind.Uint, Width: 4 } => "u32",
        ShaderType.Scalar { Kind: ScalarKind.Float, Width: 4 } => "f32",
        ShaderType.Scalar { Kind: ScalarKind.Float, Width: 2 } => "f16",
        ShaderType.Scalar { Kind: ScalarKind.Sint, Width: 8 } => "i64",
        ShaderType.Scalar { Kind: ScalarKind.Uint, Width: 8 } => "u64",
        ShaderType.Scalar { Kind: ScalarKind.Float, Width: 8 } => "f64",
        ShaderType.Vector v => IsAbstract(v) ? $"vec{v.Size}" : $"vec{v.Size}<{TypeName(v.Component)}>",
        ShaderType.Matrix m => IsAbstract(m) ? $"mat{m.Columns}x{m.Rows}" : $"mat{m.Columns}x{m.Rows}<{TypeName(m.Component)}>",
        ShaderType.CooperativeMatrix m when m.Columns == m.Rows && m.Scope == 3 => $"coop_mat{m.Columns}x{m.Rows}<{TypeName(m.Component)}, {m.Role}>",
        ShaderType.Atomic a => $"atomic<{TypeName(a.Component)}>",
        ShaderType.Pointer p => $"ptr<{Space(p.Space)}, {TypeName(p.Base)}{(p.Space == AddressSpace.Storage ? ", " + Access(p.Access) : "")}>",
        ShaderType.Array a => ArrayName(a),
        ShaderType.BindingArray a => $"binding_array<{TypeName(a.Element)}{(a.Length is uint n ? ", " + n.ToString(CultureInfo.InvariantCulture) : a.OverrideLength is string o ? ", " + o : "")}>",
        ShaderType.Structure { BuiltinResult: BuiltinResultKind.RayDesc } => "RayDesc",
        ShaderType.Structure { BuiltinResult: BuiltinResultKind.RayIntersection } => "RayIntersection",
        ShaderType.Structure s => s.Name,
        ShaderType.Sampler s => s.Comparison ? "sampler_comparison" : "sampler",
        ShaderType.AccelerationStructure a => a.VertexReturn ? "acceleration_structure<vertex_return>" : "acceleration_structure",
        ShaderType.RayQuery q => q.VertexReturn ? "ray_query<vertex_return>" : "ray_query",
        ShaderType.Image i => ImageName(i),
        _ => throw new ShaderException(DiagnosticStage.WgslWrite, $"Type {type} has no WGSL representation.")
    };

    private static string ArrayName(ShaderType.Array array)
    {
        if (IsAbstract(array)) return "array";
        if (array.Stride is uint stride && stride != TypeLayout.Of(array.Element).Stride)
            throw new ShaderException(DiagnosticStage.WgslWrite, "Non-natural array stride requires element-wrapper lowering.");
        return $"array<{TypeName(array.Element)}{(array.Length is uint n ? ", " + n.ToString(CultureInfo.InvariantCulture) : array.OverrideLength is string o ? ", " + o : "")}>";
    }

    private static string ImageName(ShaderType.Image image)
    {
        string dimension = image.Dimension switch { ImageDimension.D1 => "1d", ImageDimension.D2 => "2d", ImageDimension.D3 => "3d", _ => "cube" };
        if (image.Arrayed) dimension += "_array";
        if (image.StorageFormat is string format) return $"texture_storage_{dimension}<{format}, {Access(image.Access)}>";
        if (image.Depth) return $"texture_depth_{(image.Multisampled ? "multisampled_" : "")}{dimension}";
        return $"texture_{(image.Multisampled ? "multisampled_" : "")}{dimension}<{TypeName(image.Component)}>";
    }

    internal static string Space(AddressSpace space) => space switch
    {
        AddressSpace.Function => "function", AddressSpace.Private => "private", AddressSpace.Workgroup => "workgroup",
        AddressSpace.Uniform => "uniform", AddressSpace.Storage => "storage", AddressSpace.Immediate => "immediate", AddressSpace.TaskPayload => "task_payload",
        _ => throw new ShaderException(DiagnosticStage.WgslWrite, "Handle space is implicit in WGSL.")
    };
    internal static string Access(StorageAccess access) => access switch
    {
        StorageAccess.Read => "read", StorageAccess.Write => "write", StorageAccess.ReadWrite => "read_write",
        StorageAccess.ReadWrite | StorageAccess.Atomic => "atomic",
        _ => throw new ShaderException(DiagnosticStage.WgslWrite, "Empty storage access.")
    };

    internal static string Expr(Expression expression) => Expr(expression, null);
    private static string Expr(Expression expression, Func<Expression.Convert, string>? bitcast)
    {
        string E(Expression value) => Expr(value, bitcast);
        return expression switch
        {
            Expression.Literal literal => Literal(literal),
            Expression.Reference r => r.Name,
            Expression.Load l => E(l.Pointer),
            Expression.Unary { Operator: "-", Type: ShaderType.CooperativeMatrix m } u => $"(-1{(m.Component.Width == 2 ? "h" : "f")} * {E(u.Operand)})",
            Expression.Unary { Operator: "&" } u => Address(u, E),
            Expression.Unary u => $"({u.Operator}{E(u.Operand)})",
            Expression.Binary b => $"({E(b.Left)} {b.Operator} {E(b.Right)})",
            Expression.Call { Function: "isNan" or "isInf" } c => Classification(c, E),
            Expression.Call { Function: "coopLoad" or "coopLoadT" or "coopStore" or "coopStoreT" } c => CooperativeMemory(c, E),
            Expression.Call c when RayQueryTypes.RawGetterType(c.Function) is not null => RawRayGetter(c, E),
            Expression.Call c when c.Function.StartsWith("spirvRayQuery", StringComparison.Ordinal) => RawRayCall(c, E),
            Expression.Call c => $"{c.Function}({string.Join(", ", c.Arguments.Select(E))})",
            Expression.Construct { Type: ShaderType.Array { OverrideLength: not null } } => throw new ShaderException(DiagnosticStage.WgslWrite, "Override-sized array zero values require pipeline constant resolution."),
            Expression.Construct { Type: ShaderType.CooperativeMatrix, Components.Count: > 0 } => throw new ShaderException(DiagnosticStage.WgslWrite, "WGSL cooperative matrix constructors cannot express a scalar splat."),
            Expression.Construct c => $"{TypeName(c.Type)}({string.Join(", ", c.Components.Select(E))})",
            Expression.Convert { Bitcast: true } c => bitcast is null ? $"bitcast<{TypeName(c.Type)}>({E(c.Operand)})" : bitcast(c),
            Expression.Convert c when ConstantEvaluator.TryEvaluateRuntime(c, out var converted) => E(converted),
            Expression.Convert c => $"{TypeName(c.Type)}({E(c.Operand)})",
            Expression.Access a => $"{E(a.Base)}[{E(a.Index)}]",
            Expression.Member m => $"{E(m.Base)}.{m.Name}",
            Expression.Swizzle s => $"{E(s.Vector)}.{s.Components}",
            Expression.Select s => $"select({E(s.Reject)}, {E(s.Accept)}, {E(s.Condition)})",
            _ => throw new ShaderException(DiagnosticStage.WgslWrite, $"Unsupported expression {expression.GetType().Name}.")
        };
    }

    private static string Address(Expression.Unary address, Func<Expression, string> expr)
    {
        if (address.Operand is Expression.Access a && (a.Base.Type is ShaderType.Vector or ShaderType.Pointer { Base: ShaderType.Vector }))
            throw new ShaderException(DiagnosticStage.WgslWrite, "A native vector component address requires projection lowering before WGSL writing.", address.Operand.Span);
        return $"(&{expr(address.Operand)})";
    }

    private static string CooperativeMemory(Expression.Call call, Func<Expression, string> expr)
    {
        bool load = call.Function is "coopLoad" or "coopLoadT";
        var matrix = (ShaderType.CooperativeMatrix)(load ? call.Type : call.Arguments[0].Type);
        ShaderType memory = ((ShaderType.Pointer)call.Arguments[load ? 0 : 1].Type).Base;
        var component = memory is ShaderType.Vector vector ? vector.Component : (ShaderType.Scalar)memory;
        if (component != matrix.Component) throw new ShaderException(DiagnosticStage.WgslWrite,
            "WGSL cooperative memory operations infer the matrix component from the memory pointer; raw reinterpretation requires equivalent lowering.");
        return $"{call.Function}{(load ? "<" + TypeName(matrix) + ">" : "")}({string.Join(", ", call.Arguments.Select(expr))})";
    }

    private static string RawRayCall(Expression.Call call, Func<Expression, string> expr)
    {
        var args = call.Arguments;
        if (call.Function == "spirvRayQueryInitializeKHR")
            return $"rayQueryInitialize({expr(args[0])}, {expr(args[1])}, RayDesc({string.Join(", ", new[] { 2, 3, 5, 7, 4, 6 }.Select(i => expr(args[i])))}))";
        string name = call.Function switch
        {
            "spirvRayQueryProceedKHR" => "rayQueryProceed", "spirvRayQueryGenerateIntersectionKHR" => "rayQueryGenerateIntersection",
            "spirvRayQueryConfirmIntersectionKHR" => "rayQueryConfirmIntersection", "spirvRayQueryTerminateKHR" => "rayQueryTerminate",
            _ => throw new ShaderException(DiagnosticStage.WgslWrite, "Unsupported raw ray-query operation.")
        };
        return $"{name}({string.Join(", ", args.Select(expr))})";
    }

    private static string RawRayGetter(Expression.Call call, Func<Expression, string> expr)
    {
        if (call.Arguments.Count != 2 || call.Arguments[1] is not Expression.Literal { Value: 0u })
            throw new ShaderException(DiagnosticStage.WgslWrite,
                "Raw SPIR-V ray-query state getters require equivalent WGSL lowering; committed-intersection reads may occur before traversal completion.");
        string query = expr(call.Arguments[0]);
        if (call.Function == "spirvRayQueryGetIntersectionTriangleVertexPositionsKHR") return $"getCandidateHitVertexPositions({query})";
        string member = call.Function switch
        {
            "spirvRayQueryGetIntersectionTypeKHR" => "kind", "spirvRayQueryGetIntersectionTKHR" => "t",
            "spirvRayQueryGetIntersectionInstanceCustomIndexKHR" => "instance_custom_data",
            "spirvRayQueryGetIntersectionInstanceIdKHR" => "instance_index",
            "spirvRayQueryGetIntersectionInstanceShaderBindingTableRecordOffsetKHR" => "sbt_record_offset",
            "spirvRayQueryGetIntersectionGeometryIndexKHR" => "geometry_index", "spirvRayQueryGetIntersectionPrimitiveIndexKHR" => "primitive_index",
            "spirvRayQueryGetIntersectionBarycentricsKHR" => "barycentrics", "spirvRayQueryGetIntersectionFrontFaceKHR" => "front_face",
            "spirvRayQueryGetIntersectionObjectToWorldKHR" => "object_to_world", "spirvRayQueryGetIntersectionWorldToObjectKHR" => "world_to_object",
            _ => throw new ShaderException(DiagnosticStage.WgslWrite, "Unsupported ray query getter.")
        };
        string value = $"rayQueryGetCandidateIntersection({query}).{member}";
        // Candidate kinds are triangle=0/AABB=1 in SPIR-V and 1/3 in WGSL.
        return member == "kind" ? $"select(1u, 0u, {value} == 1u)" : value;
    }

    private static string Classification(Expression.Call call, Func<Expression, string> expr)
    {
        Expression input = call.Arguments[0];
        ShaderType.Scalar scalar = input.Type is ShaderType.Vector v ? v.Component : (ShaderType.Scalar)input.Type;
        bool wide = scalar.Width == 8;
        var unsigned = new ShaderType.Scalar(ScalarKind.Uint, wide ? 8 : 4);
        ShaderType integer = input.Type is ShaderType.Vector vector ? new ShaderType.Vector(vector.Size, unsigned) : unsigned;
        string value = expr(input);
        if (scalar.Width == 2) value = $"{TypeName(input.Type is ShaderType.Vector half ? new ShaderType.Vector(half.Size, ShaderType.F32) : ShaderType.F32)}({value})";
        string Constant(ulong bits)
        {
            string literal = bits.ToString(CultureInfo.InvariantCulture) + (wide ? "lu" : "u");
            return integer is ShaderType.Vector ? $"{TypeName(integer)}({literal})" : literal;
        }
        string mask = Constant(wide ? 0x7ffffffffffffffful : 0x7fffffffu);
        string infinity = Constant(wide ? 0x7ff0000000000000ul : 0x7f800000u);
        return $"((bitcast<{TypeName(integer)}>({value}) & {mask}) {(call.Function == "isNan" ? ">" : "==")} {infinity})";
    }

    private static string Literal(Expression.Literal literal)
    {
        if (literal.Value is bool b) return b ? "true" : "false";
        if (literal.Type is ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint, Width: 2 } narrow)
            return $"{TypeName(narrow)}({System.Convert.ToString(literal.Value, CultureInfo.InvariantCulture)})";
        if (literal.Value is int i && i == int.MinValue) return "(-2147483647i - 1i)";
        if (literal.Value is long l && l == long.MinValue) return literal.Type is ShaderType.Scalar { Kind: ScalarKind.Sint } ? "i64(9223372036854775808lu)" : "(-9223372036854775807 - 1)";
        if (literal.Value is float f)
        {
            if (!float.IsFinite(f)) return $"bitcast<f32>({BitConverter.SingleToUInt32Bits(f)}u)";
            string number = f.ToString("R", CultureInfo.InvariantCulture);
            return number + "f";
        }
        if (literal.Value is Half h)
        {
            if (!Half.IsFinite(h)) return $"bitcast<vec2<f16>>({BitConverter.HalfToUInt16Bits(h)}u).x";
            return ((float)h).ToString("R", CultureInfo.InvariantCulture) + "h";
        }
        if (literal.Value is double d)
        {
            bool concrete = literal.Type is ShaderType.Scalar { Kind: ScalarKind.Float, Width: 8 };
            if (!double.IsFinite(d)) return $"bitcast<f64>({BitConverter.DoubleToUInt64Bits(d)}lu)";
            string number = d.ToString("R", CultureInfo.InvariantCulture);
            return number + (number.Contains('.') || number.Contains('E') || number.Contains('e') ? "" : ".0") + (concrete ? "lf" : "");
        }
        string value = Convert.ToString(literal.Value, CultureInfo.InvariantCulture)!;
        return literal.Type is ShaderType.Scalar s ? value + (s.Kind == ScalarKind.Uint ? s.Width == 8 ? "lu" : "u" : s.Kind == ScalarKind.Sint ? s.Width == 8 ? "li" : "i" : "") : value;
    }

    private static string Binding(IoBinding? binding)
    {
        if (binding is null) return "";
        var value = new StringBuilder();
        if (binding.Location is uint location) value.Append($"@location({location}) ");
        if (binding.Builtin is string builtin) value.Append($"@builtin({builtin}) ");
        if (binding.Interpolation is string interpolation)
            value.Append($"@interpolate({interpolation}{(binding.Sampling is string sampling ? ", " + sampling : "")}) ");
        if (binding.Invariant) value.Append("@invariant ");
        if (binding.BlendSource is uint source) value.Append($"@blend_src({source}) ");
        if (binding.PerPrimitive) value.Append("@per_primitive ");
        return value.ToString();
    }

    private sealed partial class Writer
    {
        private readonly StringBuilder text = new();
        private int indent;
        private void Line(string value = "") => text.Append(' ', indent * 4).Append(value).Append('\n');
        private void Open(string prefix) { Line(prefix + " {"); indent++; }
        private void Close() { indent--; Line("}"); }

        public string Write(Module module)
        {
            ReserveNames(module);
            var interfaceStructures = new HashSet<ShaderType>(module.Functions.Where(f => f.Stage is not null)
                .SelectMany(f => f.Arguments.Select(a => a.Type).Append(f.ReturnType)));
            foreach (var function in module.Functions.Where(f => f.Stage == ShaderStage.Mesh))
            {
                var mesh = MeshShaderInfo.Inspect(module, function);
                interfaceStructures.Add(mesh.Structure); interfaceStructures.Add(mesh.Vertices.Element); interfaceStructures.Add(mesh.Primitives.Element);
            }
            var enables = new HashSet<string>(module.Enables, StringComparer.Ordinal);
            void IoEnable(IoBinding? binding)
            {
                if (binding?.BlendSource is not null) enables.Add("dual_source_blending");
                if (binding?.Builtin is "primitive_index" or "clip_distances" or "draw_index") enables.Add(binding.Builtin);
                if (binding?.Interpolation == "per_vertex") enables.Add("wgpu_per_vertex");
                if (binding?.PerPrimitive == true) enables.Add("wgpu_mesh_shader");
            }
            foreach (var s in module.Structures.Where(interfaceStructures.Contains)) foreach (var m in s.Members) IoEnable(m.Binding);
            foreach (var f in module.Functions) { IoEnable(f.ReturnBinding); foreach (var a in f.Arguments) IoEnable(a.Binding); }
            foreach (string enable in enables.Order(StringComparer.Ordinal)) Line($"enable {enable};");
            foreach (var filter in module.DiagnosticFilters) Line($"diagnostic({Filter(filter)});");
            foreach (var structure in module.Structures)
            {
                if (structure.BuiltinResult is not null) continue;
                Open($"struct {structure.Name}");
                _ = TypeLayout.Of(structure);
                uint position = 0;
                for (int index = 0; index < structure.Members.Count; index++)
                {
                    var member = structure.Members[index];
                    var natural = TypeLayout.Of(member.Type);
                    uint offset = member.Offset ?? TypeLayout.RoundUp(member.Alignment ?? natural.Alignment, position);
                    if (index == 0 && offset != 0)
                        throw new ShaderException(DiagnosticStage.WgslWrite, "Nonzero first member offset requires padding-field lowering.");
                    uint size = member.Size ?? natural.Size;
                    if (index + 1 < structure.Members.Count && structure.Members[index + 1].Offset is uint next)
                    {
                        var nextMember = structure.Members[index + 1];
                        uint nextAlignment = nextMember.Alignment ?? TypeLayout.Of(nextMember.Type).Alignment;
                        if (TypeLayout.RoundUp(nextAlignment, checked(offset + size)) < next) size = checked(next - offset);
                    }
                    position = checked(offset + size);
                    string layout = (member.Alignment is uint a ? $"@align({a}) " : "") + (size != natural.Size ? $"@size({size}) " : "");
                    Line($"{layout}{Binding(interfaceStructures.Contains(structure) ? member.Binding : null)}{member.Name}: {TypeName(member.Type)},");
                }
                Close(); Line();
            }
            globalExpression = true;
            foreach (var constant in module.Constants)
                Line($"{(constant.OverrideId is uint id ? $"@id({id}) " : "")}{(constant.IsOverride || constant.IsSpecialization ? "override" : "const")} {constant.Name}{(IsAbstract(constant.Type) ? "" : ": " + TypeName(constant.Type))}{(constant.Value is null ? "" : " = " + Expr(constant.Value))};");
            foreach (var global in module.Globals)
            {
                string binding = global.Binding is { } b ? $"@group({b.Group}) @binding({b.Binding}) " : "";
                string memory = ((global.MemoryDecorations & MemoryDecorations.Coherent) != 0 ? "@coherent " : "")
                    + ((global.MemoryDecorations & MemoryDecorations.Volatile) != 0 ? "@volatile " : "");
                string address = global.Space == AddressSpace.Handle ? "" : $"<{Space(global.Space)}{(global.Space == AddressSpace.Storage ? ", " + Access(global.Access) : "")}>";
                Line($"{binding}{memory}var{address} {global.Name}: {TypeName(global.Type)}{(global.Initializer is null ? "" : " = " + Expr(global.Initializer))};");
            }
            globalExpression = false;
            foreach (var function in module.Functions)
            {
                Line();
                foreach (var filter in function.DiagnosticFilters) Line($"@diagnostic({Filter(filter)})");
                if (function.Stage is ShaderStage stage)
                {
                    Line("@" + stage.ToString().ToLowerInvariant() + (stage == ShaderStage.Mesh ? $"({function.MeshOutput})" : ""));
                    if (stage is ShaderStage.Compute or ShaderStage.Task or ShaderStage.Mesh) Line($"@workgroup_size({string.Join(", ", function.WorkgroupSize.Select(Expr))})");
                    if (function.TaskPayload is string payload) Line($"@payload({payload})");
                }
                if (function.EarlyDepthTest) Line($"@early_depth_test({function.ConservativeDepth ?? "force"})");
                string args = string.Join(", ", function.Arguments.Select(a => $"{Binding(a.Binding)}{a.Name}: {TypeName(a.Type)}"));
                string result = function.ReturnType is ShaderType.Void ? "" : $" -> {Binding(function.ReturnBinding)}{TypeName(function.ReturnType)}";
                Open($"fn {function.Name}({args}){result}");
                Body(function.Body);
                Close();
            }
            WriteBitcastHelpers();
            return text.ToString();
        }

        private static string Filter(DiagnosticFilter filter) => $"{filter.Severity.ToString().ToLowerInvariant()}, {(filter.Namespace is null ? "" : filter.Namespace + ".")}{filter.Rule}";

        private void Body(Block block)
        {
            foreach (var statement in InlinePendingArrayArguments(block.Statements))
            {
                switch (statement)
                {
                    case Statement.Nested nested: Open(""); Body(nested.Body); Close(); break;
                    case Statement.Declare d:
                        if (d.Type is ShaderType.Array { OverrideLength: not null })
                            throw new ShaderException(DiagnosticStage.WgslWrite, "Writing snapshots of override-sized arrays requires pipeline constant resolution.", d.Span);
                        Line($"{(d.Mutable ? "var" : "let")} {d.Name}{(d.Type is ShaderType.Structure { BuiltinResult: not null } ? "" : ": " + TypeName(d.Type))}{(d.Initializer is null ? "" : " = " + Expr(d.Initializer))};"); break;
                    case Statement.Store s: Line($"{Expr(s.Target)} = {Expr(s.Value)};"); break;
                    case Statement.Evaluate e: Line((e.Value.Type is ShaderType.Void ? "" : "_ = ") + Expr(e.Value) + ";"); break;
                    case Statement.If i:
                        Open($"if ({Expr(i.Condition)})"); Body(i.Accept); Close();
                        if (i.Reject.Statements.Count != 0) { Open("else"); Body(i.Reject); Close(); }
                        break;
                    case Statement.Loop l:
                        Open("loop"); Body(l.Body);
                        if (l.Continuing.Statements.Count != 0 || l.BreakIf is not null)
                        {
                            Open("continuing"); Body(l.Continuing);
                            if (l.BreakIf is not null) Line($"break if {Expr(l.BreakIf)};");
                            Close();
                        }
                        Close(); break;
                    case Statement.Switch s:
                        Open($"switch ({Expr(s.Selector)})");
                        foreach (var c in s.Cases)
                        {
                            string labels = string.Join(", ", c.Values.Select(Expr).Concat(c.IsDefault ? ["default"] : Array.Empty<string>()));
                            Open((c.Values.Count == 0 ? "default" : "case " + labels) + ":"); Body(c.Body); Close();
                        }
                        Close(); break;
                    case Statement.Return r: Line(r.Value is null ? "return;" : "return " + Expr(r.Value) + ";"); break;
                    case Statement.Break: Line("break;"); break;
                    case Statement.Continue: Line("continue;"); break;
                    case Statement.Kill: Line("discard;"); break;
                    case Statement.Barrier b:
                        if (b.Storage) Line("storageBarrier();");
                        if (b.Workgroup) Line("workgroupBarrier();");
                        if (b.Texture) Line("textureBarrier();");
                        if (b.Subgroup) Line("subgroupBarrier();");
                        break;
                    default: throw new ShaderException(DiagnosticStage.WgslWrite, $"Unsupported statement {statement.GetType().Name}.");
                }
            }
        }
    }
}
