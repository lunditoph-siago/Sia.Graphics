using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class SpirvWriter
{
    private sealed partial class Writer
    {
        private readonly Dictionary<ShaderType, ShaderType> uniformTypes = [];
        private sealed record UniformStep(string? Member = null, uint Index = 0, uint? Constant = null);
        private sealed record UniformLocation(uint Root, ShaderType Logical, ShaderType Physical, MemoryDecorations Memory, IReadOnlyList<UniformStep> Steps);
        private void MarkBufferTypeIfNeeded(ShaderType type, bool buffer) { if (buffer) MarkBufferType(type); }

        // WGSL's matrix columns can be closer than Vulkan std140 permits.
        // Flatten columns into the enclosing structure, preserving every byte offset.
        private static bool FlattenUniformMatrix(ShaderType.Matrix matrix) => TypeLayout.Of(new ShaderType.Vector(matrix.Rows, matrix.Component)).Stride < 16;
        private ShaderType UniformType(ShaderType logical)
        {
            if (uniformTypes.TryGetValue(logical, out var cached)) return cached;
            ShaderType physical = logical;
            if (logical is ShaderType.Matrix matrix && FlattenUniformMatrix(matrix))
            {
                var column = new ShaderType.Vector(matrix.Rows, matrix.Component);
                uint stride = TypeLayout.Of(column).Stride;
                physical = new ShaderType.Structure("SpirvUniformMatrix_" + uniformTypes.Count,
                    Enumerable.Range(0, matrix.Columns).Select(i => new StructMember("column" + i, column, Offset: (uint)i * stride)).ToArray());
            }
            else if (logical is ShaderType.Array array)
            {
                ShaderType element = UniformType(array.Element);
                if (element != array.Element) physical = array with { Element = element, Stride = array.Stride ?? TypeLayout.Of(array.Element).Stride };
            }
            else if (logical is ShaderType.BindingArray bindings)
                physical = bindings with { Element = UniformType(bindings.Element) };
            else if (logical is ShaderType.Structure structure)
            {
                var fields = new List<StructMember>(); uint offset = 0; bool changed = false;
                foreach (var member in structure.Members)
                {
                    var layout = TypeLayout.Of(member.Type);
                    offset = member.Offset ?? TypeLayout.RoundUp(member.Alignment ?? layout.Alignment, offset);
                    if (member.Type is ShaderType.Matrix m && FlattenUniformMatrix(m))
                    {
                        var column = new ShaderType.Vector(m.Rows, m.Component); uint stride = TypeLayout.Of(column).Stride;
                        for (int c = 0; c < m.Columns; c++) fields.Add(new(member.Name + "_column" + c, column, Offset: offset + (uint)c * stride)
                        { MemoryDecorations = member.MemoryDecorations });
                        changed = true;
                    }
                    else
                    {
                        ShaderType mapped = UniformType(member.Type); changed |= mapped != member.Type;
                        fields.Add(member with { Type = mapped, Offset = offset });
                    }
                    offset = checked(offset + (member.Size ?? layout.Size));
                }
                if (changed) physical = new ShaderType.Structure("SpirvUniform_" + structure.Name, fields);
            }
            uniformTypes.Add(logical, physical); return physical;
        }

        private sealed partial class FunctionEmitter
        {
            private bool NeedsUniformConversion(Expression place)
            {
                Expression root = place;
                while (root is Expression.Access or Expression.Member or Expression.Unary { Operator: "&" or "*" })
                    root = root switch { Expression.Access a => a.Base, Expression.Member m => m.Base, Expression.Unary u => u.Operand, _ => root };
                return root is Expression.Reference r && (Lookup(r.Name).UniformLocation is not null
                    || Lookup(r.Name) is { Storage: 2, PhysicalType: { } physical } symbol && physical != symbol.Type);
            }
            private UniformLocation UniformPlace(Expression place)
            {
                switch (place)
                {
                    case Expression.Reference reference:
                        var symbol = Lookup(reference.Name);
                        if (symbol.UniformLocation is { } alias) return alias;
                        ShaderType physical = symbol.PhysicalType ?? symbol.Type;
                        uint pointer = symbol.Id;
                        if (symbol.BufferWrapper) pointer = Result(Op.AccessChain, new ShaderType.Pointer(physical, AddressSpace.Uniform, StorageAccess.Read), pointer, owner.Constant(Expression.U32(0)));
                        return new(pointer, symbol.Type, physical, owner.globalMemory.GetValueOrDefault(reference.Name), []);
                    case Expression.Unary { Operator: "&" or "*" } unary: return UniformPlace(unary.Operand);
                    case Expression.Member member:
                        var container = UniformPlace(member.Base);
                        return container with { Steps = container.Steps.Append(new UniformStep(Member: member.Name)).ToArray() };
                    case Expression.Access access:
                        var parent = UniformPlace(access.Base);
                        uint? constant = ConstantEvaluator.TryEvaluateRuntime(access.Index, out var evaluated) && evaluated is Expression.Literal literal
                            && literal.Value is uint or int && System.Convert.ToInt64(literal.Value) >= 0 ? System.Convert.ToUInt32(literal.Value) : null;
                        uint index = Value(access.Index);
                        return parent with { Steps = parent.Steps.Append(new UniformStep(Index: index, Constant: constant)).ToArray() };
                    default: throw owner.Error("Unsupported uniform reference path.", place.Span);
                }
            }
            private uint UniformChild(uint pointer, ShaderType child, uint index, bool descriptor = false, bool dynamic = false)
            {
                var type = new ShaderType.Pointer(child, AddressSpace.Uniform, StorageAccess.Read);
                uint result = Result(Op.AccessChain, type, pointer, index);
                if (descriptor && dynamic) { owner.NonUniform(index, type); owner.NonUniform(result, type); }
                else if (owner.nonUniformResources.TryGetValue(pointer, out var resource)) owner.NonUniform(result, resource);
                return result;
            }
            private uint UniformChild(uint pointer, ShaderType child, int index) => UniformChild(pointer, child, owner.Constant(Expression.U32(checked((uint)index))));
            private static int UniformField(ShaderType.Structure structure, int index) => structure.Members.Take(index)
                .Sum(m => m.Type is ShaderType.Matrix matrix && FlattenUniformMatrix(matrix) ? matrix.Columns : 1);
            private uint UniformRead(Expression place, SpirvMemoryAccess? memory = null)
            {
                var location = UniformPlace(place);
                ShaderType resultType = DataType(place.Type);
                uint SelectColumn(UniformStep step, int count, Func<int, uint> read)
                {
                    if (step.Constant is uint fixedIndex && fixedIndex < count) return read((int)fixedIndex);
                    // Struct members require constant access-chain indices. Only
                    // the selected case performs a memory operation; indexes
                    // were evaluated once when the location was captured.
                    uint temporary = Variable(resultType), merge = owner.Id(), fallback = owner.Id();
                    uint[] labels = Enumerable.Range(0, count).Select(_ => owner.Id()).ToArray();
                    Add(Op.SelectionMerge, merge, 0);
                    Add(Op.Switch, new[] { step.Index, fallback }.Concat(labels.SelectMany((label, i) => new[] { (uint)i, label })).ToArray());
                    for (int i = 0; i < count; i++) { Label(labels[i]); Add(Op.Store, temporary, read(i)); Branch(merge); }
                    Label(fallback); Add(Op.Store, temporary, owner.Null(resultType)); Branch(merge); Label(merge);
                    return Result(Op.Load, resultType, temporary);
                }
                uint Path(uint pointer, ShaderType logical, ShaderType physical, int depth, MemoryDecorations inherited)
                {
                    if (depth == location.Steps.Count) return LoadUniform(pointer, logical, physical, memory, inherited);
                    var step = location.Steps[depth];
                    if (step.Member is string name && logical is ShaderType.Structure structure && physical is ShaderType.Structure mapped)
                    {
                        int field = (int)MemberIndex(structure, name), physicalField = UniformField(structure, field);
                        var member = structure.Members[field]; inherited |= member.MemoryDecorations;
                        if (member.Type is ShaderType.Matrix matrix && FlattenUniformMatrix(matrix))
                        {
                            var column = new ShaderType.Vector(matrix.Rows, matrix.Component);
                            if (depth + 1 == location.Steps.Count)
                                return Result(Op.CompositeConstruct, matrix, Enumerable.Range(0, matrix.Columns)
                                    .Select(c => LoadUniform(UniformChild(pointer, column, physicalField + c), column, column, memory?.Leaf(), inherited)).ToArray());
                            return SelectColumn(location.Steps[depth + 1], matrix.Columns,
                                c => Path(UniformChild(pointer, column, physicalField + c), column, column, depth + 2, inherited));
                        }
                        return Path(UniformChild(pointer, mapped.Members[physicalField].Type, physicalField), member.Type, mapped.Members[physicalField].Type, depth + 1, inherited);
                    }
                    if (logical is ShaderType.Matrix matrixType && physical is ShaderType.Structure columns)
                    {
                        var column = new ShaderType.Vector(matrixType.Rows, matrixType.Component);
                        return SelectColumn(step, matrixType.Columns, c => Path(UniformChild(pointer, column, c), column, column, depth + 1, inherited));
                    }
                    (ShaderType element, ShaderType mappedElement) = (logical, physical) switch
                    {
                        (ShaderType.Array a, ShaderType.Array b) => (a.Element, b.Element),
                        (ShaderType.BindingArray a, ShaderType.BindingArray b) => (a.Element, b.Element),
                        (ShaderType.Matrix a, ShaderType.Matrix b) => (new ShaderType.Vector(a.Rows, a.Component), new ShaderType.Vector(b.Rows, b.Component)),
                        (ShaderType.Vector a, ShaderType.Vector b) => (a.Component, b.Component),
                        _ => throw owner.Error("Uniform access path has incompatible types.", place.Span)
                    };
                    return Path(UniformChild(pointer, mappedElement, step.Index, logical is ShaderType.BindingArray, step.Constant is null), element, mappedElement, depth + 1, inherited);
                }
                return Path(location.Root, location.Logical, location.Physical, 0, location.Memory);
            }
            private uint LoadUniform(uint pointer, ShaderType logical, ShaderType physical, SpirvMemoryAccess? memory, MemoryDecorations inherited)
            {
                if (logical == physical) return MemoryLoad(pointer, logical, DecoratedMemory(memory, inherited | TypeMemory(logical), AddressSpace.Uniform));
                uint Child(int index, ShaderType child) => UniformChild(pointer, child, index);
                memory = memory?.Leaf();
                if (logical is ShaderType.Matrix matrix && physical is ShaderType.Structure columns)
                {
                    uint[] values = columns.Members.Select((m, i) => LoadUniform(Child(i, m.Type), m.Type, m.Type, memory, inherited)).ToArray();
                    return Result(Op.CompositeConstruct, matrix, values);
                }
                if (logical is ShaderType.Array array && physical is ShaderType.Array mapped)
                {
                    if (array.Length is not uint length) throw owner.Error("Uniform arrays must be statically sized.");
                    uint[] values = Enumerable.Range(0, checked((int)length)).Select(i => LoadUniform(Child(i, mapped.Element), array.Element, mapped.Element, memory, inherited)).ToArray();
                    return Result(Op.CompositeConstruct, logical, values);
                }
                if (logical is ShaderType.Structure structure && physical is ShaderType.Structure mappedStructure)
                {
                    var values = new List<uint>(); int field = 0;
                    foreach (var member in structure.Members)
                    {
                        if (member.Type is ShaderType.Matrix m && FlattenUniformMatrix(m))
                        {
                            var columnValues = new List<uint>();
                            for (int c = 0; c < m.Columns; c++)
                            {
                                ShaderType columnType = mappedStructure.Members[field].Type;
                                columnValues.Add(LoadUniform(Child(field++, columnType), columnType, columnType, memory, inherited | member.MemoryDecorations));
                            }
                            values.Add(Result(Op.CompositeConstruct, m, columnValues.ToArray()));
                        }
                        else
                        {
                            ShaderType fieldType = mappedStructure.Members[field].Type;
                            values.Add(LoadUniform(Child(field++, fieldType), member.Type, fieldType, memory, inherited | member.MemoryDecorations));
                        }
                    }
                    return Result(Op.CompositeConstruct, logical, values.ToArray());
                }
                throw owner.Error("Uniform layout conversion has incompatible types.");
            }
        }
    }
}
