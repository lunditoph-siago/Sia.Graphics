using System.Globalization;
using System.Text;
using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Translation.Valid;

/// <summary>Checks the shared IR independently of either source-language parser.</summary>
public static partial class ModuleValidator
{
    public static void Validate(Module module)
    {
        ArgumentNullException.ThrowIfNull(module);
        new Validator(module).Run();
    }

    private sealed partial class Validator(Module module)
    {
        private const int Vertex = 1, Fragment = 2, Compute = 4, Task = 8, Mesh = 16, WorkgroupStages = Compute | Task | Mesh, AllStages = 31;
        private readonly Dictionary<string, Variable> globals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ShaderFunction> functions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> calls = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> stages = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> payloadUses = new(StringComparer.Ordinal);
        private readonly HashSet<ShaderType> checkedTypes = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<ShaderType> visitingTypes = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<string> arrayLengthOverrides = new(StringComparer.Ordinal);
        private readonly Stack<Dictionary<string, Variable>> scopes = new();
        private readonly Stack<BreakTarget> breaks = new();
        private ShaderFunction? function;
        private int loopDepth, expressionDepth;
        private sealed record Variable(ShaderType Type, bool Place, bool Writable, AddressSpace Space = AddressSpace.Function);
        private sealed class BreakTarget(bool loop) { public bool Loop { get; } = loop; public bool Used; }
        private ShaderException Error(string message, SourceSpan span = default) => new(DiagnosticStage.Validation, message, span);
        private void Require(bool condition, string message, SourceSpan span = default) { if (!condition) throw Error(message, span); }

