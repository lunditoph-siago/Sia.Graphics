using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;

namespace Sia.Spirv.Compiler.Translation.Valid;

/// <summary>Native variable-pointer invariants over verified expanded addresses.</summary>
internal static class NativePointerValidator
{
    public static void Validate(ControlFlowFunction graph, Module module, bool fullVariablePointers)
        => Check(graph, module, fullVariablePointers, checkOrigins: true);

    // Calls have not bound their returned addresses yet. Only shape/capability
    // restrictions can be proved here; the complete check runs after expansion.
    public static void ValidateBeforeExpansion(ControlFlowFunction graph, Module module, bool fullVariablePointers)
        => Check(graph, module, fullVariablePointers, checkOrigins: false);

    private static void Check(ControlFlowFunction graph, Module module, bool fullVariablePointers, bool checkOrigins)
    {
        var origins = PointerAliasAnalysis.Origins(module, graph);
        bool Matrix(ShaderType type) => type switch {
            ShaderType.Matrix => true, ShaderType.Array array => Matrix(array.Element),
            ShaderType.Structure structure => structure.Members.Any(m => Matrix(m.Type)), _ => false
        };
        void Check(SsaValue value) {
            if (value.Type is not ShaderType.Pointer pointer) return;
            void Require(bool valid, string reason) {
                if (!valid) throw new ShaderException(DiagnosticStage.SpirvParse, "Native " + graph.Signature.Name + ": " + reason);
            }
            Require(pointer.Space is AddressSpace.Storage or AddressSpace.Workgroup, "Variable pointers require storage or workgroup memory.");
            Require(pointer.Space != AddressSpace.Workgroup || fullVariablePointers, "Workgroup pointer return requires the full VariablePointers capability.");
            Require(!Matrix(pointer.Base), "Variable pointers cannot point to objects containing matrices.");
            if (fullVariablePointers || !checkOrigins) return;
            var roots = origins[value.Id];
            // Parameter roots are checked again after the helper binds caller SSA
            // addresses. Descriptor arrays use the explicitly deferred native route.
            if (roots.Any(r => r.Parameter >= 0)) return;
            Require(roots.Count == 1 && roots.All(r => r.Global is { } name && module.Globals.Any(g => g.Name == name && g.Type is ShaderType.Structure)),
                "VariablePointersStorageBuffer selections must remain within one storage buffer structure.");
        }
        foreach (var parameter in graph.Blocks.SelectMany(b => b.Parameters)) Check(parameter);
        foreach (var instruction in graph.Blocks.SelectMany(b => b.Instructions))
            if (instruction.Operation is ValueOperation.Load && instruction.Result is { Type: ShaderType.Pointer } address) Check(address);
        foreach (var returned in graph.Blocks.Select(b => b.Terminator).OfType<ControlFlowTerminator.Return>())
            if (returned.Value is { Type: ShaderType.Pointer } address) Check(address);
    }
}
