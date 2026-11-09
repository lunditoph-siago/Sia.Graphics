using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Tests.Translation;

public sealed class SpirvNativeBuiltinTests
{
    [Fact]
    public void LdexpIsAdmittedByTheCanonicalPipeline()
    {
        var source = """
            @group(0) @binding(0) var<storage, read_write> data: array<f32>;
            @compute @workgroup_size(1) fn main() { data[1] = ldexp(data[0], -1); }
            """;
        var binary = ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default);
        string result = ShaderTranslator.SpirvToWgsl(binary, SpirvCompilationTarget.Default);
        Assert.Contains("ldexp(", result);
        WgslReader.Parse(result);
    }

    public static TheoryData<string, string, string, double[]> NativeOverloads => new() {
        { "int", "OpBitCount %int %u", "countOneBits(", [32] },
        { "uint", "OpBitCount %uint %i", "countOneBits(", [32] },
        { "v2int", "OpBitCount %v2int %vu", "countOneBits(", [32, 1] },
        { "v2uint", "OpBitCount %v2uint %vi", "countOneBits(", [32, 1] },
        { "int", "OpBitReverse %int %i", "reverseBits(", [-1] },
        { "uint", "OpExtInst %uint %glsl SAbs %u", "abs(i32(", [1] },
        { "uint", "OpExtInst %uint %glsl SSign %u", "sign(i32(", [uint.MaxValue] },
        { "uint", "OpExtInst %uint %glsl SClamp %u %ulo %ihi", "clamp(i32(", [1] },
        { "int", "OpExtInst %int %glsl UClamp %i %ilo %uhi", "clamp(u32(", [3] },
        { "v2uint", "OpExtInst %v2uint %glsl SClamp %vu %vi %vi", "clamp(vec2<i32>(", [uint.MaxValue, 1] },
        { "v2int", "OpExtInst %v2int %glsl UClamp %vi %vu %vu", "clamp(vec2<u32>(", [-1, 1] },
        { "uint", "OpExtInst %uint %glsl SMin %u %ulo", "min(i32(", [uint.MaxValue] },
        { "int", "OpExtInst %int %glsl UMin %i %ulo", "min(u32(", [1] },
        { "uint", "OpExtInst %uint %glsl SMax %u %ulo", "max(i32(", [1] },
        { "int", "OpExtInst %int %glsl UMax %i %ulo", "max(u32(", [-1] },
        { "uint", "OpExtInst %uint %glsl FindSMsb %u", "firstLeadingBit(i32(", [uint.MaxValue] },
        { "int", "OpExtInst %int %glsl FindUMsb %i", "firstLeadingBit(u32(", [31] },
        { "int", "OpExtInst %int %glsl FindILsb %u", "firstTrailingBit(", [0] },
        { "float", "OpExtInst %float %glsl Ldexp %f %u", "ldexp(", [1] },
        { "v2float", "OpExtInst %v2float %glsl Ldexp %vf %vu", "ldexp(", [1, 4] },
        { "float", "OpExtInst %float %glsl FaceForward %f %negative %f", "select(", [2] },
        { "float", "OpExtInst %float %glsl FaceForward %f %f %f", "select(", [-2] },
        { "float", "OpExtInst %float %glsl Reflect %f %negative", " * ", [-2] },
        { "float", "OpExtInst %float %glsl Refract %negative %negative %half", "refract(", [1] },
    };

    [SpirvToolchainTheory]
    [Trait("Category", "Toolchain")]
    [MemberData(nameof(NativeOverloads))]
    public void NativeBuiltinOverloadsRetainTheirResultTypesAndValidate(
        string type, string operation, string expectedText, double[] expectedValues)
    {
        byte[] binary = SpirvTestToolchain.Assemble(NativeBuiltinFixture.Source(type, operation));
        SpirvTestToolchain.Validate(binary);
        string wgsl = ShaderTranslator.SpirvToWgsl(binary, SpirvCompilationTarget.Default);
        Assert.True(wgsl.Contains(expectedText, StringComparison.Ordinal), operation + "\n" + wgsl);
        var parsed = WgslReader.Parse(wgsl);
        var output = parsed.Globals.Single(g => g.Binding?.Binding == 0);
        ShaderType expected = type switch {
            "uint" => ShaderType.U32, "int" => ShaderType.I32, "float" => ShaderType.F32,
            "v2uint" => new ShaderType.Vector(2, ShaderType.U32), "v2int" => new ShaderType.Vector(2, ShaderType.I32),
            _ => new ShaderType.Vector(2, ShaderType.F32)
        };
        Assert.Equal(expected, ((ShaderType.Structure)output.Type).Members[0].Type);
        Assert.Equal(expectedValues, NativeBuiltinFixture.Evaluate(parsed));
        SpirvTestToolchain.Validate(ShaderTranslator.WgslToSpirv(wgsl, SpirvCompilationTarget.Default));
    }
}