        public void Run()
        {
            DiagnosticFilters(module.DiagnosticFilters);
            foreach (string enable in module.Enables) Require(WgslExtensions.Enable(enable), "Unknown or unimplemented WGSL enable extension.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var structure in module.Structures) { Require(names.Add(structure.Name), "Duplicate module name."); Type(structure); }
            var overrideIds = new HashSet<uint>();
            var constantsByName = new Dictionary<string, ShaderConstant>(StringComparer.Ordinal);
            foreach (var constant in module.Constants) Require(constantsByName.TryAdd(constant.Name, constant), "Duplicate module name.");
            var overrideExpressions = new OverrideExpressions.Checker(constantsByName);
            foreach (var constant in module.Constants)
            {
                Require(names.Add(constant.Name), "Duplicate module name."); Type(constant.Type);
                Require(!PipelineSized(constant.Type), "Constants cannot have override-sized array types.");
                Require(!constant.IsSpecialization || !constant.IsOverride && constant.OverrideId is null && constant.Value is not null,
                    "Derived specialization values require an initializer and cannot have an override ID or be independently overridden.");
                if (constant.IsOverride)
                {
                    Require(constant.Type is ShaderType.Scalar { Kind: not (ScalarKind.AbstractFloat or ScalarKind.AbstractInt) }, "Override must have a concrete scalar type.");
                    if (constant.OverrideId is uint id) Require(id <= ushort.MaxValue && overrideIds.Add(id), "Invalid or duplicate override ID.");
                    if (constant.Value is not null) Require(overrideExpressions.IsValid(constant.Value), "Override initializer is not an override expression or has cyclic dependencies.");
                }
                else if (constant.IsSpecialization)
                {
                    Require(constant.Type is ShaderType.Scalar { Kind: not (ScalarKind.AbstractFloat or ScalarKind.AbstractInt) }, "Derived specialization must have a concrete scalar type.");
                    Require(overrideExpressions.IsValid(constant.Value!), "Invalid derived specialization expression or cyclic dependency.");
                }
                else Require(constant.Value is not null && ConstantEvaluator.TryEvaluate(constant.Value, out _), "const initializer is not constant.");
                globals.Add(constant.Name, new(constant.Type, false, false));
            }
            foreach (var global in module.Globals)
            {
                Require(names.Add(global.Name), "Duplicate module name."); Type(global.Type);
                bool handle = global.Type is ShaderType.Image or ShaderType.Sampler or ShaderType.AccelerationStructure or ShaderType.BindingArray { Element: ShaderType.Image or ShaderType.Sampler or ShaderType.AccelerationStructure };
                Require(global.Type is not ShaderType.RayQuery, "Ray queries require function-local variables.");
                Require(!ContainsCooperative(global.Type) || global.Space == AddressSpace.Private, "Cooperative matrix variables require private or function address space.");
                Require(global.Space != AddressSpace.Function, "Module variable cannot use function address space.");
                if (global.Type is ShaderType.Array { OverrideLength: not null }) Require(global.Space == AddressSpace.Workgroup, "Override-sized data arrays require workgroup address space.");
                Require((global.MemoryDecorations & ~(MemoryDecorations.Coherent | MemoryDecorations.Volatile)) == 0, "Unknown memory decoration.");
                Require(global.MemoryDecorations == MemoryDecorations.None || global.Space == AddressSpace.Storage, "Memory decorations require storage address space.");
                Require((global.Space == AddressSpace.Handle) == handle, "Opaque resource must use handle address space.");
                if (global.Type is ShaderType.BindingArray && !handle) Require(global.Space is AddressSpace.Uniform or AddressSpace.Storage, "Buffer binding arrays require uniform or storage address space.");
                bool resource = handle || global.Space is AddressSpace.Uniform or AddressSpace.Storage;
                Require(resource == (global.Binding is not null), "Resource variable requires group/binding, and ordinary variables cannot have one.");
                if (global.Space is AddressSpace.Uniform or AddressSpace.Storage or AddressSpace.Immediate)
                    Require(HostShareable(global.Type), "Buffer type is not host-shareable.");
                if (ContainsAtomic(global.Type)) Require(global.Space is AddressSpace.Workgroup or AddressSpace.TaskPayload || global.Space == AddressSpace.Storage && (global.Access & StorageAccess.ReadWrite) == StorageAccess.ReadWrite, "Atomic data requires workgroup, task payload or read-write storage memory.");
                if (RuntimeSized(global.Type)) Require(global.Space == AddressSpace.Storage, "Runtime array requires storage memory.");
                if (global.Space == AddressSpace.Storage) Require(global.Access is StorageAccess.Read or StorageAccess.ReadWrite || global.Access == (StorageAccess.ReadWrite | StorageAccess.Atomic), "Invalid storage buffer access.");
                if (global.Space == AddressSpace.TaskPayload) Require(Data(global.Type) && TypeLayout.Of(global.Type).Size > 0, "Task payload requires nonempty sized data.");
                if (global.Initializer is not null)
                {
                    Require(global.Space == AddressSpace.Private, "Only private module variables can have initializers.");
                    Require(overrideExpressions.IsValid(global.Initializer), "Global initializer is not a constant or override expression.");
                }
                globals.Add(global.Name, new(global.Type, !handle, global.Space != AddressSpace.Uniform && (global.Access & StorageAccess.Write) != 0, global.Space));
            }
            foreach (var f in module.Functions)
            {
                DiagnosticFilters(f.DiagnosticFilters);
                Require(names.Add(f.Name), "Duplicate module name."); functions.Add(f.Name, f); calls.Add(f.Name, new(StringComparer.Ordinal)); stages.Add(f.Name, AllStages);
                payloadUses.Add(f.Name, new(StringComparer.Ordinal));
            }
            foreach (var constant in module.Constants) if (constant.Value is not null) Same(Expr(constant.Value), constant.Type, "Constant initializer type mismatch.");
            foreach (var global in module.Globals) if (global.Initializer is not null) Same(Expr(global.Initializer), global.Type, "Global initializer type mismatch.");
            foreach (var f in module.Functions)
            {
                function = f; scopes.Push(new(StringComparer.Ordinal));
                Type(f.ReturnType);
                Require(f.ReturnType is not (ShaderType.RayQuery or ShaderType.AccelerationStructure), "Ray handles cannot be returned from functions.");
                Require(!PipelineSized(f.ReturnType), "Function return type must be constructible before pipeline constant resolution.");
                foreach (var argument in f.Arguments)
                {
                    Type(argument.Type);
                    Require(argument.Type is not ShaderType.RayQuery, "Ray queries cannot be passed by value.");
                    Require(argument.Type is not ShaderType.Pointer pointer || pointer.Space is AddressSpace.Function or AddressSpace.Private, "Function pointer parameters require function or private address space in the reference version.");
                    Require(!RuntimeSized(argument.Type) && !ContainsAtomic(argument.Type), "Function parameter cannot contain unsized or atomic data.");
                    Require(scopes.Peek().TryAdd(argument.Name, new(argument.Type, false, false)), "Duplicate parameter name.");
                }
                bool fallthrough = Body(f.Body, false);
                Require(f.ReturnType is ShaderType.Void || !fallthrough, "Function can reach its end without returning a value.");
                scopes.Pop();
                if (f.Stage is ShaderStage stage) Entry(f, stage);
                else
                {
                    Require(f.ReturnBinding is null && f.Arguments.All(a => a.Binding is null), "Non-entry function cannot have IO bindings.");
                    Require(!f.EarlyDepthTest, "Early depth test requires a fragment entry point.");
                    Require(f.TaskPayload is null && f.MeshOutput is null, "Non-entry functions cannot have mesh/payload attributes.");
                }
            }
            function = null;
            var resolved = new Dictionary<string, int>(StringComparer.Ordinal); var active = new HashSet<string>(StringComparer.Ordinal);
            int Resolve(string name)
            {
                if (resolved.TryGetValue(name, out int known)) return known;
                Require(active.Add(name), "Recursive shader function calls are forbidden.");
                int allowed = stages[name]; foreach (string callee in calls[name]) { allowed &= Resolve(callee); payloadUses[name].UnionWith(payloadUses[callee]); }
                active.Remove(name); resolved.Add(name, allowed); return allowed;
            }
            foreach (var f in module.Functions)
            {
                int allowed = Resolve(f.Name);
                if (f.Stage is ShaderStage stage)
                {
                    Require((allowed & (stage switch { ShaderStage.Vertex => Vertex, ShaderStage.Fragment => Fragment, ShaderStage.Task => Task, ShaderStage.Mesh => Mesh, _ => Compute })) != 0, $"Entry '{f.Name}' calls an operation unavailable in its stage.");
                    Require(payloadUses[f.Name].All(name => name == f.TaskPayload), "Entry uses a task payload not declared by its payload attribute.");
                    if (f.TaskPayload is string payload) Require(TypeLayout.Of(module.Globals.Single(g => g.Name == payload).Type).Size >= 4, "Selected task payload requires at least four bytes.");
                }
            }
            foreach (string name in arrayLengthOverrides)
                Require(constantsByName.TryGetValue(name, out var length) && (length.IsOverride || length.IsSpecialization)
                    && length.Type is ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint }, "Array length must refer to an integer override.");
        }

