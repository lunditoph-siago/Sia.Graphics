using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Legalization;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class SpirvWriter
{
    private sealed partial class Writer
    {
        private readonly Dictionary<(uint Function, EntryInterfaceField Field), uint> entryInterfaceVariables = [];
        private readonly HashSet<uint> workgroupBuiltins = [];
        private void InterfaceDecorations(uint id, SpirvInterfaceMetadata metadata)
        {
            capabilities.UnionWith(metadata.Capabilities); extensions.UnionWith(metadata.Extensions);
            foreach (var decoration in metadata.Decorations) Decorate(id, decoration.Decoration, decoration.Operands.ToArray());
        }
        private void EmitEntryPoint(SpirvEntryWrapper wrapper)
        {
            var metadata = EntryAbi.Entries[wrapper.Entry.Name];
            var signature = wrapper.Function.Graph.Signature;
            uint id = functions[signature.Name].Id;
            var interfaces = new List<uint>();
            foreach (var field in wrapper.Interfaces) {
                var prepared = metadata.Interfaces[field]; uint storage = prepared.Storage, variable = Id();
                declarations.Add(I(Op.Variable, Pointer(Type(field.Type), storage), variable, storage));
                entryInterfaceVariables.Add((id, field), variable);
                interfaces.Add(variable); Name(variable, field.Name); InterfaceDecorations(variable, prepared);
            }
            functionGlobalUses[id] = []; functionCalls[id] = [];
            new FunctionEmitter(this, id, signature).EmitCanonical(wrapper.Function);
            if (metadata.IncludeGlobalInterfaces) {
                var visited = new HashSet<uint>();
                void Collect(uint fn) { if (!visited.Add(fn)) return; interfaces.AddRange(functionGlobalUses[fn]); foreach (uint callee in functionCalls[fn]) Collect(callee); }
                Collect(id);
                if (metadata.TaskPayload is string payload) interfaces.Add(globals[payload].Id);
            }
            entryPoints.Add(I(Op.EntryPoint, new uint[] { metadata.Stage, id }.Concat(SpirvBinary.StringWords(metadata.Name)).Concat(interfaces.Distinct()).ToArray()));
            if (metadata.WorkgroupBuiltin is { } builtin) {
                uint value = EmitSpecialization(builtin);
                if (workgroupBuiltins.Add(value)) Decorate(value, 11, 25);
            }
            foreach (var mode in metadata.Modes) {
                uint[] operands = mode.Values is { } values ? values.Select(EmitSpecialization).ToArray() : mode.Literals.ToArray();
                executionModes.Add(I(mode.Values is null ? Op.ExecutionMode : Op.ExecutionModeId, new[] { id, mode.Mode }.Concat(operands).ToArray()));
            }
        }
    }
}
