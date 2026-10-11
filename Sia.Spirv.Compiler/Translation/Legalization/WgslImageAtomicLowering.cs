using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Legalization;

/// <summary>Legalize native image atomics without discarding observed results or ordered effects.</summary>
internal static class WgslImageAtomicLowering
{
    private static bool Native(ControlFlowInstruction instruction) => instruction.Operation is ValueOperation.Builtin { AtomicMemory: not null } call
        && call.Function.StartsWith("textureAtomic", StringComparison.Ordinal);
    private static bool NativeResult(ControlFlowInstruction instruction) => Native(instruction)
        && ((ValueOperation.Builtin)instruction.Operation).ReturnType is not ShaderType.Void;

    private static void ValidateTypes(Module module)
    {
        void Type(ShaderType type) {
            if (type is ShaderType.BindingArray array) Type(array.Element);
            if (type is ShaderType.Pointer pointer) Type(pointer.Base);
            if (type is ShaderType.Image { Multisampled: true } multisampled && (multisampled.StorageFormat is not null || multisampled.Arrayed))
                throw new ShaderException(DiagnosticStage.WgslWrite, "WGSL cannot preserve a multisampled storage or array image type.");
            if (type is ShaderType.Image { StorageFormat: { } format } native && StorageImageFormats.TryCode(format, out uint code)
                && native.Component != StorageImageFormats.Component(code))
                throw new ShaderException(DiagnosticStage.WgslWrite, "WGSL cannot preserve a native image component that differs from its storage format.");
            if (type is ShaderType.Image image && (image.Access & StorageAccess.Atomic) != 0
                && image.Access != (StorageAccess.ReadWrite | StorageAccess.Atomic))
                throw new ShaderException(DiagnosticStage.WgslWrite, "WGSL atomic texture access cannot preserve a read-only or write-only native image.");
        }
        foreach (var global in module.Globals) Type(global.Type);
        foreach (var function in module.Functions) {
            Type(function.ReturnType); foreach (var argument in function.Arguments) Type(argument.Type);
        }
    }

