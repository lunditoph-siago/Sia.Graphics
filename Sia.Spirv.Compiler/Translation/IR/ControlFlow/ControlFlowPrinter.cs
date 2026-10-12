using System.Globalization;
using System.Text;

namespace Sia.Spirv.Compiler.Translation.IR.ControlFlow;

internal static class ControlFlowPrinter
{
    public static string Write(ControlFlowFunction function)
    {
        var text = new StringBuilder();
        string V(SsaValue value) => "v" + value.Id.ToString(CultureInfo.InvariantCulture);
        string Edge(ControlFlowEdge edge) => "b" + edge.Target.ToString(CultureInfo.InvariantCulture) + "(" + string.Join(",", edge.Arguments.Select(V)) + ")";
        text.Append("function ").Append(function.Signature.Name).Append(" entry b").Append(function.Entry.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var block in function.Blocks.OrderBy(b => b.Id)) {
            text.Append('b').Append(block.Id.ToString(CultureInfo.InvariantCulture)).Append('(')
                .Append(string.Join(",", block.Parameters.Select(v => V(v) + ":" + Type(v.Type)))).Append("):");
            string Boundary(int? id) => id is int value ? "b" + value.ToString(CultureInfo.InvariantCulture) : "unreachable";
            if (function.SelectionMerges.TryGetValue(block.Id, out var merge)) text.Append(" selection-merge=").Append(Boundary(merge));
            if (function.Loops.TryGetValue(block.Id, out var loop)) text.Append(" loop-merge=").Append(Boundary(loop.Merge)).Append(" continuing=").Append(Boundary(loop.Continuing));
            text.Append('\n');
            foreach (var instruction in block.Instructions) {
                text.Append("  ");
                if (instruction.Result is { } value) text.Append(V(value)).Append(':').Append(Type(value.Type)).Append(" = ");
                text.Append(instruction.Operation switch {
                    ValueOperation.Literal l => "literal " + Convert.ToString(l.Value, CultureInfo.InvariantCulture),
                    ValueOperation.InterfaceLoad l => "interface-load " + l.Field.Name + " effects=" + instruction.Effects,
                    ValueOperation.InterfaceStore s => "interface-store " + s.Field.Name + " " + V(s.Value) + " effects=" + instruction.Effects,
                    ValueOperation.Symbol s => "symbol " + s.Name, ValueOperation.Local l => "local " + l.Name + (l.ZeroInitialize ? " zero" : " allocation-only"),
                    ValueOperation.Let l => "let " + l.Name + " " + V(l.Value),
                    ValueOperation.Unary u => u.Operator + " " + V(u.Operand), ValueOperation.Binary b => b.Operator + " " + V(b.Left) + "," + V(b.Right),
                    ValueOperation.Convert c => (c.Bitcast ? "bitcast " : "convert ") + V(c.Operand),
                    ValueOperation.Construct c => "construct " + string.Join(",", c.Components.Select(V)),
                    ValueOperation.Select s => "select " + V(s.Condition) + "," + V(s.Accept) + "," + V(s.Reject),
                    ValueOperation.Access a => "access " + V(a.Base) + "," + V(a.Index), ValueOperation.Member m => "member " + V(m.Base) + "." + m.Name,
                    ValueOperation.Swizzle s => "swizzle " + V(s.Vector) + "." + s.Components,
                    ValueOperation.Builtin b => b.Function + " " + string.Join(",", b.Arguments.Select(V)) + " atomic=" + b.AtomicMemory + " memory=" + b.MemoryAccess,
                    ValueOperation.Barrier b => (b.Control ? "control-barrier" : "memory-barrier") + " storage=" + b.Storage + " workgroup=" + b.Workgroup
                        + " texture=" + b.Texture + " subgroup=" + b.Subgroup + " native=" + b.NativeMemory,
                    ValueOperation.Call c => "call " + c.Function + " " + string.Join(",", c.Arguments.Select(V)) + " callee-effects=" + c.CalleeEffects,
                    ValueOperation.Load l => "load " + V(l.Pointer) + " " + l.MemoryAccess,
                    ValueOperation.Store s => "store " + V(s.Pointer) + "," + V(s.Value) + " " + s.MemoryAccess,
                    ValueOperation.MeshStore s => "mesh-store " + s.Field.Name + "[" + V(s.Index) + "]=" + V(s.Value),
                    ValueOperation.MeshSetOutputs s => "mesh-counts " + V(s.Vertices) + "," + V(s.Primitives),
                    ValueOperation.Demote => "helper-demote", ValueOperation.HelperInvocation => "helper-query", _ => "?"
                }).Append(" effects=").Append(instruction.Effects).Append(" span=")
                    .Append(instruction.Span.Start.ToString(CultureInfo.InvariantCulture)).Append(':')
                    .Append(instruction.Span.Length.ToString(CultureInfo.InvariantCulture));
                if (instruction.DiagnosticFilters.Count != 0) text.Append(" diagnostics=").Append(string.Join(",", instruction.DiagnosticFilters.Select(f =>
                    (f.Namespace is null ? "" : f.Namespace + ".") + f.Rule + ":" + f.Severity)));
                text.Append('\n');
            }
            text.Append("  ").Append(block.Terminator switch {
                ControlFlowTerminator.Branch b => "br " + Edge(b.Edge),
                ControlFlowTerminator.Conditional c => "if " + V(c.Condition) + " " + Edge(c.Accept) + " " + Edge(c.Reject),
                ControlFlowTerminator.Switch s => "switch " + V(s.Selector) + " " + string.Join(" ", s.Cases.Select(c =>
                    string.Join(",", c.Values.Select(v => Convert.ToString(v.Value, CultureInfo.InvariantCulture))) + ":" + Edge(c.Edge))) + " default:" + Edge(s.Default),
                ControlFlowTerminator.Return r => "return" + (r.Value is { } value ? " " + V(value) : ""),
                ControlFlowTerminator.TaskDispatch d => "task-dispatch " + V(d.Dimensions) + " payload=" + d.Payload + " effects=" + d.Effects,
                ControlFlowTerminator.Unreachable u => "unreachable span=" + u.Span.Start.ToString(CultureInfo.InvariantCulture)
                    + ":" + u.Span.Length.ToString(CultureInfo.InvariantCulture),
                ControlFlowTerminator.InvocationKill k => (k.ExplicitTermination ? "invocation-terminate span=" : "invocation-kill span=") + k.Span.Start.ToString(CultureInfo.InvariantCulture)
                    + ":" + k.Span.Length.ToString(CultureInfo.InvariantCulture) + " effects=" + k.Effects, _ => "missing-terminator"
            }).Append('\n');
        }
        return text.ToString();
    }
    private static string Type(ShaderType type) => type switch {
        ShaderType.Scalar s => s.Kind + s.Width.ToString(CultureInfo.InvariantCulture),
        ShaderType.Pointer p => "ptr<" + p.Space + "," + p.Access + "," + Type(p.Base) + ">",
        ShaderType.Structure s => s.Name, ShaderType.Vector v => "vec" + v.Size.ToString(CultureInfo.InvariantCulture) + "<" + Type(v.Component) + ">",
        ShaderType.Matrix m => "mat" + m.Columns.ToString(CultureInfo.InvariantCulture) + "x" + m.Rows.ToString(CultureInfo.InvariantCulture) + "<" + Type(m.Component) + ">",
        ShaderType.Array a => "array<" + Type(a.Element) + "," + (a.Length?.ToString(CultureInfo.InvariantCulture) ?? "runtime") + ">", _ => type.GetType().Name
    };
}
