using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class SpirvWriter
{
    private sealed partial class Writer
    {
        private readonly Dictionary<MeshOutputField, uint> meshOutputs = [];
        private uint MeshOutput(MeshOutputField field)
        {
            if (meshOutputs.TryGetValue(field, out uint known)) return known;
            // This outer interface type is independent of buffer array identities.
            uint array = Id(); declarations.Add(I(Op.TypeArray, array, Type(field.Type), Constant(Expression.U32(field.Capacity))));
            uint variable = Id(); declarations.Add(I(Op.Variable, Pointer(array, 3), variable, 3));
            Name(variable, field.Name); InterfaceDecorations(variable, EntryAbi.MeshInterfaces[field]);
            meshOutputs.Add(field, variable); return variable;
        }

        private sealed partial class FunctionEmitter
        {
            private void TaskDispatch(Statement.TaskDispatch dispatch)
            {
                uint dimensions = Value(dispatch.Dimensions);
                uint[] counts = Enumerable.Range(0, 3).Select(i => Result(Op.CompositeExtract, ShaderType.U32, dimensions, (uint)i)).ToArray();
                uint payload = owner.globals[dispatch.Payload].Id;
                owner.functionGlobalUses[functionId].Add(payload);
                Add(Op.EmitMeshTasksEXT, [.. counts, payload]); terminated = true;
            }
        }
    }
}