        private void DiagnosticFilters(IEnumerable<DiagnosticFilter> filters)
        {
            var rules = new HashSet<(string?, string)>();
            foreach (var filter in filters)
            {
                Require(filter is not null && Enum.IsDefined(filter.Severity), "Invalid diagnostic severity.");
                Require(DiagnosticIdentifier(filter!.Rule) && (filter.Namespace is null || DiagnosticIdentifier(filter.Namespace)), "Invalid diagnostic rule name.");
                Require(rules.Add((filter.Namespace, filter.Rule)), "Duplicate diagnostic filter in the same scope.");
            }
        }

        private static bool DiagnosticIdentifier(string? value)
        {
            if (string.IsNullOrEmpty(value) || value == "_" || value.StartsWith("__", StringComparison.Ordinal)) return false;
            int index = 0;
            while (index < value.Length)
            {
                if (!Rune.TryGetRuneAt(value, index, out var rune)) return false;
                var category = Rune.GetUnicodeCategory(rune);
                bool start = rune.Value == '_' || category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                    or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber
                    || rune.Value is 0x1885 or 0x1886 or 0x2118 or 0x212e or 0x309b or 0x309c;
                bool continuation = category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                    or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation
                    || rune.Value is 0xb7 or 0x387 or 0x19da or >= 0x1369 and <= 0x1371;
                if (!start && (index == 0 || !continuation)) return false;
                index += rune.Utf16SequenceLength;
            }
            return true;
        }

