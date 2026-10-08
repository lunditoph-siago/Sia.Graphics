namespace Sia.Spirv.Compiler.Translation.IR;

/// <summary>Validated shape of the workgroup aggregate used by WGSL mesh entries.</summary>
internal sealed record MeshShaderInfo(GlobalVariable Variable, ShaderType.Structure Structure,
    int VerticesMember, int PrimitivesMember, int VertexCountMember, int PrimitiveCountMember,
    ShaderType.Array Vertices, ShaderType.Array Primitives, uint Topology)
{
    internal static MeshShaderInfo Inspect(Module module, ShaderFunction entry)
    {
        static ShaderException Error(string message) => new(DiagnosticStage.Validation, message);
        var variable = module.Globals.SingleOrDefault(g => g.Name == entry.MeshOutput)
            ?? throw Error("Mesh entry requires an output variable.");
        if (variable.Space != AddressSpace.Workgroup || variable.Type is not ShaderType.Structure structure)
            throw Error("Mesh output must be a workgroup structure.");
        int Member(string builtin)
        {
            int[] indices = structure.Members.Select((m, i) => (m, i)).Where(p => p.m.Binding == new IoBinding(Builtin: builtin)).Select(p => p.i).ToArray();
            if (indices.Length != 1) throw Error("Mesh output requires exactly one " + builtin + " member.");
            return indices[0];
        }
        if (structure.Members.Count != 4) throw Error("Mesh output requires four builtin members.");
        int v = Member("vertices"), p = Member("primitives"), vc = Member("vertex_count"), pc = Member("primitive_count");
        if (structure.Members[vc].Type != ShaderType.U32 || structure.Members[pc].Type != ShaderType.U32)
            throw Error("Mesh output counts must be u32.");
        if (structure.Members[v].Type is not ShaderType.Array { Length: > 0, Element: ShaderType.Structure } vertices
            || structure.Members[p].Type is not ShaderType.Array { Length: > 0, Element: ShaderType.Structure } primitives)
            throw Error("Mesh vertex/primitive outputs require fixed arrays of structures.");
        var primitive = (ShaderType.Structure)primitives.Element;
        var indices = primitive.Members.Where(m => m.Binding?.Builtin is "point_index" or "line_indices" or "triangle_indices").ToArray();
        if (indices.Length != 1) throw Error("Mesh primitive output requires exactly one index builtin.");
        uint topology = indices[0].Binding!.Builtin switch { "point_index" => 27u, "line_indices" => 5269u, _ => 5298u };
        ShaderType expected = topology == 27 ? ShaderType.U32 : new ShaderType.Vector(topology == 5269 ? 2 : 3, ShaderType.U32);
        if (indices[0].Type != expected) throw Error("Mesh primitive index has the wrong type.");
        return new(variable, structure, v, p, vc, pc, vertices, primitives, topology);
    }
}