    internal static CanonicalModule Run(CanonicalModule input)
    {
        // Inspect operations first so diagnostics identify the original access,
        // before the declaration-level guard handles unused resource types.
        foreach (var instruction in input.Functions.Values.SelectMany(g => g.Blocks).SelectMany(b => b.Instructions)) {
            if (instruction.Operation is not ValueOperation.Builtin { ImageAccess: { } access } call) continue;
            var image = (ShaderType.Image)call.Arguments[0].Type;
            if (image.Multisampled && (image.StorageFormat is not null || image.Arrayed))
                throw new ShaderException(DiagnosticStage.WgslWrite, "WGSL cannot preserve a native multisampled image load/store.", instruction.Span);
            if ((access.Flags & 3840) != 0)
                throw new ShaderException(DiagnosticStage.WgslWrite, "WGSL cannot preserve native image volatility or inter-thread memory ordering.", instruction.Span);
            var value = call.Function == "textureStore" ? call.Arguments[^1].Type : call.ReturnType;
            if (!image.Depth && value != new ShaderType.Vector(4, image.Component)
                || image.StorageFormat is string format && StorageImageFormats.TryCode(format, out uint code) && image.Component != StorageImageFormats.Component(code))
                throw new ShaderException(DiagnosticStage.WgslWrite, "WGSL cannot preserve native image texel signedness through this texture builtin.", instruction.Span);
        }
        if (!input.Functions.Values.SelectMany(g => g.Blocks).SelectMany(b => b.Instructions).Any(Native)) {
            ValidateTypes(input.Declarations); return input;
        }
        var graphs = new Dictionary<string, ControlFlowFunction>(StringComparer.Ordinal);
        foreach (var (name, original) in input.Functions) {
            if (!original.Blocks.SelectMany(b => b.Instructions).Any(Native)) { graphs.Add(name, original); continue; }
            foreach (var instruction in original.Blocks.SelectMany(b => b.Instructions).Where(Native)) {
                var call = (ValueOperation.Builtin)instruction.Operation; var image = (ShaderType.Image)call.Arguments[0].Type;
                if (image.Multisampled) throw new ShaderException(DiagnosticStage.WgslWrite,
                    "WGSL cannot preserve a native multisampled image atomic operation.", instruction.Span);
                bool operation = call.Function is "textureAtomicAdd" or "textureAtomicMin" or "textureAtomicMax" or "textureAtomicAnd"
                    or "textureAtomicOr" or "textureAtomicXor" or "textureAtomicSub" or "textureAtomicIncrement" or "textureAtomicDecrement";
                bool format = image.StorageFormat is "r32uint" or "r32sint"
                    || image.StorageFormat == "r64uint" && call.Function is "textureAtomicMin" or "textureAtomicMax";
                if (!operation || !format) throw new ShaderException(DiagnosticStage.WgslWrite,
                    "Native image atomic operation has no equivalent WGSL texture atomic builtin; preserve SPIR-V output.", instruction.Span);
            }
            var definitions = original.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null)
                .ToDictionary(i => i.Result!.Value.Id);
            var blocks = original.Blocks.ToDictionary(b => b.Id);
            var incoming = original.Blocks.SelectMany(b => b.Parameters).ToDictionary(p => p.Id, _ => new List<SsaValue>());
            foreach (var edge in original.Blocks.SelectMany(b => b.Terminator!.Edges))
                for (int i = 0; i < edge.Arguments.Count; i++) incoming[blocks[edge.Target].Parameters[i].Id].Add(edge.Arguments[i]);
            var live = new HashSet<int>(); var pending = new Queue<SsaValue>();
            void Need(SsaValue value) { if (live.Add(value.Id)) pending.Enqueue(value); }
            foreach (var block in original.Blocks) {
                foreach (var instruction in block.Instructions.Where(i => i.Effects != ShaderEffects.None))
                    foreach (var operand in instruction.Operation.Operands) Need(operand);
                foreach (var operand in block.Terminator!.Operands) Need(operand);
            }
            // Edge arguments matter only if their destination parameter is used.
            // A cycle of dead phi/alias values therefore does not consume a result.
            while (pending.TryDequeue(out var value)) {
                if (definitions.TryGetValue(value.Id, out var instruction))
                    foreach (var operand in instruction.Operation.Operands) Need(operand);
                if (incoming.TryGetValue(value.Id, out var arguments)) foreach (var argument in arguments) Need(argument);
            }
            foreach (var instruction in original.Blocks.SelectMany(b => b.Instructions).Where(NativeResult))
                if (instruction.Result is { } result && live.Contains(result.Id))
                    throw new ShaderException(DiagnosticStage.WgslWrite,
                        "WGSL texture atomics cannot return an old value; this SPIR-V result is used.", instruction.Span);

            var graph = original.Copy();
            foreach (var block in graph.Blocks) {
                block.Parameters.RemoveAll(p => !live.Contains(p.Id));
                foreach (var edge in block.Terminator!.Edges)
                    for (int i = edge.Arguments.Count - 1; i >= 0; i--)
                        if (!live.Contains(blocks[edge.Target].Parameters[i].Id)) edge.Arguments.RemoveAt(i);
                var instructions = block.Instructions.ToArray(); block.Instructions.Clear();
                foreach (var instruction in instructions) {
                    if (Native(instruction)) {
                        var call = (ValueOperation.Builtin)instruction.Operation;
                        if (call.Function is "textureAtomicSub" or "textureAtomicIncrement" or "textureAtomicDecrement") {
                            var component = ((ShaderType.Image)call.Arguments[0].Type).Component;
                            SsaValue Emit(ValueOperation operation) {
                                var value = graph.Value(component);
                                block.Instructions.Add(new(value, operation, instruction.Span) { DiagnosticFilters = instruction.DiagnosticFilters }); return value;
                            }
                            bool sub = call.Function == "textureAtomicSub", increment = call.Function == "textureAtomicIncrement";
                            object literal = component.Kind == ScalarKind.Uint ? (object)(sub ? 0u : increment ? 1u : uint.MaxValue) : sub ? 0 : increment ? 1 : -1;
                            var delta = Emit(new ValueOperation.Literal(literal));
                            if (sub) delta = Emit(new ValueOperation.Binary("-", delta, call.Arguments[^1]));
                            call = call with { Function = "textureAtomicAdd", Arguments = (sub ? call.Arguments.SkipLast(1) : call.Arguments).Append(delta).ToArray() };
                        }
                        block.Instructions.Add(instruction with { Result = null, Operation = call with { ReturnType = new ShaderType.Void() } });
                    }
                    else if (instruction.Effects != ShaderEffects.None || instruction.Result is { } result && live.Contains(result.Id))
                        block.Instructions.Add(instruction);
                }
            }
            graphs.Add(name, graph);
        }
        var output = input with { Functions = graphs };
        ValidateTypes(output.Declarations); ModuleValidator.Validate(output); return output;
    }
}