        private void Type(ShaderType type)
        {
            if (checkedTypes.Contains(type)) return;
            Require(visitingTypes.Add(type), "Recursive data type.");
            switch (type)
            {
                case ShaderType.Void: break;
                case ShaderType.Scalar s:
                    Require(s.Kind switch { ScalarKind.Bool => s.Width == 1, ScalarKind.Float => s.Width is 2 or 4 or 8,
                        ScalarKind.Sint or ScalarKind.Uint => s.Width is 2 or 4 or 8, ScalarKind.AbstractFloat or ScalarKind.AbstractInt => s.Width == 8, _ => false }, "Invalid scalar width or kind."); break;
                case ShaderType.Vector v: Type(v.Component); Require(v.Size is >= 2 and <= 4, "Invalid vector size."); break;
                case ShaderType.Matrix m:
                    Type(m.Component); Require(m.Columns is >= 2 and <= 4 && m.Rows is >= 2 and <= 4 && m.Component.Kind is ScalarKind.Float or ScalarKind.AbstractFloat, "Invalid matrix type."); break;
                case ShaderType.Atomic a: Type(a.Component); Require(a.Component is { Kind: ScalarKind.Sint or ScalarKind.Uint, Width: 4 or 8 } or { Kind: ScalarKind.Float, Width: 4 }, "Invalid atomic component."); break;
                case ShaderType.CooperativeMatrix m:
                    Type(m.Component); Require(m.Columns is 8 or 16 && m.Rows is 8 or 16 && m.Component is { Kind: ScalarKind.Float, Width: 2 or 4 }
                        && Enum.IsDefined(m.Role) && m.Scope is 2 or 3, "Invalid cooperative matrix type."); break;
                case ShaderType.Pointer p:
                    Type(p.Base); Require(p.Space != AddressSpace.Handle && (p.Access is StorageAccess.Read or StorageAccess.Write or StorageAccess.ReadWrite || p.Access == (StorageAccess.ReadWrite | StorageAccess.Atomic)) && p.Base is not ShaderType.Void, "Invalid pointer type."); break;
                case ShaderType.Array a:
                    Type(a.Element); Require(a.Length != 0 && !RuntimeSized(a.Element) && !PipelineSized(a.Element) && Data(a.Element), "Invalid array element or size.");
                    if (a.OverrideLength is string length)
                    {
                        Require(a.Length is null, "Array cannot have both a constant and override length."); arrayLengthOverrides.Add(length);
                        var layout = TypeLayout.Of(a.Element); uint stride = a.Stride ?? layout.Stride;
                        Require(stride >= layout.Size && stride % layout.Alignment == 0, "Invalid array stride.");
                    }
                    else _ = TypeLayout.Of(a);
                    break;
                case ShaderType.Structure s:
                    if (s.BuiltinResult is BuiltinResultKind.RayDesc or BuiltinResultKind.RayIntersection)
                        Require(RayQueryTypes.SameStructure(s, s.BuiltinResult == BuiltinResultKind.RayDesc ? RayQueryTypes.Descriptor : RayQueryTypes.Intersection), "Invalid predeclared ray structure.");
                    Require(s.Members.Count != 0 && s.Members.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count() == s.Members.Count, "Empty structure or duplicate member.");
                    foreach (var m in s.Members)
                    {
                        Type(m.Type); Require(Data(m.Type) && !PipelineSized(m.Type), "Invalid structure member type.");
                        Require((m.MemoryDecorations & ~(MemoryDecorations.Coherent | MemoryDecorations.Volatile)) == 0, "Unknown member memory decoration.");
                    }
                    _ = TypeLayout.Of(s); break;
                case ShaderType.AccelerationStructure: case ShaderType.RayQuery: case ShaderType.Sampler: break;
                case ShaderType.Image image:
                    Type(image.Component); Require(image.StorageFormat == "r64uint" ? image.Component == new ShaderType.Scalar(ScalarKind.Uint, 8) : image.Component.Kind is ScalarKind.Float or ScalarKind.Sint or ScalarKind.Uint && image.Component.Width == 4, "Invalid image scalar.");
                    if (image.StorageFormat is string format)
                    {
                        Require(StorageImageFormats.TryCode(format, out uint code), "Unknown storage image format.");
                        Same(image.Component, StorageImageFormats.Component(code), "Storage image component does not match its format.");
                    }
                    Require(image.Access is StorageAccess.Read or StorageAccess.Write or StorageAccess.ReadWrite || image.StorageFormat is not null && image.Access == (StorageAccess.ReadWrite | StorageAccess.Atomic), "Invalid image access.");
                    Require(!image.Multisampled || image.Dimension == ImageDimension.D2 && !image.Arrayed, "Multisampled images must be non-array 2D.");
                    Require(!image.Arrayed || image.Dimension is ImageDimension.D2 or ImageDimension.Cube, "Invalid arrayed image dimension.");
                    Require(!image.Depth || image.Component == ShaderType.F32 && image.StorageFormat is null && image.Dimension is ImageDimension.D2 or ImageDimension.Cube, "Invalid depth image."); break;
                case ShaderType.BindingArray a:
                    Type(a.Element); Require(a.Length != 0 && !PipelineSized(a.Element), "Invalid binding array element or length.");
                    if (a.OverrideLength is string count)
                    { Require(a.Length is null, "Binding array cannot have both a constant and override length."); arrayLengthOverrides.Add(count); }
                    break;
                default: throw Error("Unknown type variant.");
            }
            visitingTypes.Remove(type); checkedTypes.Add(type);
        }

