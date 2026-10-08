using Sia.Spirv.Naga.Back;
using Sia.Spirv.Naga.Front;
using Sia.Spirv.Naga.IR;
using Sia.Spirv.Naga.Spirv;
using Sia.Spirv.Naga.Valid;

namespace Sia.Spirv.Naga.Tests;

public class TaskPayloadTests
{
    internal const string AtomicSource = """
        enable wgpu_mesh_shader;
        var<task_payload> unused:bool;
        var<task_payload> payload:atomic<u32>;
        @task @payload(payload) @workgroup_size(2) fn main()->@builtin(mesh_task_size) vec3u {
            atomicStore(&payload,0u);
            let old=atomicAdd(&payload,1u);
            return vec3u(old+1u);
        }
        """;
    internal static string MeshAtomicSource => MeshShaderTests.Source.Replace("visible:bool", "visible:atomic<u32>")
        .Replace("return payload.visible;", "return atomicLoad(&payload.visible)!=0u;")
        .Replace("payload.visible=true;", "atomicStore(&payload.visible,1u);");

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PayloadAtomicsAndUnusedSmallVariablesTranslate(bool mesh)
    {
        var module = WgslReader.Parse(mesh ? MeshAtomicSource : AtomicSource); ModuleValidator.Validate(module);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
        var constants = binary.Instructions.Where(i => (Op)i.Opcode == Op.Constant).ToDictionary(i => i.Operands[1], i => i.Operands[2]);
        Assert.All(binary.Instructions.Where(i => (Op)i.Opcode is Op.AtomicLoad or Op.AtomicStore or Op.AtomicIAdd), i => Assert.Equal(2u, constants[i.Operands[(Op)i.Opcode == Op.AtomicStore ? 1 : 3]]));
        var imported = SpirvReader.Parse(binary.ToBytes()); ModuleValidator.Validate(imported);
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(imported)));
    }

    [Theory]
    [InlineData("atomicAdd(&payload.visible,1u)")]
    [InlineData("atomicExchange(&payload.visible,1u)")]
    public void MeshPayloadAtomicWritesInHelpersAreRejected(string operation)
    {
        var module = WgslReader.Parse(MeshAtomicSource.Replace("atomicLoad(&payload.visible)", operation));
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(module));
    }

    [Theory]
    [InlineData("bool")] [InlineData("f16")]
    public void SelectedSmallPayloadStillFails(string type)
    {
        string source = "enable wgpu_mesh_shader; enable f16; var<task_payload> payload:" + type
            + "; @task @payload(payload) @workgroup_size(1) fn main()->@builtin(mesh_task_size) vec3u {return vec3u(1);}";
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(WgslReader.Parse(source)));
    }
}
