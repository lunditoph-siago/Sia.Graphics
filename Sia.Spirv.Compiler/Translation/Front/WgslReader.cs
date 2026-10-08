using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Front;

public static partial class WgslReader
{
    public static Module Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new Lowerer(new WgslParser(source).Parse()).Lower();
    }

    private sealed partial class Lowerer(SModule syntax)
    {
        private readonly Module module = new();
        private readonly Dictionary<string, ShaderType> namedTypes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Symbol> globals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ShaderFunction> functions = new(StringComparer.Ordinal);
        private readonly HashSet<string> mustUseFunctions = new(StringComparer.Ordinal);
        private readonly Stack<Dictionary<string, Symbol>> scopes = new();
        private readonly HashSet<string> resolving = new(StringComparer.Ordinal);
        private readonly HashSet<string> resolvingTypes = new(StringComparer.Ordinal);
        private readonly HashSet<string> generated = new(syntax.Identifiers, StringComparer.Ordinal);
        private ShaderFunction? currentFunction;
        private int temporary;
        private sealed record Symbol(Expression Value, bool Place, bool Writable);
        private ShaderException Error(string message, SourceSpan span = default) => new(DiagnosticStage.Validation, message, span);
        private string Fresh()
        {
            string name; do name = "sia_temp" + temporary++; while (!generated.Add(name)); return name;
        }

        public Module Lower()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in syntax.Structures.Select(s => s.Name).Concat(syntax.Aliases.Keys).Concat(syntax.Declarations.Select(d => d.Name)).Concat(syntax.Functions.Select(f => f.Name)))
                if (!names.Add(name)) throw Error($"Duplicate module identifier '{name}'.");
            module.Enables.UnionWith(syntax.Enables);
            module.DiagnosticFilters.AddRange(syntax.DiagnosticFilters);
            foreach (var structure in syntax.Structures) ResolveType(new(structure.Name, [], default));
            foreach (string alias in syntax.Aliases.Keys) ResolveType(new(alias, [], default));
            foreach (var declaration in syntax.Declarations) ResolveGlobal(declaration.Name);
            foreach (var source in syntax.Functions)
            {
                CheckAttributes(source.Attributes.Where(a => a.Name != "diagnostic").ToArray(), "vertex", "fragment", "compute", "task", "mesh", "payload", "workgroup_size", "early_depth_test", "must_use");
                var function = new ShaderFunction(source.Name) { ReturnType = source.Result is null ? new ShaderType.Void() : ResolveType(source.Result), ReturnBinding = Io(source.ResultAttributes) };
                function.DiagnosticFilters.AddRange(source.Attributes.Where(a => a.DiagnosticFilter is not null).Select(a => a.DiagnosticFilter!));
                if (source.Attributes.FirstOrDefault(a => a.Name == "must_use") is { } mustUse)
                {
                    if (mustUse.Arguments.Count != 0 || function.ReturnType is ShaderType.Void) throw Error("@must_use needs a function with a return value and no attribute arguments.", mustUse.Span);
                    mustUseFunctions.Add(function.Name);
                }
                int stages = 0;
                foreach (var attribute in source.Attributes)
                {
                    if (attribute.Name is "vertex" or "fragment" or "compute" or "task" or "mesh")
                    {
                        if (++stages > 1 || attribute.Arguments.Count != (attribute.Name == "mesh" ? 1 : 0)) throw Error("Function has invalid stage attributes.", attribute.Span);
                        function.Stage = attribute.Name switch { "vertex" => ShaderStage.Vertex, "fragment" => ShaderStage.Fragment, "task" => ShaderStage.Task, "mesh" => ShaderStage.Mesh, _ => ShaderStage.Compute };
                        if (attribute.Name is "mesh" or "task" && !syntax.Enables.Contains("wgpu_mesh_shader")) throw Error("Mesh/task stages require wgpu_mesh_shader.", attribute.Span);
                        if (attribute.Name == "mesh") function.MeshOutput = MeshGlobalName(attribute);
                    }
                    else if (attribute.Name == "payload") function.TaskPayload = MeshGlobalName(attribute);
                    else if (attribute.Name == "workgroup_size")
                    {
                        if (attribute.Arguments.Count is < 1 or > 3) throw Error("workgroup_size needs one to three arguments.", attribute.Span);
                        var workgroup = new Expression[] { Expression.U32(1), Expression.U32(1), Expression.U32(1) };
                        for (int i = 0; i < attribute.Arguments.Count; i++)
                        {
                            var size = Eval(attribute.Arguments[i], new());
                            workgroup[i] = Materialize(size, DefaultType(size.Type));
                        }
                        function.WorkgroupSize = workgroup;
                    }
                    else if (attribute.Name == "early_depth_test")
                    {
                        if (attribute.Arguments is not [SExpression.Name { Templates.Count: 0 } depth] || depth.Text is not ("force" or "less_equal" or "greater_equal" or "unchanged")) throw Error("Invalid early_depth_test argument.", attribute.Span);
                        function.EarlyDepthTest = true; function.ConservativeDepth = depth.Text == "force" ? null : depth.Text;
                    }
                }
                foreach (var argument in source.Arguments)
                    function.Arguments.Add(new(argument.Name, ResolveType(argument.Type), Io(argument.Attributes)));
                if (function.Arguments.Select(a => a.Name).Distinct(StringComparer.Ordinal).Count() != function.Arguments.Count) throw Error("Duplicate function parameter.");
                if (function.Stage is ShaderStage.Compute or ShaderStage.Task or ShaderStage.Mesh && !source.Attributes.Any(a => a.Name == "workgroup_size")) throw Error("Workgroup entry point needs workgroup_size.");
                if (function.Stage is not (ShaderStage.Compute or ShaderStage.Task or ShaderStage.Mesh) && source.Attributes.Any(a => a.Name == "workgroup_size")) throw Error("workgroup_size requires a compute, task or mesh entry.");
                functions.Add(function.Name, function); module.Functions.Add(function);
            }
            foreach (var assertion in syntax.Assertions) Assert(assertion);
            foreach (var source in syntax.Functions)
            {
                currentFunction = functions[source.Name];
                scopes.Push(new(StringComparer.Ordinal));
                foreach (var arg in currentFunction.Arguments) scopes.Peek().Add(arg.Name, new(new Expression.Reference(arg.Name, arg.Type), false, false));
                currentFunction.Body = Body(source.Body, false);
                scopes.Pop();
            }
            currentFunction = null;
            return module;
        }

        private string MeshGlobalName(SAttribute attribute)
        {
            if (attribute.Arguments is not [SExpression.Name { Templates.Count: 0 } name] || !module.Globals.Any(g => g.Name == name.Text))
                throw Error("Mesh/payload attribute requires a module variable name.", attribute.Span);
            return name.Text;
        }

        private ShaderType ResolveType(SExpression.Name syntaxType)
        {
            string name = syntaxType.Text;
            if (namedTypes.TryGetValue(name, out var existing) && syntaxType.Templates.Count == 0) return existing;
            bool userType = syntax.Aliases.ContainsKey(name) || syntax.Structures.Any(s => s.Name == name);
            if (userType && !resolvingTypes.Add(name)) throw Error($"Recursive type '{name}'.", syntaxType.Span);
            try
            {
                ShaderType result;
                var args = syntaxType.Templates;
                ShaderType Argument(int index) => index < args.Count && args[index] is SExpression.Name type ? ResolveType(type) : throw Error("Expected a type template argument.", syntaxType.Span);
                ShaderType.Scalar Scalar(int index) => Argument(index) as ShaderType.Scalar ?? throw Error("Expected a scalar type.", syntaxType.Span);
                string Word(int index) => index < args.Count && args[index] is SExpression.Name word && word.Templates.Count == 0 ? word.Text : throw Error("Expected a name template argument.", syntaxType.Span);
                if (syntax.Aliases.TryGetValue(name, out var alias))
                {
                    if (args.Count != 0) throw Error("Aliases do not take template arguments.", syntaxType.Span);
                    result = ResolveType(alias); namedTypes[name] = result; return result;
                }
                var structure = syntax.Structures.FirstOrDefault(s => s.Name == name);
                if (structure is not null)
                {
                    if (args.Count != 0) throw Error("Structures do not take template arguments.", syntaxType.Span);
                    var members = new List<StructMember>(); var memberNames = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var member in structure.Members)
                    {
                        if (!memberNames.Add(member.Name)) throw Error("Duplicate structure member.");
                        CheckAttributes(member.Attributes, "align", "size", "location", "builtin", "interpolate", "invariant", "blend_src", "per_primitive");
                        members.Add(new(member.Name, ResolveType(member.Type), Alignment: UintAttribute(member.Attributes, "align"), Size: UintAttribute(member.Attributes, "size"), Binding: Io(member.Attributes, true)));
                    }
                    if (members.Count == 0) throw Error("Structure must have at least one member.");
                    var value = new ShaderType.Structure(name, members);
                    namedTypes.Add(name, value); module.Structures.Add(value); return value;
                }
                if (name is "bool" or "i16" or "u16" or "i32" or "u32" or "f32" or "f16" or "i64" or "u64" or "f64")
                {
                    if (args.Count != 0) throw Error("Scalar types do not take template arguments.", syntaxType.Span);
                    if (name == "f16" && !syntax.Enables.Contains("f16")) throw Error("f16 requires 'enable f16;'.", syntaxType.Span);
                    if (name is "i16" or "u16" && !syntax.Enables.Contains("wgpu_int16")) throw Error("16-bit integers require 'enable wgpu_int16;'.", syntaxType.Span);
                    result = name switch { "bool" => ShaderType.Bool, "i16" => ShaderType.I16, "u16" => ShaderType.U16, "i32" => ShaderType.I32, "u32" => ShaderType.U32, "f32" => ShaderType.F32, "i64" => new ShaderType.Scalar(ScalarKind.Sint, 8), "u64" => new ShaderType.Scalar(ScalarKind.Uint, 8), "f64" => new ShaderType.Scalar(ScalarKind.Float, 8), _ => ShaderType.F16 };
                }
                else if (name.StartsWith("vec", StringComparison.Ordinal) && name.Length is 4 or 5 && name[3] is >= '2' and <= '4')
                {
                    ShaderType.Scalar scalar = name.Length == 5 ? ShorthandScalar(name[4], syntaxType.Span) : args.Count == 1 ? Scalar(0) : throw Error("Vector type needs a scalar template argument.", syntaxType.Span);
                    if (name.Length == 5 && args.Count != 0) throw Error("Shorthand vector has no template arguments.");
                    result = new ShaderType.Vector(name[3] - '0', scalar);
                }
                else if (name.StartsWith("mat", StringComparison.Ordinal) && name.Length is 6 or 7 && name[3] is >= '2' and <= '4' && name[4] == 'x' && name[5] is >= '2' and <= '4')
                {
                    ShaderType.Scalar scalar = name.Length == 7 ? ShorthandScalar(name[6], syntaxType.Span) : args.Count == 1 ? Scalar(0) : throw Error("Matrix type needs a scalar template argument.");
                    if (scalar.Kind != ScalarKind.Float || name.Length == 7 && args.Count != 0) throw Error("Invalid matrix scalar type.");
                    result = new ShaderType.Matrix(name[3] - '0', name[5] - '0', scalar);
                }
                else if (name is "coop_mat8x8" or "coop_mat16x16")
                {
                    if (!syntax.Enables.Contains("wgpu_cooperative_matrix")) throw Error("Cooperative matrices require wgpu_cooperative_matrix.", syntaxType.Span);
                    if (args.Count != 2 || Scalar(0) is not { Kind: ScalarKind.Float, Width: 2 or 4 } component
                        || Word(1) is not ("A" or "B" or "C")) throw Error("Cooperative matrix needs f16/f32 and an A/B/C role.", syntaxType.Span);
                    int size = name == "coop_mat8x8" ? 8 : 16;
                    result = new ShaderType.CooperativeMatrix(size, size, component, Enum.Parse<CooperativeRole>(Word(1)));
                }
                else if (name is "array" or "binding_array")
                {
                    if (args.Count is < 1 or > 2) throw Error("Array type needs one or two template arguments.");
                    ShaderType element = Argument(0); uint? count = null; string? pending = null;
                    if (args.Count == 2)
                    {
                        var prelude = new Block(); Expression length = Eval(args[1], prelude);
                        if (prelude.Statements.Count != 0) throw Error("Array length must be a constant or override expression.", args[1].Span);
                        if (ConstantEvaluator.TryEvaluate(length, out _)) count = UintExpression(args[1]);
                        else
                        {
                            if (length.Type is not ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint }
                                || !OverrideExpressions.IsValid(length, module.Constants.ToDictionary(c => c.Name, StringComparer.Ordinal)))
                                throw Error("Array length must be an integer constant or override expression.", args[1].Span);
                            if (length is Expression.Reference r && module.Constants.Any(c => c.Name == r.Name && (c.IsOverride || c.IsSpecialization))) pending = r.Name;
                            else
                            {
                                pending = Fresh(); module.Constants.Add(new(pending, length.Type, length) { IsSpecialization = true });
                                globals.Add(pending, new(new Expression.Reference(pending, length.Type), false, false));
                            }
                        }
                    }
                    if (count == 0) throw Error("Array length must be positive.");
                    result = name == "array" ? new ShaderType.Array(element, count) { OverrideLength = pending }
                        : new ShaderType.BindingArray(element, count) { OverrideLength = pending };
                }
                else if (name == "atomic")
                {
                    if (args.Count != 1 || Scalar(0) is not ({ Kind: ScalarKind.Sint or ScalarKind.Uint, Width: 4 or 8 } or { Kind: ScalarKind.Float, Width: 4 })) throw Error("atomic needs i32, u32, i64, u64 or f32.");
                    result = new ShaderType.Atomic(Scalar(0));
                }
                else if (name == "ptr")
                {
                    if (args.Count is < 2 or > 3) throw Error("ptr needs address space, type and optional access.");
                    AddressSpace space = Space(Word(0));
                    result = new ShaderType.Pointer(Argument(1), space, args.Count == 3 ? Access(Word(2)) : space == AddressSpace.Storage ? StorageAccess.Read : StorageAccess.ReadWrite);
                }
                else if (name is "RayDesc" or "RayIntersection")
                {
                    if (!syntax.Enables.Contains("wgpu_ray_query") && !syntax.Enables.Contains("wgpu_ray_tracing_pipeline")) throw Error("Ray types require a ray extension.", syntaxType.Span);
                    if (args.Count != 0) throw Error("Ray structures do not take template arguments.", syntaxType.Span);
                    var ray = name == "RayDesc" ? RayQueryTypes.Descriptor : RayQueryTypes.Intersection;
                    result = ray; if (!module.Structures.Contains(ray)) module.Structures.Add(ray);
                }
                else if (name is "ray_query" or "acceleration_structure")
                {
                    if (!syntax.Enables.Contains("wgpu_ray_query") && !syntax.Enables.Contains("wgpu_ray_tracing_pipeline")) throw Error("Ray types require a ray extension.", syntaxType.Span);
                    bool vertex = args.Count == 1 && Word(0) == "vertex_return";
                    if (args.Count > 1 || args.Count == 1 && !vertex) throw Error("Invalid ray type template argument.", syntaxType.Span);
                    if (vertex && !syntax.Enables.Contains("wgpu_ray_query_vertex_return")) throw Error("vertex_return requires wgpu_ray_query_vertex_return.", syntaxType.Span);
                    result = name == "ray_query" ? new ShaderType.RayQuery(vertex) : new ShaderType.AccelerationStructure(vertex);
                }
                else if (name is "sampler" or "sampler_comparison")
                {
                    if (args.Count != 0) throw Error("Sampler does not take template arguments.");
                    result = new ShaderType.Sampler(name == "sampler_comparison");
                }
                else if (name.StartsWith("texture_", StringComparison.Ordinal))
                {
                    bool storage = name.StartsWith("texture_storage_", StringComparison.Ordinal), depth = name.StartsWith("texture_depth_", StringComparison.Ordinal), ms = name.Contains("multisampled", StringComparison.Ordinal);
                    string dimensionName = name[(name.LastIndexOf('_') + 1)..];
                    bool arrayed = dimensionName == "array";
                    if (arrayed) dimensionName = name[..name.LastIndexOf('_')].Split('_')[^1];
                    ImageDimension dimension = dimensionName switch { "1d" => ImageDimension.D1, "2d" => ImageDimension.D2, "3d" => ImageDimension.D3, "cube" => ImageDimension.Cube, _ => throw Error("Unknown texture dimension.") };
                    if (args.Count != (storage ? 2 : depth ? 0 : 1)) throw Error("Invalid texture template arguments.");
                    string? format = storage ? Word(0) : null;
                    ShaderType.Scalar scalar = storage ? StorageScalar(format!) : depth ? ShaderType.F32 : Scalar(0);
                    result = new ShaderType.Image(dimension, scalar, arrayed, ms, depth, format, storage ? Access(Word(1)) : StorageAccess.Read);
                }
                else throw Error($"Unknown type '{name}'.", syntaxType.Span);
                return result;
            }
            finally { if (userType) resolvingTypes.Remove(name); }
        }

        private ShaderType.Scalar ShorthandScalar(char suffix, SourceSpan span) => suffix switch
        {
            'f' => ShaderType.F32, 'i' => ShaderType.I32, 'u' => ShaderType.U32,
            'h' when syntax.Enables.Contains("f16") => ShaderType.F16, _ => throw Error("Invalid scalar shorthand or missing f16 enable.", span)
        };
        private AddressSpace Space(string name) => name switch
        {
            "function" => AddressSpace.Function, "private" => AddressSpace.Private, "workgroup" => AddressSpace.Workgroup,
            "uniform" => AddressSpace.Uniform, "storage" => AddressSpace.Storage, "immediate" => AddressSpace.Immediate,
            "task_payload" when syntax.Enables.Contains("wgpu_mesh_shader") => AddressSpace.TaskPayload,
            _ => throw Error($"Unknown address space '{name}'.")
        };
        private StorageAccess Access(string name) => name switch
        { "read" => StorageAccess.Read, "write" => StorageAccess.Write, "read_write" => StorageAccess.ReadWrite, "atomic" => StorageAccess.ReadWrite | StorageAccess.Atomic, _ => throw Error($"Unknown storage access '{name}'.") };
        private ShaderType.Scalar StorageScalar(string format) => StorageImageFormats.TryCode(format, out uint code) ? StorageImageFormats.Component(code) : throw Error($"Unknown storage texture format '{format}'.");

        private Symbol ResolveGlobal(string name)
        {
            if (globals.TryGetValue(name, out var symbol)) return symbol;
            var source = syntax.Declarations.FirstOrDefault(d => d.Name == name) ?? throw Error($"Unknown identifier '{name}'.");
            if (!resolving.Add(name)) throw Error($"Cyclic declaration dependency '{name}'.", source.Span);
            try
            {
                CheckAttributes(source.Kind == "var" ? source.Attributes.Where(a => a.Name is not ("coherent" or "volatile")).ToArray() : source.Attributes,
                    source.Kind == "var" ? ["group", "binding"] : ["id"]);
                MemoryDecorations memory = MemoryDecorations.None;
                foreach (var attribute in source.Attributes.Where(a => a.Name is "coherent" or "volatile"))
                {
                    if (attribute.Arguments.Count != 0) throw Error("Memory attributes do not take arguments.", attribute.Span);
                    memory |= attribute.Name == "coherent" ? MemoryDecorations.Coherent : MemoryDecorations.Volatile;
                }
                var block = new Block();
                Expression? initializer = source.Value is null ? null : Eval(source.Value, block);
                ShaderType type = source.Type is not null ? ResolveType(source.Type) : initializer?.Type ?? throw Error("Cannot infer declaration type.");
                if (source.Kind is "var" or "override") type = DefaultType(type);
                if (initializer is not null) initializer = Materialize(initializer, type);
                if (block.Statements.Count != 0) throw Error("Module initializer cannot have runtime side effects.", source.Span);
                if (source.Kind == "const")
                {
                    if (initializer is null || !ConstantEvaluator.TryEvaluate(initializer, out var value)) throw Error("const needs a constant expression.", source.Span);
                    module.Constants.Add(new(name, type, value)); symbol = new(value, false, false);
                }
                else if (source.Kind == "override")
                {
                    if (type is ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint, Width: 8 })
                        throw Error("WGSL override cannot have a 64-bit integer type.", source.Span);
                    module.Constants.Add(new(name, type, initializer, true, UintAttribute(source.Attributes, "id")));
                    symbol = new(new Expression.Reference(name, type), false, false);
                }
                else
                {
                    string Word(int i) => source.Qualifiers[i] is SExpression.Name word ? word.Text : throw Error("Expected variable qualifier.");
                    bool handle = type is ShaderType.Image or ShaderType.Sampler or ShaderType.AccelerationStructure or ShaderType.BindingArray { Element: ShaderType.Image or ShaderType.Sampler or ShaderType.AccelerationStructure };
                    AddressSpace space = source.Qualifiers.Count == 0 ? handle ? AddressSpace.Handle : AddressSpace.Private : Space(Word(0));
                    if (source.Qualifiers.Count > 2 || space == AddressSpace.Function) throw Error("Invalid module variable address space.");
                    StorageAccess access = source.Qualifiers.Count == 2 ? Access(Word(1)) : space is AddressSpace.Uniform or AddressSpace.Storage ? StorageAccess.Read : StorageAccess.ReadWrite;
                    if (initializer is not null && ConstantEvaluator.TryEvaluate(initializer, out var folded)) initializer = folded;
                    uint? group = UintAttribute(source.Attributes, "group"), binding = UintAttribute(source.Attributes, "binding");
                    if (group.HasValue != binding.HasValue) throw Error("Resource group and binding must be specified together.");
                    module.Globals.Add(new(name, type, space, access, group is uint g ? new(g, binding!.Value) : null, initializer) { MemoryDecorations = memory });
                    symbol = new(new Expression.Reference(name, handle ? type : new ShaderType.Pointer(type, space, access)), !handle, space != AddressSpace.Uniform && (access & StorageAccess.Write) != 0);
                }
                globals.Add(name, symbol); return symbol;
            }
            finally { resolving.Remove(name); }
        }

        private static ShaderType DefaultType(ShaderType type) => type switch
        {
            ShaderType.Scalar { Kind: ScalarKind.AbstractInt } => ShaderType.I32,
            ShaderType.Scalar { Kind: ScalarKind.AbstractFloat } => ShaderType.F32,
            ShaderType.Vector v => v with { Component = (ShaderType.Scalar)DefaultType(v.Component) },
            ShaderType.Matrix m => m with { Component = (ShaderType.Scalar)DefaultType(m.Component) },
            ShaderType.Array a => a with { Element = DefaultType(a.Element) }, _ => type
        };

        private static void CheckAttributes(IReadOnlyList<SAttribute> attributes, params string[] allowed)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attribute in attributes)
                if (!allowed.Contains(attribute.Name, StringComparer.Ordinal) || !seen.Add(attribute.Name))
                    throw new ShaderException(DiagnosticStage.Validation, $"Unknown, inapplicable or duplicate attribute '@{attribute.Name}'.", attribute.Span);
        }
        private uint UintExpression(SExpression expression)
        {
            Expression value = Eval(expression, new());
            if (!ConstantEvaluator.TryEvaluate(value, out var constant) || constant is not Expression.Literal literal) throw Error("Expected constant integer.", expression.Span);
            try { return literal.Value switch { uint u => u, int i when i >= 0 => (uint)i, long l when l >= 0 => checked((uint)l), _ => throw Error("Expected nonnegative integer.", expression.Span) }; }
            catch (OverflowException) { throw Error("Integer attribute is out of range.", expression.Span); }
        }
        private uint? UintAttribute(IReadOnlyList<SAttribute> attributes, string name)
        {
            var attribute = attributes.FirstOrDefault(a => a.Name == name);
            if (attribute is null) return null;
            if (attribute.Arguments.Count != 1) throw Error($"@{name} needs one argument.", attribute.Span);
            return UintExpression(attribute.Arguments[0]);
        }
        private IoBinding? Io(IReadOnlyList<SAttribute> attributes, bool member = false)
        {
            if (!member) CheckAttributes(attributes, "location", "builtin", "interpolate", "invariant", "blend_src", "per_primitive");
            string Word(SAttribute attribute, int index) => index < attribute.Arguments.Count && attribute.Arguments[index] is SExpression.Name name && name.Templates.Count == 0 ? name.Text : throw Error("Expected attribute name argument.", attribute.Span);
            var builtin = attributes.FirstOrDefault(a => a.Name == "builtin");
            var interpolation = attributes.FirstOrDefault(a => a.Name == "interpolate");
            var perPrimitive = attributes.FirstOrDefault(a => a.Name == "per_primitive");
            if (perPrimitive is not null && (perPrimitive.Arguments.Count != 0 || !syntax.Enables.Contains("wgpu_mesh_shader"))) throw Error("per_primitive requires wgpu_mesh_shader and no arguments.", perPrimitive.Span);
            uint? location = UintAttribute(attributes, "location");
            if (perPrimitive is not null && location is null) throw Error("per_primitive requires a location binding.", perPrimitive.Span);
            if (builtin is not null && (builtin.Arguments.Count != 1 || location is not null)) throw Error("Invalid IO binding.");
            string? enabledBuiltin = builtin is null ? null : Word(builtin, 0);
            if (enabledBuiltin is "clip_distances" or "draw_index" or "primitive_index" && !syntax.Enables.Contains(enabledBuiltin))
                throw Error($"{enabledBuiltin} requires 'enable {enabledBuiltin};'.", builtin!.Span);
            if (interpolation is not null && interpolation.Arguments.Count is < 1 or > 2) throw Error("Invalid interpolation attribute.");
            if (interpolation is not null && Word(interpolation, 0) == "per_vertex" && !syntax.Enables.Contains("wgpu_per_vertex"))
                throw Error("per_vertex interpolation requires 'enable wgpu_per_vertex;'.", interpolation.Span);
            if (builtin is null && location is null) return null;
            return new(location, builtin is null ? null : Word(builtin, 0), interpolation is null ? null : Word(interpolation, 0),
                interpolation?.Arguments.Count == 2 ? Word(interpolation, 1) : null, attributes.Any(a => a.Name == "invariant"), UintAttribute(attributes, "blend_src"), perPrimitive is not null);
        }
        private void Assert(SExpression expression)
        {
            if (!ConstantEvaluator.TryEvaluate(Eval(expression, new()), out var value) || value is not Expression.Literal { Value: true })
                throw Error("const_assert did not evaluate to true.", expression.Span);
        }
    }
}
