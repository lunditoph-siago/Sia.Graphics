using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;

namespace Sia.Spirv.Compiler.Translation.Legalization;

internal static partial class CanonicalReferenceLowering
{
    private static void RewriteComparisons(ControlFlowFunction graph, Dictionary<int, ControlFlowInstruction> definitions,
        Func<SsaValue, ControlFlowBlock, Choice[]> choices)
    {
        SsaValue Source(SsaValue value) {
            while (definitions.GetValueOrDefault(value.Id)?.Operation is ValueOperation.Let alias) value = alias.Value;
            return value;
        }
        foreach (var block in graph.Blocks) {
            var original = block.Instructions.ToArray(); block.Instructions.Clear();
            foreach (var instruction in original) {
                if (instruction is not { Result: { } result, Operation: ValueOperation.Binary { Left.Type: ShaderType.Pointer } binary }) {
                    block.Instructions.Add(instruction); continue;
                }
                int first = block.Instructions.Count;
                SsaValue Emit(ShaderType type, ValueOperation operation) {
                    var value = graph.Value(type); block.Instructions.Add(instruction with { Result = value, Operation = operation }); return value;
                }
                SsaValue Bool(bool value) => Emit(ShaderType.Bool, new ValueOperation.Literal(value));
                SsaValue Binary(string op, SsaValue a, SsaValue b) => Emit(op == "-" ? a.Type : ShaderType.Bool, new ValueOperation.Binary(op, a, b));
                SsaValue IndexAs(SsaValue value, ShaderType.Scalar type) {
                    if (value.Type == type) return value;
                    if (type.Kind == ScalarKind.Sint)
                        return Emit(type, new ValueOperation.Convert(IndexAs(value, type with { Kind = ScalarKind.Uint }), true));
                    var source = (ShaderType.Scalar)value.Type;
                    if (source.Kind == ScalarKind.Sint) value = Emit(source with { Kind = ScalarKind.Uint }, new ValueOperation.Convert(value, true));
                    return value.Type == type ? value : Emit(type, new ValueOperation.Convert(value, false));
                }
                SsaValue EqualIndex(SsaValue a, SsaValue b) {
                    var type = new ShaderType.Scalar(ScalarKind.Uint, Math.Max(4, Math.Max(((ShaderType.Scalar)a.Type).Width, ((ShaderType.Scalar)b.Type).Width)));
                    return Binary("==", IndexAs(a, type), IndexAs(b, type));
                }
                bool difference = binary.Operator == "-";
                var answer = difference ? Emit(result.Type, new ValueOperation.Construct([])) : Bool(false);
                if (Source(binary.Left) == Source(binary.Right)) {
                    if (!difference) answer = Bool(true);
                }
                else {
                    var left = choices(binary.Left, block); var right = choices(binary.Right, block);
                    foreach (var a in left) foreach (var b in right) {
                        if (a.Shape.Root != b.Shape.Root) {
                            if (binary.Left.Type is ShaderType.Pointer { Space: AddressSpace.Storage })
                                throw new ShaderException(DiagnosticStage.Validation,
                                    "Cross-buffer pointer comparison requires address equivalence for potentially aliased bindings.", instruction.Span);
                            continue;
                        }
                        int count = a.Shape.Parts.Length;
                        if (count != b.Shape.Parts.Length || difference && count == 0) continue;
                        var condition = Binary("&&", a.Condition, b.Condition);
                        ShaderType parent = ((ShaderType.Pointer)a.Shape.Root.Type).Base;
                        bool same = true;
                        for (int i = 0; i < count - (difference ? 1 : 0); i++) {
                            var x = a.Shape.Parts[i]; var y = b.Shape.Parts[i];
                            if (x.Member != y.Member) { same = false; break; }
                            if (x.Member is { } member) parent = ((ShaderType.Structure)parent).Members.Single(m => m.Name == member).Type;
                            else {
                                condition = Binary("&&", condition, EqualIndex(a.Indices[i]!.Value, b.Indices[i]!.Value));
                                parent = parent switch {
                                    ShaderType.Array array => array.Element, ShaderType.Vector vector => vector.Component,
                                    ShaderType.Matrix matrix => new ShaderType.Vector(matrix.Rows, matrix.Component),
                                    _ => throw new ShaderException(DiagnosticStage.Validation, "Invalid pointer comparison projection.", instruction.Span)
                                };
                            }
                        }
                        if (!same) continue;
                        if (difference) {
                            // The source operation defines subtraction only within
                            // one array object. Other pairs retain the zero policy.
                            if (parent is not ShaderType.Array || a.Indices[^1] is not { } x || b.Indices[^1] is not { } y) continue;
                            var value = Binary("-", IndexAs(x, (ShaderType.Scalar)result.Type), IndexAs(y, (ShaderType.Scalar)result.Type));
                            answer = Emit(result.Type, new ValueOperation.Select(condition, value, answer));
                        }
                        else answer = Binary("||", answer, condition);
                    }
                }
                if (binary.Operator == "!=") answer = Emit(ShaderType.Bool, new ValueOperation.Unary("!", answer));
                // Choice tag predicates are emitted by the shared provenance pass.
                // They belong to this operation's diagnostic location as well.
                for (int i = first; i < block.Instructions.Count; i++)
                    block.Instructions[i] = block.Instructions[i] with { Span = instruction.Span, DiagnosticFilters = instruction.DiagnosticFilters };
                block.Instructions.Add(instruction with { Operation = new ValueOperation.Let("pointer_comparison", answer) });
            }
        }
    }
}
