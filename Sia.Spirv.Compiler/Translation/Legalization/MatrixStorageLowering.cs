using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

internal sealed partial class MatrixStorageLowering
{
    private sealed record MatrixColumn(SsaValue Rows, ShaderType.Matrix Matrix, SsaValue Column);
    private sealed record Address(SsaValue Pointer, Node Node, MatrixColumn? Column = null);

    internal static CanonicalModule Run(CanonicalModule input)
    {
        if (!HasLayout(input.Declarations)) return input;
        ModuleValidator.Validate(input, native: true);
        input = CanonicalReferenceLowering.Run(CanonicalHelperInliner.RunTargetHelpers(input));
        return new MatrixStorageLowering(input.Declarations).Graphs(input);
    }

    private CanonicalModule Graphs(CanonicalModule canonical)
    {
        foreach (var s in input.Structures) names.Add(s.Name);
        foreach (var g in input.Globals) names.Add(g.Name);
        foreach (var c in input.Constants) names.Add(c.Name);
        foreach (var f in input.Functions) names.Add(f.Name);
        foreach (var member in input.Structures.SelectMany(s => s.Members).Where(m => m.MatrixLayout is not null))
            for (ShaderType type = member.Type; type is ShaderType.Array array; type = array.Element) layoutArrays.Add(array);
        foreach (var s in input.Structures) Logical(s);
        output.Enables.UnionWith(input.Enables); output.DiagnosticFilters.AddRange(input.DiagnosticFilters);
        foreach (var g in input.Globals) {
            var node = Memory(g.Type);
            output.Globals.Add(g with { Type = node.Physical, Initializer = g.Initializer is null ? null : PhysicalValue(node, Expr(g.Initializer)) });
            locations.Add(g.Name, new(new Expression.Reference(g.Name, new ShaderType.Pointer(node.Physical, g.Space, g.Access)), node));
        }
        foreach (var c in input.Constants) output.Constants.Add(c with { Type = Logical(c.Type), Value = c.Value is null ? null : Expr(c.Value) });
        var graphs = new Dictionary<string, ControlFlowFunction>(StringComparer.Ordinal);
        foreach (var f in input.Functions) {
            var signature = new ShaderFunction(f.Name) { ReturnType = Logical(f.ReturnType), ReturnBinding = f.ReturnBinding, Stage = f.Stage,
                WorkgroupSize = f.WorkgroupSize.Select(Expr).ToArray(), EarlyDepthTest = f.EarlyDepthTest,
                ConservativeDepth = f.ConservativeDepth, TaskPayload = f.TaskPayload, MeshOutput = f.MeshOutput };
            signature.Arguments.AddRange(f.Arguments.Select(a => a with { Type = Logical(a.Type) }));
            signature.DiagnosticFilters.AddRange(f.DiagnosticFilters);
            output.Functions.Add(signature);
            if (canonical.Functions.TryGetValue(f.Name, out var original)) {
                var graph = original.Copy(signature); Graph(graph); graphs.Add(f.Name, graph);
            }
            else {
                // Only explicitly deferred bodies use the structured adapter.
                var saved = locations; locations = new(saved, StringComparer.Ordinal);
                foreach (var a in f.Arguments.Where(a => a.Type is ShaderType.Pointer)) {
                    var pointer = (ShaderType.Pointer)a.Type; var node = Memory(Logical(pointer.Base));
                    locations[a.Name] = new(new Expression.Reference(a.Name, pointer with { Base = node.Physical }), node);
                }
                Reserve(f.Body); signature.Body = Body(f.Body); locations = saved;
            }
        }
        var result = canonical with { Declarations = output, Functions = graphs };
        ShaderEffectAnalysis.RefreshCalls(result); ModuleValidator.Validate(result, native: true); return result;
    }

