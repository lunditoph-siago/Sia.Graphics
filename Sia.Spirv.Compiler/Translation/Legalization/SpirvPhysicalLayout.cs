using System.Collections.Frozen;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Physical type identities and byte layout chosen before serialization.</summary>
internal sealed record SpirvPhysicalLayout(Module Module,
    IReadOnlyDictionary<string, SpirvGlobalLayout> Globals,
    IReadOnlyDictionary<ShaderType, ShaderType> UniformTypes,
    IReadOnlyDictionary<ShaderType, ShaderType> WorkgroupTypes,
    IReadOnlyDictionary<ShaderType, SpirvBufferLayout> Buffers,
    IReadOnlyDictionary<ShaderType.Structure, IReadOnlyList<int>> UniformFields,
    IReadOnlySet<ShaderType.Matrix> FlattenedUniformMatrices)
{
    internal SpirvEntryAbi? EntryAbi { get; init; }
    internal IReadOnlyDictionary<string, SpirvFunctionControlFlow> ControlFlow { get; init; }
        = FrozenDictionary<string, SpirvFunctionControlFlow>.Empty;
    internal IReadOnlyDictionary<string, string> DeferredControlFlow { get; init; }
        = FrozenDictionary<string, string>.Empty;
    internal IReadOnlyDictionary<string, SpirvEntryWrapper> EntryWrappers { get; init; }
        = FrozenDictionary<string, SpirvEntryWrapper>.Empty;
    internal IReadOnlyDictionary<(ShaderType Logical, bool ToPhysical), ShaderFunction> WorkgroupConversions { get; init; }
        = FrozenDictionary<(ShaderType, bool), ShaderFunction>.Empty;
    // A validation view over the existing graph owner, not another graph snapshot.
    internal CanonicalModule Canonical => new(Module, ControlFlow.ToDictionary(p => p.Key, p => p.Value.Graph, StringComparer.Ordinal),
        DeferredControlFlow, Module.Functions.Where(f => f.Stage is not null).Select(f => f.Name).ToHashSet(StringComparer.Ordinal));
}

internal sealed record SpirvGlobalLayout(ShaderType PhysicalType, ShaderType DeclarationType, bool BufferWrapper);
internal sealed record SpirvBufferLayout(uint? ArrayStride, IReadOnlyList<SpirvBufferMemberLayout> Members);
internal sealed record SpirvBufferMemberLayout(uint Offset, uint? MatrixStride);

internal static class SpirvPhysicalLayoutLowering
{
    internal static SpirvPhysicalLayout Prepare(Module module, bool useLocalSizeId = false, uint? version = null)
        => Prepare(SpirvControlFlowLowering.Capture(module), useLocalSizeId, version);

