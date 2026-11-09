using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;

namespace Sia.Spirv.Compiler.Tests.Translation;

public sealed class SpirvNativeBuiltinTests
{
    public static TheoryData<string, string, double[]> NativeOverloads => new() {
        { "int", "OpBitCount %int %u", [32] },
        { "uint", "OpExtInst %uint %glsl SClamp %u %ulo %ihi", [1] },
        { "int", "OpExtInst %int %glsl UClamp %i %ilo %uhi", [3] },
        { "v2uint", "OpExtInst %v2uint %glsl SClamp %vu %vi %vi", [uint.MaxValue, 1] },
        { "float", "OpExtInst %float %glsl Ldexp %f %u", [1] },
        { "float", "OpExtInst %float %glsl Refract %negative %negative %half", [1] }
    };

    [SpirvToolchainTheory]
    [Trait("Category", "Toolchain")]
    [MemberData(nameof(NativeOverloads))]
    public void NativeBuiltinOverloadsRetainTheirResultTypesAndValidate(
        string type, string operation, double[] expectedValues)
    {
        byte[] binary = SpirvTestToolchain.Assemble(NativeBuiltinFixture.Source(type, operation));
        SpirvTestToolchain.Validate(binary);
        string wgsl = ShaderTranslator.SpirvToWgsl(binary, SpirvCompilationTarget.Default);
        var parsed = WgslReader.Parse(wgsl);
        var output = parsed.Globals.Single(g => g.Binding?.Binding == 0);
        ShaderType expected = type switch {
            "uint" => ShaderType.U32, "int" => ShaderType.I32, "float" => ShaderType.F32,
            "v2uint" => new ShaderType.Vector(2, ShaderType.U32),
            _ => throw new InvalidOperationException("Unknown fixture type: " + type)
        };
        Assert.Equal(expected, ((ShaderType.Structure)output.Type).Members[0].Type);
        Assert.Equal(expectedValues, NativeBuiltinFixture.Evaluate(parsed));
        SpirvTestToolchain.Validate(ShaderTranslator.WgslToSpirv(wgsl, SpirvCompilationTarget.Default));
    }
}
