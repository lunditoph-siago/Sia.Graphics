using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Choose target atomic storage from native SSA accesses, after address selection and helper expansion.</summary>
internal static class AtomicStorageLowering
{
    private static bool Native(ValueOperation operation) => operation is ValueOperation.Builtin { AtomicMemory: not null } call
        && (call.Arguments.FirstOrDefault().Type is ShaderType.Pointer { Base: ShaderType.Scalar }
            || call.Function.StartsWith("textureAtomic", StringComparison.Ordinal) && call.Arguments.FirstOrDefault().Type is ShaderType.Image);

    internal static CanonicalModule Run(CanonicalModule input)
    {
        if (!input.Functions.Values.SelectMany(g => g.Blocks).SelectMany(b => b.Instructions).Any(i => Native(i.Operation))) return input;
        ModuleValidator.Validate(input, native: true);
        input = CanonicalReferenceLowering.Run(CanonicalHelperInliner.RunTargetHelpers(input));
        var source = input.Declarations;
        var output = new Module { VulkanMemoryModel = source.VulkanMemoryModel, WorkgroupInitializationRequired = source.WorkgroupInitializationRequired };
        output.Structures.AddRange(source.Structures); output.Constants.AddRange(source.Constants); output.Functions.AddRange(source.Functions);
        output.Enables.UnionWith(source.Enables); output.DiagnosticFilters.AddRange(source.DiagnosticFilters);
        var globals = source.Globals.ToDictionary(g => g.Name, StringComparer.Ordinal);
        var names = source.Structures.Select(s => s.Name).Concat(source.Globals.Select(g => g.Name))
            .Concat(source.Functions.Select(f => f.Name)).Concat(source.Constants.Select(c => c.Name)).ToHashSet(StringComparer.Ordinal);
        string Fresh(string stem) { string name = stem; int index = 0; while (!names.Add(name)) name = stem + "_" + ++index; return name; }
        ShaderException Error(string detail, SourceSpan span = default) => new(DiagnosticStage.Validation, "Canonical atomic storage: " + detail, span);
        ShaderType Upgrade(ShaderType type, IReadOnlyList<string?> path, int depth)
        {
            if (depth == path.Count) return type is ShaderType.Image image ? image with { Access = image.Access | StorageAccess.Atomic }
                : type is ShaderType.Atomic ? type : type is ShaderType.Scalar scalar
                ? new ShaderType.Atomic(scalar) : throw Error("atomic access must end at a scalar");
            if (type is ShaderType.Array array && path[depth] is null) return array with { Element = Upgrade(array.Element, path, depth + 1) };
            if (type is ShaderType.BindingArray bindings && path[depth] is null) return bindings with { Element = Upgrade(bindings.Element, path, depth + 1) };
            if (type is ShaderType.Structure structure && path[depth] is { } member) {
                var members = structure.Members.Select(m => m.Name == member ? m with { Type = Upgrade(m.Type, path, depth + 1) } : m).ToArray();
                if (!structure.Members.Any(m => m.Name == member)) throw Error("unknown atomic member");
                if (members.SequenceEqual(structure.Members)) return type;
                var result = structure with { Name = Fresh("Atomic_" + structure.Name), Members = members };
                output.Structures.Add(result); return result;
            }
            throw Error("atomic address projection does not match its storage type");
        }
        foreach (var graph in input.Functions.Values) {
            var definitions = graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null).ToDictionary(i => i.Result!.Value.Id);
            (string Root, List<string?> Path) Address(SsaValue value) {
                if (!definitions.TryGetValue(value.Id, out var instruction)) throw Error("unresolved atomic address");
                switch (instruction.Operation) {
                    case ValueOperation.Symbol symbol when globals.ContainsKey(symbol.Name): return (symbol.Name, []);
                    case ValueOperation.Let alias: return Address(alias.Value);
                    case ValueOperation.Member member: { var address = Address(member.Base); address.Path.Add(member.Name); return address; }
                    case ValueOperation.Access access when access.Base.Type is ShaderType.Pointer { Base: ShaderType.Array } or ShaderType.BindingArray:
                        { var address = Address(access.Base); address.Path.Add(null); return address; }
                    default: throw Error("atomic address requires resolved global provenance", instruction.Span);
                }
            }
            foreach (var instruction in graph.Blocks.SelectMany(b => b.Instructions).Where(i => Native(i.Operation))) {
                var call = (ValueOperation.Builtin)instruction.Operation;
                var (root, path) = Address(call.Arguments[0]); var global = globals[root];
                globals[root] = global with { Type = Upgrade(global.Type, path, 0) };
            }
        }
        output.Globals.AddRange(source.Globals.Select(g => globals[g.Name]));
        var graphs = new Dictionary<string, ControlFlowFunction>(StringComparer.Ordinal);
        foreach (var (name, original) in input.Functions) {
            var graph = original.Copy();
            var definitions = graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null).ToDictionary(i => i.Result!.Value.Id);
            var values = new Dictionary<int, SsaValue>();
            SsaValue Value(SsaValue value) {
                if (value.Type is not ShaderType.Pointer && !CanonicalTypes.Resource(value.Type)) return value;
                if (values.TryGetValue(value.Id, out var known)) return known;
                var operation = definitions.GetValueOrDefault(value.Id)?.Operation;
                ShaderType Base(SsaValue v) => Value(v).Type is ShaderType.Pointer pointer ? pointer.Base : Value(v).Type;
                ShaderType type = operation switch {
                    ValueOperation.Symbol s when globals.TryGetValue(s.Name, out var global) => global.Type,
                    ValueOperation.Let l => Base(l.Value),
                    ValueOperation.Member m => ((ShaderType.Structure)Base(m.Base)).Members.Single(f => f.Name == m.Name).Type,
                    ValueOperation.Access a when Base(a.Base) is ShaderType.Array array => array.Element,
                    ValueOperation.Access a when Base(a.Base) is ShaderType.BindingArray bindings => bindings.Element,
                    _ => value.Type is ShaderType.Pointer original ? original.Base : value.Type
                };
                var result = value with { Type = value.Type is ShaderType.Pointer pointer ? pointer with { Base = type } : type };
                values.Add(value.Id, result); return result;
            }
            foreach (var block in graph.Blocks.ToArray()) {
                var old = block.Instructions.ToArray(); var terminator = block.Terminator!;
                block.Instructions.Clear(); var current = block;
                foreach (var originalInstruction in old) {
                    var instruction = originalInstruction with { Result = originalInstruction.Result is { } result ? Value(result) : null,
                        Operation = originalInstruction.Operation.Map(Value) };
                    SsaValue Emit(ShaderType type, ValueOperation operation) {
                        var value = graph.Value(type); current.Instructions.Add(new(value, operation, instruction.Span) { DiagnosticFilters = instruction.DiagnosticFilters }); return value;
                    }
                    void Effect(ValueOperation operation) => current.Instructions.Add(new(null, operation, instruction.Span) { DiagnosticFilters = instruction.DiagnosticFilters });
                    SsaValue Member(SsaValue parent, string member) {
                        var type = parent.Type is ShaderType.Pointer p ? p.Base : parent.Type;
                        var field = ((ShaderType.Structure)type).Members.Single(m => m.Name == member).Type;
                        return Emit(parent.Type is ShaderType.Pointer pointer ? pointer with { Base = field } : field, new ValueOperation.Member(parent, member));
                    }
                    SsaValue Index(SsaValue parent, SsaValue index) {
                        var type = parent.Type is ShaderType.Pointer p ? p.Base : parent.Type;
                        var element = ((ShaderType.Array)type).Element;
                        return Emit(parent.Type is ShaderType.Pointer pointer ? pointer with { Base = element } : element, new ValueOperation.Access(parent, index));
                    }
                    void Copy(SsaValue memory, SsaValue data, ShaderType logical, bool load, SpirvMemoryAccess? access) {
                        if (logical is ShaderType.Structure structure) {
                            foreach (var member in structure.Members) Copy(Member(memory, member.Name), Member(data, member.Name), member.Type, load, access?.Leaf());
                            return;
                        }
                        if (logical is ShaderType.Array { Length: uint length } array) {
                            var zero = Emit(ShaderType.U32, new ValueOperation.Literal(0u));
                            var count = Emit(ShaderType.U32, new ValueOperation.Literal(length));
                            var header = graph.Block(); var body = graph.Block(); var continuing = graph.Block(); var end = graph.Block();
                            var index = graph.Value(ShaderType.U32); header.Parameters.Add(index);
                            current.Terminator = new ControlFlowTerminator.Branch(new(header.Id, [zero]));
                            current = header;
                            var condition = Emit(ShaderType.Bool, new ValueOperation.Binary("<", index, count));
                            header.Terminator = new ControlFlowTerminator.Conditional(condition, new(body.Id), new(end.Id));
                            graph.Loops.Add(header.Id, new(continuing.Id, end.Id)); current = body;
                            Copy(Index(memory, index), Index(data, index), array.Element, load, access?.Leaf());
                            current.Terminator = new ControlFlowTerminator.Branch(new(continuing.Id)); current = continuing;
                            var next = Emit(ShaderType.U32, new ValueOperation.Binary("+", index, Emit(ShaderType.U32, new ValueOperation.Literal(1u))));
                            continuing.Terminator = new ControlFlowTerminator.Branch(new(header.Id, [next])); current = end; return;
                        }
                        bool atomic = ((ShaderType.Pointer)memory.Type).Base is ShaderType.Atomic;
                        if (load) {
                            var value = Emit(logical, atomic ? new ValueOperation.Builtin("atomicLoad", [memory], logical, MemoryAccess: access ?? new(0)) : new ValueOperation.Load(memory, access));
                            Effect(new ValueOperation.Store(data, value));
                        }
                        else Effect(atomic ? new ValueOperation.Builtin("atomicStore", [memory, data], new ShaderType.Void(), MemoryAccess: access ?? new(0)) : new ValueOperation.Store(memory, data, access));
                    }
                    if (instruction is { Operation: ValueOperation.Load load, Result: { } loaded }
                        && ((ShaderType.Pointer)load.Pointer.Type).Base != loaded.Type) {
                        if (((ShaderType.Pointer)load.Pointer.Type).Base is ShaderType.Atomic) {
                            current.Instructions.Add(instruction with { Operation = new ValueOperation.Builtin("atomicLoad", [load.Pointer], loaded.Type, MemoryAccess: load.MemoryAccess ?? new(0)) });
                        }
                        else {
                            var temporary = Emit(new ShaderType.Pointer(loaded.Type, AddressSpace.Function), new ValueOperation.Local("sia_atomic_snapshot_" + loaded.Id, false));
                            Copy(load.Pointer, temporary, loaded.Type, true, load.MemoryAccess?.Leaf());
                            current.Instructions.Add(instruction with { Operation = new ValueOperation.Load(temporary) });
                        }
                    }
                    else if (instruction.Operation is ValueOperation.Store store && ((ShaderType.Pointer)store.Pointer.Type).Base != store.Value.Type)
                        Copy(store.Pointer, store.Value, store.Value.Type, false, store.MemoryAccess);
                    else current.Instructions.Add(instruction);
                }
                current.Terminator = terminator.Map(Value);
                foreach (var edge in current.Terminator.Edges)
                    for (int i = 0; i < edge.Arguments.Count; i++) edge.Arguments[i] = Value(edge.Arguments[i]);
                if (current != block && graph.SelectionMerges.Remove(block.Id, out var merge)) graph.SelectionMerges.Add(current.Id, merge);
            }
            LocalValuePromotion.Run(graph); graphs.Add(name, graph);
        }
        var lowered = input with { Declarations = output, Functions = graphs };
        ShaderEffectAnalysis.RefreshCalls(lowered); ModuleValidator.Validate(lowered, native: true); return lowered;
    }
}