    private void Graph(ControlFlowFunction graph)
    {
        var definitions = graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null).ToDictionary(i => i.Result!.Value.Id);
        var globals = input.Globals.ToDictionary(g => g.Name, StringComparer.Ordinal);
        ControlFlowBlock current = graph.Blocks[0]; ControlFlowInstruction origin = new(null, new ValueOperation.Construct([]));
        SsaValue Data(SsaValue value) => value with { Type = Logical(value.Type) };
        SsaValue Emit(ShaderType type, ValueOperation operation) {
            var value = graph.Value(type); current.Instructions.Add(new(value, operation, origin.Span) { DiagnosticFilters = origin.DiagnosticFilters }); return value;
        }
        void Effect(ValueOperation operation) => current.Instructions.Add(new(null, operation, origin.Span) { DiagnosticFilters = origin.DiagnosticFilters });
        SsaValue Literal(uint value) => Emit(ShaderType.U32, new ValueOperation.Literal(value));
        SsaValue Child(SsaValue pointer, ShaderType type, SsaValue index) => Emit(((ShaderType.Pointer)pointer.Type) with { Base = type }, new ValueOperation.Access(pointer, index));
        Address Field(Address parent, string name) {
            if (parent.Node.Original is not ShaderType.Structure s || parent.Node.Members is null) throw Error("Invalid matrix member address.");
            int index = s.Members.ToList().FindIndex(m => m.Name == name);
            if (index < 0) throw Error("Unknown matrix member.");
            var node = parent.Node.Members[index];
            return new(Emit(((ShaderType.Pointer)parent.Pointer.Type) with { Base = node.Physical }, new ValueOperation.Member(parent.Pointer, name)), node);
        }
        Address Index(Address parent, SsaValue index) {
            if (parent.Column is { } projection) {
                var row = new ShaderType.Vector(projection.Matrix.Columns, projection.Matrix.Component);
                return new(Child(Child(projection.Rows, row, index), row.Component, projection.Column), Memory(row.Component));
            }
            if (parent.Node.Matrix is { } layout && parent.Node.Original is ShaderType.Matrix matrix) {
                var column = new ShaderType.Vector(matrix.Rows, matrix.Component);
                return layout.RowMajor ? new(parent.Pointer, Memory(column), new(parent.Pointer, matrix, index))
                    : new(Child(parent.Pointer, column, index), Memory(column));
            }
            if (parent.Node.Element is { } element) return new(Child(parent.Pointer, element.Physical, index), element);
            ShaderType child = parent.Node.Original switch { ShaderType.Matrix m => new ShaderType.Vector(m.Rows, m.Component),
                ShaderType.Vector v => v.Component, _ => throw Error("Invalid matrix-layout index.") };
            return new(Child(parent.Pointer, child, index), Memory(child));
        }
        Address Place(SsaValue value) {
            if (!definitions.TryGetValue(value.Id, out var definition)) throw Error("Unresolved matrix address parameter.");
            var pointer = (ShaderType.Pointer)value.Type;
            switch (definition.Operation) {
                case ValueOperation.Symbol s:
                    var node = Memory(globals.TryGetValue(s.Name, out var global) ? global.Type : Logical(pointer.Base));
                    return new(Emit(pointer with { Base = node.Physical }, s), node);
                case ValueOperation.Local: return new(Data(value), Memory(Logical(pointer.Base)));
                case ValueOperation.Let l: return Place(l.Value);
                case ValueOperation.Unary { Operator: "&" or "*" } u: return Place(u.Operand);
                case ValueOperation.Member m: return Field(Place(m.Base), m.Name);
                case ValueOperation.Access a: return Index(Place(a.Base), Data(a.Index));
                default: throw Error("Matrix storage requires resolved address provenance.");
            }
        }
        SsaValue Value(SsaValue value) {
            if (value.Type is not ShaderType.Pointer) return Data(value);
            var place = Place(value);
            if (place.Column is not null) throw Error("Strided matrix column escapes require helper expansion.");
            return place.Pointer;
        }
        void Loop(uint length, Action<SsaValue> body) {
            var zero = Literal(0); var count = Literal(length);
            var header = graph.Block(); var iteration = graph.Block(); var continuing = graph.Block(); var end = graph.Block();
            var index = graph.Value(ShaderType.U32); header.Parameters.Add(index);
            current.Terminator = new ControlFlowTerminator.Branch(new(header.Id, [zero])); current = header;
            var condition = Emit(ShaderType.Bool, new ValueOperation.Binary("<", index, count));
            header.Terminator = new ControlFlowTerminator.Conditional(condition, new(iteration.Id), new(end.Id));
            graph.Loops.Add(header.Id, new(continuing.Id, end.Id)); current = iteration; body(index);
            current.Terminator = new ControlFlowTerminator.Branch(new(continuing.Id)); current = continuing;
            var next = Emit(ShaderType.U32, new ValueOperation.Binary("+", index, Literal(1)));
            continuing.Terminator = new ControlFlowTerminator.Branch(new(header.Id, [next])); current = end;
        }
        SsaValue Read(Address place, SpirvMemoryAccess? memory) {
            if (place.Column is { } projection)
                return Emit(place.Node.Logical, new ValueOperation.Construct(Enumerable.Range(0, projection.Matrix.Rows)
                    .Select(r => Read(Index(place, Literal((uint)r)), memory?.Leaf())).ToArray()));
            if (place.Node.Matrix is not null && place.Node.Original is ShaderType.Matrix matrix)
                return Emit(matrix, new ValueOperation.Construct(Enumerable.Range(0, matrix.Columns)
                    .Select(c => Read(Index(place, Literal((uint)c)), memory?.Leaf())).ToArray()));
            if (place.Node.Physical == place.Node.Logical) return Emit(place.Node.Logical, new ValueOperation.Load(place.Pointer, memory));
            if (place.Node.Original is ShaderType.Structure structure)
                return Emit(place.Node.Logical, new ValueOperation.Construct(structure.Members.Select(m => Read(Field(place, m.Name), memory?.Leaf())).ToArray()));
            if (place.Node.Original is ShaderType.Array { Length: uint length } && place.Node.Element is { } element) {
                var temporary = Emit(new ShaderType.Pointer(place.Node.Logical, AddressSpace.Function), new ValueOperation.Local(Name("sia_matrix_snapshot"), false));
                Loop(length, index => Effect(new ValueOperation.Store(Child(temporary, element.Logical, index), Read(Index(place, index), memory?.Leaf()))));
                return Emit(place.Node.Logical, new ValueOperation.Load(temporary));
            }
            throw Error("Whole matrix-layout array access requires a resolved finite length.");
        }
        void Write(Address place, SsaValue value, SpirvMemoryAccess? memory) {
            if (place.Column is { } projection) {
                for (uint r = 0; r < projection.Matrix.Rows; r++) Write(Index(place, Literal(r)),
                    Emit(projection.Matrix.Component, new ValueOperation.Access(value, Literal(r))), memory?.Leaf());
                return;
            }
            if (place.Node.Matrix is not null && place.Node.Original is ShaderType.Matrix matrix) {
                for (uint c = 0; c < matrix.Columns; c++) Write(Index(place, Literal(c)),
                    Emit(new ShaderType.Vector(matrix.Rows, matrix.Component), new ValueOperation.Access(value, Literal(c))), memory?.Leaf());
                return;
            }
            if (place.Node.Physical == place.Node.Logical) { Effect(new ValueOperation.Store(place.Pointer, value, memory)); return; }
            if (place.Node.Original is ShaderType.Structure structure) {
                foreach (var member in structure.Members) Write(Field(place, member.Name),
                    Emit(Logical(member.Type), new ValueOperation.Member(value, member.Name)), memory?.Leaf());
                return;
            }
            if (place.Node.Original is ShaderType.Array { Length: uint length } && place.Node.Element is { } element) {
                Loop(length, index => Write(Index(place, index), Emit(element.Logical, new ValueOperation.Access(value, index)), memory?.Leaf())); return;
            }
            throw Error("Whole matrix-layout array access requires a resolved finite length.");
        }
        foreach (var block in graph.Blocks.ToArray()) {
            var instructions = block.Instructions.ToArray(); var terminator = block.Terminator!;
            block.Instructions.Clear(); current = block;
            for (int i = 0; i < block.Parameters.Count; i++) block.Parameters[i] = Data(block.Parameters[i]);
            foreach (var instruction in instructions) {
                origin = instruction;
                if (instruction.Result?.Type is ShaderType.Pointer && instruction.Operation is not ValueOperation.Local) continue;
                if (instruction.Operation is ValueOperation.Load load && instruction.Result is { } result) {
                    var value = Read(Place(load.Pointer), load.MemoryAccess);
                    current.Instructions.Add(instruction with { Result = Data(result), Operation = new ValueOperation.Let("sia_matrix_value_" + result.Id, value) });
                }
                else if (instruction.Operation is ValueOperation.Store store) Write(Place(store.Pointer), Data(store.Value), store.MemoryAccess);
                else {
                    var operation = instruction.Operation.Map(Value);
                    if (operation is ValueOperation.Call call) operation = call with { ReturnType = Logical(call.ReturnType) };
                    if (operation is ValueOperation.Builtin builtin) operation = builtin with { ReturnType = Logical(builtin.ReturnType) };
                    current.Instructions.Add(instruction with { Result = instruction.Result is { } original ? Data(original) : null, Operation = operation });
                }
            }
            current.Terminator = terminator.Map(Value);
            foreach (var edge in current.Terminator.Edges)
                for (int i = 0; i < edge.Arguments.Count; i++) edge.Arguments[i] = Value(edge.Arguments[i]);
            if (current != block && graph.SelectionMerges.Remove(block.Id, out var merge)) graph.SelectionMerges.Add(current.Id, merge);
        }
        LocalValuePromotion.Run(graph);
    }
}
