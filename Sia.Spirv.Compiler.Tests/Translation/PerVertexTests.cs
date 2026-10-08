using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class PerVertexTests
{
    [Theory]
    [InlineData("f32", "", false)]
    [InlineData("vec3f", "", false)]
    [InlineData("i32", "", false)]
    [InlineData("vec2u", "", false)]
    [InlineData("f16", "enable f16;", false)]
    [InlineData("vec2<u16>", "enable wgpu_int16;", false)]
    [InlineData("vec3f", "", true)]
    [InlineData("vec2<i16>", "enable wgpu_int16;", true)]
    public void PerVertexInputsSurviveBothLanguages(string element, string enable, bool structure)
    {
        string input = $"@location(0) @interpolate(per_vertex) value:array<{element},3>";
        string source = enable + "enable wgpu_per_vertex; " + (structure
            ? $"struct Inputs {{ {input} }} @fragment fn main(input:Inputs) -> @location(0) vec4f {{ _ = input.value[1]; return vec4f(1); }}"
            : $"@fragment fn main({input}) -> @location(0) vec4f {{ _ = value[1]; return vec4f(1); }}");
        byte[] bytes = ShaderTranslator.WgslToSpirv(source);
        var binary = SpirvBinary.Parse(bytes);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 5284);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Decorate && i.Operands[1] == 5285);
        Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Extension && SpirvBinary.ReadString(i.Operands, out _) == "SPV_KHR_fragment_shader_barycentric");
        string output = ShaderTranslator.SpirvToWgsl(bytes);
        Assert.Contains("enable wgpu_per_vertex;", output);
        Assert.Contains("@interpolate(per_vertex)", output);
        ModuleValidator.Validate(WgslReader.Parse(output));
        Assert.NotEmpty(ShaderTranslator.WgslToSpirv(output));
    }

    [Theory]
    [InlineData("@fragment fn main(@location(0) @interpolate(per_vertex) value:array<f32,3>) {}")]
    [InlineData("enable wgpu_per_vertex; @fragment fn main(@location(0) @interpolate(per_vertex) value:array<f32,2>) {}")]
    [InlineData("enable wgpu_per_vertex; @fragment fn main(@location(0) @interpolate(per_vertex) value:vec3f) {}")]
    [InlineData("enable wgpu_per_vertex; @fragment fn main(@location(0) @interpolate(per_vertex) value:array<bool,3>) {}")]
    [InlineData("enable wgpu_per_vertex; @fragment fn main(@location(0) @interpolate(per_vertex) value:array<mat2x2f,3>) {}")]
    [InlineData("enable wgpu_per_vertex; @vertex fn main(@location(0) @interpolate(per_vertex) value:array<f32,3>) -> @builtin(position) vec4f { return vec4f(1); }")]
    [InlineData("enable wgpu_per_vertex; @compute @workgroup_size(1) fn main(@location(0) @interpolate(per_vertex) value:array<f32,3>) {}")]
    [InlineData("enable wgpu_per_vertex; @fragment fn main(@location(0) @interpolate(per_vertex,centroid) value:array<f32,3>) {}")]
    [InlineData("enable wgpu_per_vertex; @fragment fn main(@location(0) @interpolate(per_vertex,sample) value:array<f32,3>) {}")]
    [InlineData("enable wgpu_per_vertex; @fragment fn main(@location(0) @interpolate(per_vertex,center) value:array<f32,3>) {}")]
    public void InvalidPerVertexInterfacesAreRejected(string source)
    {
        Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv(source));
    }

    [Fact]
    public void IrWriterDiscoversRequiredEnable()
    {
        var module = new Module();
        var function = new ShaderFunction("main") { Stage = ShaderStage.Fragment };
        function.Arguments.Add(new("value", new ShaderType.Array(ShaderType.F32, 3), new(Location: 0, Interpolation: "per_vertex")));
        module.Functions.Add(function);
        Assert.Contains("enable wgpu_per_vertex;", WgslWriter.Write(module));
    }
}
