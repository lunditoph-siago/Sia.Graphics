using System.Collections.Frozen;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;

namespace Sia.Spirv.Compiler.Translation.Legalization;

// Buffer layout belongs to addresses. Aggregate SSA values and ordinary memory
// use logical types; ordered loads/stores cross that boundary exactly once.
internal sealed partial class SpirvValueLayoutLowering(SpirvPhysicalLayout input,
    IDictionary<string, ControlFlowFunction> graphs)
{
    private readonly Dictionary<ShaderType, ShaderType> values = [];
    private readonly Dictionary<ShaderType, ShaderType> physical = input.BufferTypes.ToDictionary(p => p.Key, p => p.Value);
    private readonly Dictionary<ShaderType, SpirvBufferLayout> buffers = input.BufferTypes.Count == 0 ? [] : input.Buffers.ToDictionary(p => p.Key, p => p.Value);
    private readonly Dictionary<(ShaderType From, ShaderType To), ShaderFunction> conversions = input.ValueConversions.ToDictionary(p => p.Key, p => p.Value);
    private readonly List<ShaderFunction> helpers = [];
    private readonly HashSet<string> names = input.Module.Functions.Select(f => f.Name)
        .Concat(input.Module.Globals.Select(g => g.Name)).Concat(input.Module.Constants.Select(c => c.Name))
        .Concat(input.Module.Structures.Select(s => s.Name)).ToHashSet(StringComparer.Ordinal);
    private readonly Module output = new() { VulkanMemoryModel = input.Module.VulkanMemoryModel,
        WorkgroupInitializationRequired = input.Module.WorkgroupInitializationRequired };

    private string Name(string stem) { string name = stem; int suffix = 0; while (!names.Add(name)) name = stem + "_" + ++suffix; return name; }
    private static ShaderType Data(ShaderType type) => type is ShaderType.Pointer p ? p.Base : type;
    private static bool BufferSpace(AddressSpace space) => space is AddressSpace.Storage or AddressSpace.Uniform or AddressSpace.Immediate;
    private ShaderType Buffer(ShaderType type)
    {
        if (physical.TryGetValue(type, out var known)) return known;
        ShaderType mapped = type switch {
            ShaderType.Array array => array with { Element = Buffer(array.Element),
                Stride = input.Buffers.TryGetValue(array, out var layout) ? layout.ArrayStride : array.Stride ?? TypeLayout.Of(array.Element).Stride },
            ShaderType.BindingArray array => array with { Element = Buffer(array.Element) },
            ShaderType.Structure structure => new ShaderType.Structure(Name("SpirvBuffer_" + structure.Name),
                structure.Members.Select(m => m with { Type = Buffer(m.Type) }).ToArray(), structure.BuiltinResult),
            _ => type
        };
        physical.Add(type, mapped);
        physical.TryAdd(mapped, mapped);
        if (input.Buffers.TryGetValue(type, out var decorations)) buffers.TryAdd(mapped, decorations);
        return mapped;
    }

    private ShaderType ValueType(ShaderType type)
    {
        if (values.TryGetValue(type, out var known)) return known;
        ShaderType mapped = type;
        switch (type) {
            case ShaderType.Pointer p:
                mapped = p with { Base = BufferSpace(p.Space) ? Buffer(p.Base) : p.Space == AddressSpace.Workgroup
                    ? ValueType(input.WorkgroupTypes.GetValueOrDefault(p.Base, p.Base)) : ValueType(p.Base) }; break;
            case ShaderType.Array a:
                mapped = a with { Element = ValueType(a.Element), Stride = null }; break;
            case ShaderType.BindingArray { Element: ShaderType.Structure } a: mapped = Buffer(a); break;
            case ShaderType.Structure s:
                var members = s.Members.Select(m => m with { Type = ValueType(m.Type) }).ToArray();
                if (!members.SequenceEqual(s.Members)) mapped = s with { Members = members };
                break;
        }
        values.Add(type, mapped); return mapped;
    }

    private ShaderFunction Conversion(ShaderType from, ShaderType to)
    {
        if (conversions.TryGetValue((from, to), out var known)) return known;
        var signature = new ShaderFunction(Name("sia_spv_value_convert_" + conversions.Count)) { ReturnType = to };
        signature.Arguments.Add(new("value", from));
        var graph = new ControlFlowFunction(signature); var block = graph.Block(); graph.Entry = block.Id;
        SsaValue Emit(ShaderType type, ValueOperation operation) { var value = graph.Value(type); block.Instructions.Add(new(value, operation)); return value; }
        SsaValue Convert(SsaValue value, ShaderType target) {
            if (value.Type == target) return value;
            if (value.Type is ShaderType.Array a && target is ShaderType.Array b && a.Length is uint length && b.Length == length)
                return Emit(target, new ValueOperation.Construct(Enumerable.Range(0, checked((int)length)).Select(i =>
                    Convert(Emit(a.Element, new ValueOperation.Access(value, Emit(ShaderType.U32, new ValueOperation.Literal((uint)i)))), b.Element)).ToArray()));
            if (value.Type is ShaderType.Structure s && target is ShaderType.Structure t && s.Members.Count == t.Members.Count)
                return Emit(target, new ValueOperation.Construct(s.Members.Select((m, i) =>
                    Convert(Emit(m.Type, new ValueOperation.Member(value, m.Name)), t.Members[i].Type)).ToArray()));
            throw new ShaderException(DiagnosticStage.SpirvWrite, "Incompatible aggregate value layout conversion.");
        }
        block.Terminator = new ControlFlowTerminator.Return(Convert(Emit(from, new ValueOperation.Symbol("value")), to));
        conversions.Add((from, to), signature); graphs.Add(signature.Name, graph); helpers.Add(signature);
        return signature;
    }

    private void Graph(ControlFlowFunction graph)
    {
        SsaValue Map(SsaValue value) => value with { Type = ValueType(value.Type) };
        foreach (var block in graph.Blocks) {
            for (int i = 0; i < block.Parameters.Count; i++) block.Parameters[i] = Map(block.Parameters[i]);
            block.Terminator = block.Terminator!.Map(Map);
            foreach (var edge in block.Terminator.Edges)
                for (int i = 0; i < edge.Arguments.Count; i++) edge.Arguments[i] = Map(edge.Arguments[i]);
            var instructions = new List<ControlFlowInstruction>();
            SsaValue Convert(SsaValue value, ShaderType type, ControlFlowInstruction origin, SsaValue? result = null) {
                if (value.Type == type) return value;
                var helper = Conversion(value.Type, type); var returned = result ?? graph.Value(type);
                instructions.Add(origin with { Result = returned, Operation = new ValueOperation.Call(helper.Name, [value], type) }); return returned;
            }
            foreach (var instruction in block.Instructions) {
                var operation = instruction.Operation.Map(Map);
                SsaValue? result = instruction.Result is { } original ? Map(original) : null;
                if (operation is ValueOperation.Load load && result is { } loaded && Data(load.Pointer.Type) != loaded.Type) {
                    var raw = graph.Value(Data(load.Pointer.Type));
                    instructions.Add(instruction with { Result = raw, Operation = load });
                    Convert(raw, loaded.Type, instruction, loaded); continue;
                }
                if (operation is ValueOperation.Store store)
                    operation = store with { Value = Convert(store.Value, Data(store.Pointer.Type), instruction) };
                if (operation is ValueOperation.Builtin { Function: "workgroupUniformLoad" } uniform && result is { } valueResult
                    && Data(uniform.Arguments.Single().Type) is not ShaderType.Atomic
                    && Data(uniform.Arguments.Single().Type) is { } physicalType && physicalType != valueResult.Type) {
                    var raw = graph.Value(physicalType);
                    instructions.Add(instruction with { Result = raw, Operation = uniform with { ReturnType = physicalType } });
                    Convert(raw, valueResult.Type, instruction, valueResult); continue;
                }
                if (operation is ValueOperation.Call call) operation = call with { ReturnType = ValueType(call.ReturnType) };
                if (operation is ValueOperation.Builtin builtin) operation = builtin with { ReturnType = ValueType(builtin.ReturnType) };
                instructions.Add(instruction with { Result = result, Operation = operation });
            }
            block.Instructions.Clear(); block.Instructions.AddRange(instructions);
        }
    }

    internal static SpirvPhysicalLayout Run(SpirvPhysicalLayout input, IDictionary<string, ControlFlowFunction> graphs,
        IReadOnlyList<ShaderType> workgroupOrder)
        => new SpirvValueLayoutLowering(input, graphs).Run(workgroupOrder);

    internal static void Run(ControlFlowFunction graph, SpirvPhysicalLayout layout)
    {
        var pass = new SpirvValueLayoutLowering(layout, new Dictionary<string, ControlFlowFunction>(StringComparer.Ordinal));
        pass.Graph(graph);
        if (pass.helpers.Count != 0) throw new ShaderException(DiagnosticStage.SpirvWrite, "Graph access requires prepared aggregate conversions.");
    }

    private SpirvPhysicalLayout Run(IReadOnlyList<ShaderType> workgroupOrder)
    {
        // Mesh publication can request a converted field after entry preparation.
        // Preserve frontend encounter order rather than frozen-map enumeration.
        foreach (var type in workgroupOrder.Where(CanonicalTypes.Data)) {
            var logical = ValueType(type); var shared = ValueType(input.WorkgroupTypes[type]);
            if (logical != shared) { _ = Conversion(logical, shared); _ = Conversion(shared, logical); }
        }
        var globals = new Dictionary<string, SpirvGlobalLayout>(StringComparer.Ordinal);
        foreach (var global in input.Module.Globals) {
            var layout = input.Globals[global.Name]; bool buffer = BufferSpace(global.Space);
            globals.Add(global.Name, layout with { PhysicalType = buffer ? Buffer(layout.PhysicalType) : ValueType(layout.PhysicalType),
                DeclarationType = buffer ? Buffer(layout.DeclarationType) : ValueType(layout.DeclarationType) });
            output.Globals.Add(global with { Type = buffer ? Buffer(global.Type) : global.Space == AddressSpace.Workgroup
                ? ValueType(layout.PhysicalType) : ValueType(global.Type),
                Initializer = global.Initializer is { } initial ? Value(initial) : null });
        }
        output.Structures.AddRange(input.Module.Structures.Select(s => (ShaderType.Structure)ValueType(s)));
        output.Constants.AddRange(input.Module.Constants.Select(c => c with { Type = ValueType(c.Type), Value = c.Value is { } v ? Value(v) : null }));
        output.Enables.UnionWith(input.Module.Enables); output.DiagnosticFilters.AddRange(input.Module.DiagnosticFilters);
        foreach (var f in input.Module.Functions) {
            Block body = f.Body;
            if (!graphs.ContainsKey(f.Name)) {
                scopes.Push(f.Arguments.Select(a => a.Name).ToHashSet(StringComparer.Ordinal)); body = Body(f.Body); scopes.Pop();
            }
            var args = f.Arguments.Select(a => a with { Type = ValueType(a.Type) }).ToArray(); var returns = ValueType(f.ReturnType);
            ShaderFunction signature = f;
            if (!ReferenceEquals(body, f.Body) || !args.SequenceEqual(f.Arguments) || returns != f.ReturnType) {
                signature = new(f.Name) { Body = body, ReturnType = returns, Stage = f.Stage, ReturnBinding = f.ReturnBinding,
                    WorkgroupSize = f.WorkgroupSize.ToArray(), TaskPayload = f.TaskPayload, MeshOutput = f.MeshOutput,
                    EarlyDepthTest = f.EarlyDepthTest, ConservativeDepth = f.ConservativeDepth };
                signature.Arguments.AddRange(args); signature.DiagnosticFilters.AddRange(f.DiagnosticFilters);
            }
            output.Functions.Add(signature);
            if (graphs.TryGetValue(f.Name, out var borrowed)) { var owned = borrowed.Copy(signature); Graph(owned); graphs[f.Name] = owned; }
        }
        output.Functions.AddRange(helpers);
        var uniformTypes = input.UniformTypes.ToFrozenDictionary(p => p.Key, p => Buffer(p.Value));
        return input with { Module = output, Globals = globals.ToFrozenDictionary(StringComparer.Ordinal), Buffers = buffers.ToFrozenDictionary(),
            BufferTypes = physical.ToFrozenDictionary(), ValueConversions = conversions.ToFrozenDictionary(), UniformTypes = uniformTypes,
            WorkgroupTypes = input.WorkgroupTypes.ToFrozenDictionary(p => p.Key, p => ValueType(p.Value)) };
    }
}