        private static bool Data(ShaderType type) => type is ShaderType.Scalar or ShaderType.Vector or ShaderType.Matrix or ShaderType.CooperativeMatrix or ShaderType.Array or ShaderType.Structure or ShaderType.Atomic;
        private static bool ContainsCooperative(ShaderType type) => type switch
        {
            ShaderType.CooperativeMatrix => true, ShaderType.Array a => ContainsCooperative(a.Element),
            ShaderType.Structure s => s.Members.Any(m => ContainsCooperative(m.Type)), _ => false
        };
        private static bool RuntimeSized(ShaderType type) => type switch { ShaderType.Array a => a.Length is null && a.OverrideLength is null, ShaderType.Structure s => s.Members.Any(m => RuntimeSized(m.Type)), _ => false };
        private static bool PipelineSized(ShaderType type) => type is ShaderType.Array { OverrideLength: not null } or ShaderType.BindingArray { OverrideLength: not null };
        private static bool ContainsAtomic(ShaderType type) => type switch { ShaderType.Atomic => true, ShaderType.Array a => ContainsAtomic(a.Element), ShaderType.BindingArray a => ContainsAtomic(a.Element), ShaderType.Structure s => s.Members.Any(m => ContainsAtomic(m.Type)), _ => false };
        private static bool HostShareable(ShaderType type) => type switch
        {
            ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint or ScalarKind.Float } => true,
            ShaderType.Vector v => HostShareable(v.Component), ShaderType.Matrix m => HostShareable(m.Component),
            ShaderType.Atomic a => HostShareable(a.Component), ShaderType.Array a => HostShareable(a.Element), ShaderType.BindingArray a => HostShareable(a.Element),
            ShaderType.Structure s => s.Members.All(m => HostShareable(m.Type)), _ => false
        };
        private void Same(ShaderType actual, ShaderType expected, string message, SourceSpan span = default) => Require(actual == expected, message, span);
        private static ShaderType DataType(ShaderType type) => type is ShaderType.Pointer p ? p.Base : type;
        private static ShaderType.Scalar? Scalar(ShaderType type) => type switch { ShaderType.Scalar s => s, ShaderType.Vector v => v.Component, ShaderType.Matrix m => m.Component, ShaderType.CooperativeMatrix m => m.Component, _ => null };
        private static bool Numeric(ShaderType type) => Scalar(type)?.Kind is ScalarKind.Sint or ScalarKind.Uint or ScalarKind.Float or ScalarKind.AbstractInt or ScalarKind.AbstractFloat;
        private static bool Integer(ShaderType type) => Scalar(type)?.Kind is ScalarKind.Sint or ScalarKind.Uint or ScalarKind.AbstractInt;
        private void Restrict(int mask) { if (function is not null) stages[function.Name] &= mask; }
        private Variable Lookup(string name)
        {
            foreach (var scope in scopes) if (scope.TryGetValue(name, out var local)) return local;
            if (!globals.TryGetValue(name, out var global)) throw Error($"Unknown reference '{name}'.");
            if (global.Space == AddressSpace.Workgroup) Restrict(WorkgroupStages);
            if (global.Space == AddressSpace.TaskPayload)
            {
                Restrict(Task | Mesh);
                if (function is not null) payloadUses[function.Name].Add(name);
            }
            return global;
        }

