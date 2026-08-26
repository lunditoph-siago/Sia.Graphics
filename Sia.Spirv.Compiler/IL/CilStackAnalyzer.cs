using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Sia.Spirv.Compiler.Metadata;

namespace Sia.Spirv.Compiler.IL;

internal sealed class CilStackAnalyzer(
    MetadataReader reader,
    MethodDefinitionHandle methodHandle)
{
    public void Validate(ShaderCilView view)
    {
        var graph = view.Graph;
        if (graph.Blocks.Count == 0) {
            return;
        }

        var entryDepths = new int?[graph.Blocks.Count];
        entryDepths[0] = 0;
        var pending = new Queue<int>();
        pending.Enqueue(0);

        while (pending.TryDequeue(out var blockId)) {
            var block = graph.Blocks[blockId];
            var depth = entryDepths[blockId]!.Value;
            for (var index = 0; index < block.Instructions.Count; index++) {
                var instruction = block.Instructions[index];
                var (pop, push) = GetStackChange(view, block, index, instruction);
                if (depth < pop) {
                    throw new InvalidDataException(
                        $"Evaluation stack underflow at IL_{instruction.Offset:x4}.");
                }
                depth = depth - pop + push;
            }

            foreach (var successor in block.Successors) {
                if (entryDepths[successor] is int existingDepth) {
                    if (existingDepth != depth) {
                        throw new InvalidDataException(
                            $"Evaluation stack depth mismatch at IL_{graph.Blocks[successor].StartOffset:x4}: " +
                            $"expected {existingDepth}, received {depth}.");
                    }
                    continue;
                }

                entryDepths[successor] = depth;
                pending.Enqueue(successor);
            }
        }
    }

    private (int Pop, int Push) GetStackChange(
        ShaderCilView view,
        CilBasicBlock block,
        int instructionIndex,
        CilInstruction instruction)
    {
        var pop = instruction.OpCode.StackBehaviourPop == StackBehaviour.Varpop
            ? GetVariablePopCount(view, block, instructionIndex, instruction)
            : GetFixedPopCount(instruction.OpCode.StackBehaviourPop);
        var push = instruction.OpCode.StackBehaviourPush == StackBehaviour.Varpush
            ? GetVariablePushCount(view, block, instructionIndex, instruction)
            : GetFixedPushCount(instruction.OpCode.StackBehaviourPush);
        return (pop, push);
    }

    private int GetVariablePopCount(
        ShaderCilView view,
        CilBasicBlock block,
        int instructionIndex,
        CilInstruction instruction)
    {
        if (instruction.OpCode == OpCodes.Ret) {
            return GetCurrentMethodSignature().ReturnType.IsVoid ? 0 : 1;
        }
        if (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) {
            var call = view.ResolveCall(block, instructionIndex);
            return call.ParameterCount + (call.IsInstance ? 1 : 0);
        }
        if (instruction.OpCode == OpCodes.Calli) {
            var signature = GetStandaloneMethodSignature((int)instruction.Operand!);
            return signature.ParameterTypes.Length + (signature.Header.IsInstance ? 1 : 0) + 1;
        }
        if (instruction.OpCode == OpCodes.Newobj) {
            return GetMethodSignature((int)instruction.Operand!).ParameterTypes.Length;
        }

        throw new InvalidDataException(
            $"Unsupported variable-pop opcode {instruction.OpCode.Name} at IL_{instruction.Offset:x4}.");
    }

    private int GetVariablePushCount(
        ShaderCilView view,
        CilBasicBlock block,
        int instructionIndex,
        CilInstruction instruction)
    {
        if (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) {
            return view.ResolveCall(block, instructionIndex).ReturnsVoid ? 0 : 1;
        }
        if (instruction.OpCode == OpCodes.Calli) {
            return GetStandaloneMethodSignature((int)instruction.Operand!).ReturnType.IsVoid ? 0 : 1;
        }
        if (instruction.OpCode == OpCodes.Newobj) {
            return 1;
        }

        throw new InvalidDataException(
            $"Unsupported variable-push opcode {instruction.OpCode.Name} at IL_{instruction.Offset:x4}.");
    }

    private MethodSignature<SignatureType> GetCurrentMethodSignature() =>
        reader.GetMethodDefinition(methodHandle)
            .DecodeSignature(SignatureTypeProvider.Instance, genericContext: null);

    // Newobj/Calli skip CilCallResolver: they never target an intrinsic
    // (already rejected elsewhere) — only their raw stack shape matters.
    private MethodSignature<SignatureType> GetMethodSignature(int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        return handle.Kind switch {
            HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)handle)
                .DecodeSignature(SignatureTypeProvider.Instance, genericContext: null),
            HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)handle)
                .DecodeMethodSignature(SignatureTypeProvider.Instance, genericContext: null),
            HandleKind.MethodSpecification => GetMethodSpecificationSignature(
                reader.GetMethodSpecification((MethodSpecificationHandle)handle)),
            _ => throw new InvalidDataException($"Token 0x{token:x8} is not a method.")
        };
    }

    private MethodSignature<SignatureType> GetMethodSpecificationSignature(
        MethodSpecification specification)
    {
        var token = MetadataTokens.GetToken(specification.Method);
        return GetMethodSignature(token);
    }

    private MethodSignature<SignatureType> GetStandaloneMethodSignature(int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind != HandleKind.StandaloneSignature) {
            throw new InvalidDataException($"Token 0x{token:x8} is not a standalone signature.");
        }
        return reader.GetStandaloneSignature((StandaloneSignatureHandle)handle)
            .DecodeMethodSignature(SignatureTypeProvider.Instance, genericContext: null);
    }

    private static int GetFixedPopCount(StackBehaviour behavior) => behavior switch {
        StackBehaviour.Pop0 => 0,
        StackBehaviour.Pop1 or
        StackBehaviour.Popi or
        StackBehaviour.Popref => 1,
        StackBehaviour.Pop1_pop1 or
        StackBehaviour.Popi_pop1 or
        StackBehaviour.Popi_popi or
        StackBehaviour.Popi_popi8 or
        StackBehaviour.Popi_popr4 or
        StackBehaviour.Popi_popr8 or
        StackBehaviour.Popref_pop1 or
        StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popi_popi_popi or
        StackBehaviour.Popref_popi_pop1 or
        StackBehaviour.Popref_popi_popi or
        StackBehaviour.Popref_popi_popi8 or
        StackBehaviour.Popref_popi_popr4 or
        StackBehaviour.Popref_popi_popr8 or
        StackBehaviour.Popref_popi_popref => 3,
        _ => throw new InvalidDataException($"Unsupported stack pop behavior {behavior}.")
    };

    private static int GetFixedPushCount(StackBehaviour behavior) => behavior switch {
        StackBehaviour.Push0 => 0,
        StackBehaviour.Push1 or
        StackBehaviour.Pushi or
        StackBehaviour.Pushi8 or
        StackBehaviour.Pushr4 or
        StackBehaviour.Pushr8 or
        StackBehaviour.Pushref => 1,
        StackBehaviour.Push1_push1 => 2,
        _ => throw new InvalidDataException($"Unsupported stack push behavior {behavior}.")
    };
}
