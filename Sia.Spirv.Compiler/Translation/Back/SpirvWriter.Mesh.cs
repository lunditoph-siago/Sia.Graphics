using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Translation.Back;

public static partial class SpirvWriter
{
    private sealed partial class Writer
    {
        private void EmitMeshFinish(ShaderFunction function, uint entry, uint result, uint localIndex,
            List<SpirvInstruction> code, List<SpirvInstruction> variables, List<uint> interfaces)
        {
            uint R(Op op, ShaderType type, params uint[] operands)
            { uint id = Id(); code.Add(I(op, new uint[] { Type(type), id }.Concat(operands).ToArray())); return id; }
            if (function.Stage == ShaderStage.Task)
            {
                uint[] dimensions = Enumerable.Range(0, 3).Select(i => R(Op.CompositeExtract, ShaderType.U32, result, (uint)i)).ToArray();
                uint payload = globals[function.TaskPayload!].Id;
                interfaces.Add(payload);
                code.Add(I(Op.EmitMeshTasksEXT, [.. dimensions, payload]));
                return;
            }
            var info = MeshShaderInfo.Inspect(module, function);
            uint aggregate = globals[info.Variable.Name].Id; interfaces.Add(aggregate);
            uint Read(uint pointer, ShaderType type, MemoryDecorations memory) => R(Op.Load, type,
                new[] { pointer }.Concat(MemoryOperands(DecoratedMemory(null, memory | TypeMemory(type), AddressSpace.Workgroup))).ToArray());
            executionModes.Add(I(Op.ExecutionMode, entry, info.Topology));
            executionModes.Add(I(Op.ExecutionMode, entry, 26, info.Vertices.Length!.Value));
            executionModes.Add(I(Op.ExecutionMode, entry, 5270, info.Primitives.Length!.Value));
            // All invocations finish the original body before publishing shared output.
            code.Add(I(Op.ControlBarrier, Constant(Expression.U32(2)), Constant(Expression.U32(2)), Constant(Expression.U32(0x108))));
            uint Count(int member, uint maximum)
            {
                uint pointer = R(Op.AccessChain, new ShaderType.Pointer(ShaderType.U32, AddressSpace.Workgroup), aggregate, Constant(Expression.U32((uint)member)));
                uint value = Read(pointer, ShaderType.U32, info.Variable.MemoryDecorations | info.Structure.Members[member].MemoryDecorations);
                return R(Op.ExtInst, ShaderType.U32, GlslImport(), 38, value, Constant(Expression.U32(maximum)));
            }
            uint vertices = Count(info.VertexCountMember, info.Vertices.Length.Value);
            uint primitives = Count(info.PrimitiveCountMember, info.Primitives.Length.Value);
            code.Add(I(Op.SetMeshOutputsEXT, vertices, primitives));
            uint start = R(Op.Load, ShaderType.U32, localIndex);
            uint step = SpecializationConvert(ShaderType.U32, function.WorkgroupSize[0]);
            foreach (var dimension in function.WorkgroupSize.Skip(1)) step = R(Op.IMul, ShaderType.U32, step, SpecializationConvert(ShaderType.U32, dimension));
            uint counter = Id(); variables.Add(I(Op.Variable, Pointer(Type(ShaderType.U32), 7), counter, 7));

            void Copy(ShaderType.Array array, int memberIndex, uint count, bool primitive)
            {
                var structure = (ShaderType.Structure)array.Element;
                var fields = new List<(uint Variable, ShaderType Type, StructMember Member, uint Index)>();
                for (int field = 0; field < structure.Members.Count; field++)
                {
                    var member = structure.Members[field]; var binding = member.Binding!;
                    if (member.Type is ShaderType.Scalar { Width: 2 } or ShaderType.Vector { Component.Width: 2 }) capabilities.Add(4436);
                    ShaderType physical = WorkgroupType(member.Type);
                    uint outputArray = Id(); declarations.Add(I(Op.TypeArray, outputArray, Type(physical), Constant(Expression.U32(array.Length!.Value))));
                    uint variable = Id(); declarations.Add(I(Op.Variable, Pointer(outputArray, 3), variable, 3));
                    Name(variable, function.Name + "_" + member.Name); interfaces.Add(variable);
                    IoDecorations(variable, binding, false);
                    if (primitive && binding.Builtin is not ("point_index" or "line_indices" or "triangle_indices")) Decorate(variable, 5271);
                    fields.Add((variable, physical, member, (uint)field));
                }
                code.Add(I(Op.Store, counter, start));
                uint header = Id(), body = Id(), continuing = Id(), done = Id();
                code.Add(I(Op.Branch, header)); code.Add(I(Op.Label, header));
                uint index = R(Op.Load, ShaderType.U32, counter);
                uint active = R(Op.ULessThan, ShaderType.Bool, index, count);
                code.Add(I(Op.LoopMerge, done, continuing, 0)); code.Add(I(Op.BranchConditional, active, body, done)); code.Add(I(Op.Label, body));
                foreach (var field in fields)
                {
                    uint source = R(Op.AccessChain, new ShaderType.Pointer(field.Type, AddressSpace.Workgroup), aggregate,
                        Constant(Expression.U32((uint)memberIndex)), index, Constant(Expression.U32(field.Index)));
                    uint value = Read(source, field.Type, info.Variable.MemoryDecorations
                        | info.Structure.Members[memberIndex].MemoryDecorations | field.Member.MemoryDecorations);
                    if (options.AdjustCoordinateSpace && !primitive && field.Member.Binding!.Builtin == "position")
                    {
                        uint y = R(Op.CompositeExtract, ShaderType.F32, value, 1);
                        value = R(Op.CompositeInsert, field.Type, R(Op.FNegate, ShaderType.F32, y), value, 1);
                    }
                    uint target = Id(); code.Add(I(Op.AccessChain, Pointer(Type(field.Type), 3), target, field.Variable, index));
                    code.Add(I(Op.Store, target, value));
                }
                code.Add(I(Op.Branch, continuing)); code.Add(I(Op.Label, continuing));
                code.Add(I(Op.Store, counter, R(Op.IAdd, ShaderType.U32, index, step)));
                code.Add(I(Op.Branch, header)); code.Add(I(Op.Label, done));
            }
            Copy(info.Vertices, info.VerticesMember, vertices, false);
            Copy(info.Primitives, info.PrimitivesMember, primitives, true);
        }
    }
}
