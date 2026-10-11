using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.IR.ControlFlow;
using Sia.Spirv.Compiler.Translation.Proc;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Front;

public static partial class SpirvReader
{
    private sealed partial class Reader
    {
        private readonly Dictionary<string, (uint? BaseStride, uint? ResultStride)> nativeArithmetic = new(StringComparer.Ordinal);

        private Expression NativePointerArithmetic()
        {
            Count(4); var a = current.Operands;
            if (!variablePointers) throw Error("Pointer arithmetic requires a variable-pointer capability.");
            if (!valueTypeIds.TryGetValue(a[2], out uint baseType) || Type(baseType) is not ShaderType.Pointer pointer
                || Type(a[0]) is not ShaderType.Pointer result) throw Error("Pointer arithmetic requires pointer operands.");
            if (pointer.Space != result.Space) throw Error("Pointer arithmetic storage class mismatch.");
            if (pointer.Space is not (AddressSpace.Storage or AddressSpace.Workgroup) || pointer.Space == AddressSpace.Workgroup && !fullVariablePointers)
                throw Error("Pointer arithmetic requires storage memory or full VariablePointers Workgroup memory.");
            uint? stride = Decoration(baseType, 6);
            if (pointer.Space == AddressSpace.Storage && stride is not > 0)
                throw Error("Storage pointer arithmetic requires an ArrayStride on its base pointer type.");
            var offset = Value(a[3]);
            if (offset.Type is not ShaderType.Scalar { Kind: ScalarKind.Sint or ScalarKind.Uint })
                throw Error("Pointer provenance index must have an integer type.");
            var span = new SourceSpan(current.WordOffset * 4, current.WordCount * 4);
            Expression address = new Expression.Binary("+", Value(a[2]), offset, pointer) { Span = span };
            SpirvInstruction NativeType(uint id) => binary.Instructions.First(i => i.Operands.Length != 0 && i.Operands[0] == id
                && (Op)i.Opcode is Op.TypePointer or Op.TypeStruct or Op.TypeArray or Op.TypeRuntimeArray or Op.TypeVector or Op.TypeMatrix);
            uint pointee = NativeType(baseType).Operands[2];
            for (int i = 4; i < a.Length; i++) {
                address = Index(address, Value(a[i]));
                var type = NativeType(pointee);
                pointee = (Op)type.Opcode == Op.TypeStruct ? type.Operands[checked(1 + (int)ConstantUint(a[i]))] : type.Operands[1];
            }
            if (address.Type != result || pointee != NativeType(a[0]).Operands[2]) throw Error("Pointer access result type mismatch.");
            nativeArithmetic.Add(Name(a[1], "r"), (stride, Decoration(a[0], 6)));
            return address with { Span = span };
        }

        private void ValidateNativeArithmetic(ControlFlowFunction graph, Module input)
        {
            if (nativeArithmetic.Count == 0 || !PointerAliasAnalysis.HasPointerOperations(graph)) return;
            var definitions = graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.Result is not null).ToDictionary(i => i.Result!.Value.Id);
            var containers = PointerAliasAnalysis.Containers(input, graph);
            var roots = PointerAliasAnalysis.Origins(input, graph);
            foreach (var instruction in definitions.Values) {
                if (instruction.Operation is not ValueOperation.Let alias || !nativeArithmetic.TryGetValue(alias.Name, out var native)) continue;
                var address = alias.Value;
                while (definitions[address.Id].Operation is ValueOperation.Access or ValueOperation.Member)
                    address = definitions[address.Id].Operation.Operands.First();
                if (definitions[address.Id].Operation is not ValueOperation.Binary { Operator: "+" } arithmetic) continue;
                // Original parameter roots are checked after the helper binds
                // caller addresses. Source type decorations stay frontend-owned.
                if (roots[arithmetic.Left.Id].Any(r => r.Parameter >= 0)) continue;
                void Require(bool condition, string reason) {
                    if (!condition) throw new ShaderException(DiagnosticStage.SpirvParse, reason, instruction.Span);
                }
                var parents = containers[arithmetic.Left.Id];
                Require(parents.Count != 0, "Pointer arithmetic has no finite containing object.");
                foreach (var parent in parents) {
                    Require(parent is ShaderType.Array || PointerAliasAnalysis.IsZero(arithmetic.Right, definitions),
                        "Pointer arithmetic requires a finite innermost-array element provenance.");
                    if (arithmetic.Left.Type is ShaderType.Pointer { Space: AddressSpace.Storage } && parent is ShaderType.Array array)
                        Require(array.Stride == native.BaseStride, "Pointer arithmetic base stride does not match its array.");
                }
                if (native.ResultStride is { } stride)
                    foreach (var parent in containers[instruction.Result!.Value.Id].OfType<ShaderType.Array>())
                        Require(parent.Stride == stride, "Pointer arithmetic result stride does not match its array.");
            }
        }
    }
}