        private void Entry(ShaderFunction entry, ShaderStage stage)
        {
            var inputs = new HashSet<string>(StringComparer.Ordinal); var outputs = new HashSet<string>(StringComparer.Ordinal);
            void Io(ShaderType type, IoBinding? binding, bool input, int meshKind = 0)
            {
                if (type is ShaderType.Structure s)
                {
                    Require(binding is null, "Structure IO binding belongs on its members.");
                    foreach (var member in s.Members) { Require(member.Type is not ShaderType.Structure, "Nested IO structures are forbidden."); Io(member.Type, member.Binding, input, meshKind); }
                    return;
                }
                Require(binding is not null && binding.Location.HasValue != (binding.Builtin is not null), "IO requires exactly one location or builtin.");
                string key = binding!.Builtin is string name ? "b:" + name : "l:" + binding.Location + ":" + binding.BlendSource;
                Require((input ? inputs : outputs).Add(key), "Duplicate entry IO binding.");
                Require(!binding.PerPrimitive || binding.Location.HasValue && (stage == ShaderStage.Fragment && input || stage == ShaderStage.Mesh && !input && meshKind == 2), "Per-primitive location requires a fragment input or mesh primitive output.");
                if (binding.Location.HasValue)
                {
                    bool perVertex = binding.Interpolation == "per_vertex";
                    ShaderType valueType = type;
                    if (perVertex)
                    {
                        Require(stage == ShaderStage.Fragment && input, "Per-vertex interpolation requires a fragment input.");
                        Require(type is ShaderType.Array { Length: 3 }, "Per-vertex input requires an array of three elements.");
                        Require(binding.Sampling is null, "Per-vertex interpolation does not accept explicit sampling in the reference version.");
                        valueType = ((ShaderType.Array)type).Element;
                    }
                    Require(stage is ShaderStage.Vertex or ShaderStage.Fragment or ShaderStage.Mesh && valueType is ShaderType.Scalar or ShaderType.Vector && Numeric(valueType), "Location IO requires numeric scalar/vector in a graphics stage.");
                    Require(meshKind != 2 || binding.PerPrimitive, "Mesh primitive locations require per_primitive.");
                    Require(binding.Interpolation is null or "flat" or "linear" or "perspective" or "per_vertex", "Invalid interpolation mode.");
                    if (!perVertex && Integer(type))
                    {
                        Require(binding.Interpolation is null or "flat", "Integer IO requires flat interpolation.");
                        if (stage == ShaderStage.Fragment && input || stage is ShaderStage.Vertex or ShaderStage.Mesh && !input)
                            Require(binding.Interpolation == "flat", "Integer inter-stage IO requires explicit flat interpolation.");
                    }
                    Require(binding.Sampling is null or "center" or "centroid" or "sample" or "first" or "either", "Invalid interpolation sampling.");
                }
                else
                {
                    (ShaderType Expected, bool Allowed) rule = binding.Builtin switch
                    {
                        "position" => (new ShaderType.Vector(4, ShaderType.F32), stage == ShaderStage.Vertex && !input || stage == ShaderStage.Mesh && !input && meshKind == 1 || stage == ShaderStage.Fragment && input),
                        "vertex_index" or "instance_index" => (ShaderType.U32, stage == ShaderStage.Vertex && input),
                        "draw_index" => (ShaderType.U32, input && (stage is ShaderStage.Vertex or ShaderStage.Task || stage == ShaderStage.Mesh && entry.TaskPayload is null)),
                        "view_index" => (ShaderType.U32, input && stage is ShaderStage.Vertex or ShaderStage.Fragment or ShaderStage.Mesh),
                        "barycentric" or "barycentric_no_perspective" => (new ShaderType.Vector(3, ShaderType.F32), stage == ShaderStage.Fragment && input),
                        "clip_distances" or "cull_distance" => (type, !input && (stage == ShaderStage.Vertex || stage == ShaderStage.Mesh && meshKind == 1) && type is ShaderType.Array { Element: var element, Length: >= 1 and <= 8 } && element == ShaderType.F32),
                        "front_facing" => (ShaderType.Bool, stage == ShaderStage.Fragment && input),
                        "frag_depth" => (ShaderType.F32, stage == ShaderStage.Fragment && !input),
                        "sample_index" => (ShaderType.U32, stage == ShaderStage.Fragment && input),
                        "primitive_index" => (ShaderType.U32, stage == ShaderStage.Fragment && input || stage == ShaderStage.Mesh && !input && meshKind == 2),
                        "sample_mask" => (ShaderType.U32, stage == ShaderStage.Fragment),
                        "global_invocation_id" or "local_invocation_id" or "workgroup_id" or "num_workgroups" => (new ShaderType.Vector(3, ShaderType.U32), stage is ShaderStage.Compute or ShaderStage.Task or ShaderStage.Mesh && input),
                        "local_invocation_index" => (ShaderType.U32, stage is ShaderStage.Compute or ShaderStage.Task or ShaderStage.Mesh && input),
                        "subgroup_size" or "subgroup_invocation_id" => (ShaderType.U32, input && stage is ShaderStage.Compute or ShaderStage.Fragment or ShaderStage.Task or ShaderStage.Mesh),
                        "num_subgroups" or "subgroup_id" => (ShaderType.U32, input && stage is ShaderStage.Compute or ShaderStage.Task or ShaderStage.Mesh),
                        "point_size" => (ShaderType.F32, !input && (stage == ShaderStage.Vertex || stage == ShaderStage.Mesh && meshKind == 1)),
                        "mesh_task_size" => (new ShaderType.Vector(3, ShaderType.U32), stage == ShaderStage.Task && !input),
                        "point_index" => (ShaderType.U32, stage == ShaderStage.Mesh && meshKind == 2 && !input),
                        "line_indices" => (new ShaderType.Vector(2, ShaderType.U32), stage == ShaderStage.Mesh && meshKind == 2 && !input),
                        "triangle_indices" => (new ShaderType.Vector(3, ShaderType.U32), stage == ShaderStage.Mesh && meshKind == 2 && !input),
                        "cull_primitive" => (ShaderType.Bool, stage == ShaderStage.Mesh && meshKind == 2 && !input),
                        _ => throw Error($"Unsupported builtin IO '{binding.Builtin}'.")
                    };
                    Require(rule.Allowed, "Builtin is unavailable at this stage or IO direction."); Same(type, rule.Expected, "Builtin has the wrong type.");
                }
            }
            foreach (var argument in entry.Arguments) Io(argument.Type, argument.Binding, true);
            if (entry.ReturnType is not ShaderType.Void) Io(entry.ReturnType, entry.ReturnBinding, false);
            if (stage == ShaderStage.Vertex) Require(outputs.Contains("b:position"), "Vertex entry must output position.");
            Require(entry.TaskPayload is null || stage is ShaderStage.Task or ShaderStage.Mesh, "Payload attribute requires a mesh/task entry.");
            if (entry.TaskPayload is string payload) Require(module.Globals.Any(g => g.Name == payload && g.Space == AddressSpace.TaskPayload), "Payload attribute requires a task_payload variable.");
            Require((stage == ShaderStage.Mesh) == (entry.MeshOutput is not null), "Mesh output attribute requires a mesh entry.");
            if (stage == ShaderStage.Task) Require(entry.TaskPayload is not null && entry.ReturnBinding == new IoBinding(Builtin: "mesh_task_size") && entry.ReturnType == new ShaderType.Vector(3, ShaderType.U32), "Task entry requires a payload and mesh_task_size vec3u result.");
            if (stage == ShaderStage.Mesh)
            {
                Require(entry.ReturnType is ShaderType.Void, "Mesh entry cannot return a value.");
                var info = MeshShaderInfo.Inspect(module, entry);
                Io(info.Vertices.Element, null, false, 1);
                Require(outputs.Contains("b:position"), "Mesh vertex outputs require position.");
                Io(info.Primitives.Element, null, false, 2);
            }
            Require(!entry.EarlyDepthTest || stage == ShaderStage.Fragment, "Early depth test requires a fragment entry.");
            if (stage is ShaderStage.Compute or ShaderStage.Task or ShaderStage.Mesh)
            {
                Require((stage == ShaderStage.Task || entry.ReturnType is ShaderType.Void) && entry.WorkgroupSize.Length == 3, "Workgroup entry has invalid result or workgroup size.");
                foreach (var size in entry.WorkgroupSize)
                {
                    Expr(size);
                    Require(size.Type is ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint, Width: 4 }, "Workgroup dimension must be an i32 or u32 scalar.");
                    Require(OverrideExpressions.IsValid(size, module.Constants.ToDictionary(c => c.Name, StringComparer.Ordinal)), "Workgroup dimension is not a constant or override expression.");
                    if (ConstantEvaluator.TryEvaluate(size, out var value) && value is Expression.Literal literal)
                        Require(System.Convert.ToInt64(literal.Value) > 0, "Workgroup dimension must be positive.");
                }
            }
        }
    }
}