    internal static SpirvPhysicalLayout Prepare(CanonicalModule canonical, bool useLocalSizeId = false, uint? version = null)
    {
        ControlFlowVerifier.Validate(canonical);
        var module = canonical.Declarations;
        var graphs = new Dictionary<string, ControlFlowFunction>(StringComparer.Ordinal);
        var deferred = canonical.DeferredFunctions.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var adapters = new Dictionary<string, ShaderFunction>(StringComparer.Ordinal);
        foreach (var pair in canonical.Functions) {
            var graph = pair.Value.Copy();
            if (SpirvControlFlowLowering.TryPrepare(graph, module, out var reason)) graphs.Add(pair.Key, graph);
            else {
                // Only a target-deferred executable graph crosses this adapter.
                // The borrowed declaration Body may be empty or obsolete.
                adapters.Add(pair.Key, StructuredControlFlowLowering.Run(graph, module)); deferred.Add(pair.Key, reason!);
            }
        }
        if (adapters.Count != 0) {
            var adapted = new Module { VulkanMemoryModel = module.VulkanMemoryModel, WorkgroupInitializationRequired = module.WorkgroupInitializationRequired };
            adapted.Structures.AddRange(module.Structures); adapted.Globals.AddRange(module.Globals); adapted.Constants.AddRange(module.Constants);
            adapted.Enables.UnionWith(module.Enables); adapted.DiagnosticFilters.AddRange(module.DiagnosticFilters);
            adapted.Functions.AddRange(module.Functions.Select(f => adapters.GetValueOrDefault(f.Name, f))); module = adapted;
        }
        var uniform = new Dictionary<ShaderType, ShaderType>();
        var workgroup = new Dictionary<ShaderType, ShaderType>();
        var bufferTypes = new HashSet<ShaderType>();
        var fields = new Dictionary<ShaderType.Structure, IReadOnlyList<int>>();
        var flattened = new HashSet<ShaderType.Matrix>();
        var globals = new Dictionary<string, SpirvGlobalLayout>(StringComparer.Ordinal);
        void MarkBuffer(ShaderType type) {
            if (!bufferTypes.Add(type)) return;
            switch (type) {
                case ShaderType.Array a: MarkBuffer(a.Element); break;
                case ShaderType.BindingArray a: MarkBuffer(a.Element); break;
                case ShaderType.Structure s: foreach (var member in s.Members) MarkBuffer(member.Type); break;
            }
        }
        bool Flatten(ShaderType.Matrix matrix) {
            bool result = TypeLayout.Of(new ShaderType.Vector(matrix.Rows, matrix.Component)).Stride < 16;
            if (result) flattened.Add(matrix);
            return result;
        }
        ShaderType Uniform(ShaderType logical) {
            if (uniform.TryGetValue(logical, out var known)) return known;
            ShaderType physical = logical;
            if (logical is ShaderType.Matrix matrix && Flatten(matrix)) {
                var column = new ShaderType.Vector(matrix.Rows, matrix.Component);
                uint stride = TypeLayout.Of(column).Stride;
                physical = new ShaderType.Structure("SpirvUniformMatrix_" + uniform.Count,
                    Enumerable.Range(0, matrix.Columns).Select(i => new StructMember("column" + i, column, Offset: (uint)i * stride)).ToArray());
            }
            else if (logical is ShaderType.Array array) {
                ShaderType element = Uniform(array.Element);
                if (element != array.Element) physical = array with { Element = element, Stride = array.Stride ?? TypeLayout.Of(array.Element).Stride };
            }
            else if (logical is ShaderType.BindingArray bindings) physical = bindings with { Element = Uniform(bindings.Element) };
            else if (logical is ShaderType.Structure structure) {
                var members = new List<StructMember>(); var positions = new List<int>(); uint offset = 0; bool changed = false;
                foreach (var member in structure.Members) {
                    var layout = TypeLayout.Of(member.Type);
                    offset = member.Offset ?? TypeLayout.RoundUp(member.Alignment ?? layout.Alignment, offset);
                    positions.Add(members.Count);
                    if (member.Type is ShaderType.Matrix m && Flatten(m)) {
                        var column = new ShaderType.Vector(m.Rows, m.Component); uint stride = TypeLayout.Of(column).Stride;
                        for (int c = 0; c < m.Columns; c++) members.Add(new(member.Name + "_column" + c, column, Offset: offset + (uint)c * stride) {
                            MemoryDecorations = member.MemoryDecorations
                        });
                        changed = true;
                    }
                    else {
                        ShaderType mapped = Uniform(member.Type); changed |= mapped != member.Type;
                        members.Add(member with { Type = mapped, Offset = offset });
                    }
                    offset = checked(offset + (member.Size ?? layout.Size));
                }
                fields.Add(structure, positions.AsReadOnly());
                if (changed) physical = new ShaderType.Structure("SpirvUniform_" + structure.Name, members.AsReadOnly());
            }
            uniform.Add(logical, physical); return physical;
        }
        ShaderType Workgroup(ShaderType logical) {
            if (workgroup.TryGetValue(logical, out var known)) return known;
            ShaderType physical = logical;
            if (logical is ShaderType.Array array) {
                ShaderType element = Workgroup(array.Element);
                if (element != array.Element || bufferTypes.Contains(array)) {
                    uint? identityStride = array.Stride is null ? TypeLayout.Of(array.Element).Stride : null;
                    physical = array with { Element = element, Stride = identityStride };
                }
            }
            else if (logical is ShaderType.Structure structure) {
                var members = structure.Members.Select(m => m with { Type = Workgroup(m.Type) }).ToArray();
                if (bufferTypes.Contains(structure) || members.Where((m, i) => m.Type != structure.Members[i].Type).Any())
                    physical = new ShaderType.Structure("SpirvWorkgroup_" + structure.Name, members);
            }
            else if (logical is ShaderType.Matrix matrix) {
                _ = Workgroup(new ShaderType.Vector(matrix.Rows, matrix.Component)); _ = Workgroup(matrix.Component);
            }
            else if (logical is ShaderType.Vector vector) _ = Workgroup(vector.Component);
            else if (logical is ShaderType.Atomic atomic) _ = Workgroup(atomic.Component);
            workgroup.Add(logical, physical); return physical;
        }
        foreach (var global in module.Globals.Where(g => g.Space is AddressSpace.Uniform or AddressSpace.Storage or AddressSpace.Immediate)) MarkBuffer(global.Type);
        foreach (var global in module.Globals) {
            bool buffer = global.Space is AddressSpace.Uniform or AddressSpace.Storage or AddressSpace.Immediate;
            bool wrap = buffer && global.Type is not ShaderType.BindingArray && !(global.Type is ShaderType.Structure structure && TypeLayout.Of(structure).IsRuntimeSized);
            ShaderType physical = global.Space == AddressSpace.Uniform ? Uniform(global.Type) : global.Space == AddressSpace.Workgroup ? Workgroup(global.Type) : global.Type;
            if (buffer) MarkBuffer(physical);
            ShaderType declaration = physical;
            if (wrap) {
                declaration = new ShaderType.Structure("SpirvBlock_" + global.Name, [new StructMember("value", physical, Offset: 0)]);
                MarkBuffer(declaration);
            }
            globals.Add(global.Name, new(physical, declaration, wrap));
        }
        // Function parameters and native address values may expose workgroup
        // subtypes even when the function is outside the entry call graph.
        var visited = new HashSet<ShaderType>();
        void Type(ShaderType type) {
            if (!visited.Add(type)) return;
            switch (type) {
                case ShaderType.Pointer p:
                    if (p.Space == AddressSpace.Workgroup) _ = Workgroup(p.Base);
                    Type(p.Base); break;
                case ShaderType.Array a: Type(a.Element); break;
                case ShaderType.BindingArray a: Type(a.Element); break;
                case ShaderType.Structure s: foreach (var m in s.Members) Type(m.Type); break;
            }
        }
        void Expr(Expression e) {
            Type(e.Type);
            IEnumerable<Expression> children = e switch {
                Expression.Load l => [l.Pointer], Expression.Unary u => [u.Operand], Expression.Binary b => [b.Left, b.Right],
                Expression.Call c => c.Arguments, Expression.Construct c => c.Components, Expression.Convert c => [c.Operand],
                Expression.Access a => [a.Base, a.Index], Expression.Member m => [m.Base], Expression.Swizzle s => [s.Vector],
                Expression.Select s => [s.Condition, s.Accept, s.Reject], _ => []
            };
            foreach (var child in children) Expr(child);
        }
        void Body(Block body) {
            foreach (var statement in body.Statements) switch (statement) {
                case Statement.Declare d: Type(d.Type); if (d.Initializer is { } value) Expr(value); break;
                case Statement.Store s: Expr(s.Target); Expr(s.Value); break;
                case Statement.Evaluate e: Expr(e.Value); break;
                case Statement.Return { Value: { } returned }: Expr(returned); break;
                case Statement.Nested n: Body(n.Body); break;
                case Statement.If i: Expr(i.Condition); Body(i.Accept); Body(i.Reject); break;
                case Statement.Loop l: Body(l.Body); Body(l.Continuing); if (l.BreakIf is { } condition) Expr(condition); break;
                case Statement.Switch s: Expr(s.Selector); foreach (var c in s.Cases) Body(c.Body); break;
            }
        }
        foreach (var global in module.Globals) { Type(global.Type); if (global.Initializer is { } initializer) Expr(initializer); }
        foreach (var constant in module.Constants) { Type(constant.Type); if (constant.Value is { } value) Expr(value); }
        foreach (var function in module.Functions) {
            Type(function.ReturnType); foreach (var argument in function.Arguments) Type(argument.Type);
            if (graphs.TryGetValue(function.Name, out var graph))
                foreach (var block in graph.Blocks) {
                    foreach (var parameter in block.Parameters) Type(parameter.Type);
                    foreach (var instruction in block.Instructions) {
                        if (instruction.Result is { } result) Type(result.Type);
                        foreach (var operand in instruction.Operation.Operands) Type(operand.Type);
                    }
                    foreach (var operand in block.Terminator!.Operands.Concat(block.Terminator.Edges.SelectMany(e => e.Arguments))) Type(operand.Type);
                }
            else Body(function.Body);
        }
        foreach (var physical in workgroup.Values.ToArray()) workgroup.TryAdd(physical, physical);
        var buffers = new Dictionary<ShaderType, SpirvBufferLayout>();
        foreach (var type in bufferTypes) {
            uint? stride = type is ShaderType.Array array ? array.Stride ?? TypeLayout.Of(array.Element).Stride : null;
            var members = new List<SpirvBufferMemberLayout>(); uint offset = 0;
            if (type is ShaderType.Structure structure) foreach (var member in structure.Members) {
                var layout = TypeLayout.Of(member.Type);
                offset = member.Offset ?? TypeLayout.RoundUp(member.Alignment ?? layout.Alignment, offset);
                ShaderType element = member.Type; while (element is ShaderType.Array a) element = a.Element;
                uint? matrixStride = element is ShaderType.Matrix matrix ? TypeLayout.Of(new ShaderType.Vector(matrix.Rows, matrix.Component)).Stride : null;
                members.Add(new(offset, matrixStride));
                offset = checked(offset + (member.Size ?? layout.Size));
            }
            buffers.Add(type, new(stride, members.AsReadOnly()));
        }
        var prepared = new SpirvPhysicalLayout(module, globals.ToFrozenDictionary(StringComparer.Ordinal), uniform.ToFrozenDictionary(), workgroup.ToFrozenDictionary(),
            buffers.ToFrozenDictionary(), fields.ToFrozenDictionary(), flattened.ToFrozenSet());
        prepared = SpirvWorkgroupValueLowering.Run(prepared, workgroup.Keys.ToArray(), graphs);
        prepared = SpirvUniformAccessLowering.Run(prepared, graphs, deferred);
        prepared = SpirvWorkgroupAccessLowering.Run(prepared, graphs);
        return SpirvEntryMetadataLowering.Prepare(SpirvEntryWrapperLowering.Prepare(SpirvControlFlowLowering.Prepare(prepared, graphs)), useLocalSizeId, version);
    }
}
