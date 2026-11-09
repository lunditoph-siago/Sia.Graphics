using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation;
using Sia.Spirv.Compiler.Translation.Spirv;

namespace Sia.Spirv.Compiler.Tests.Support;

internal static class IntegerTestShader
{
    internal static string Source(string expression, string type = "u32")
    {
        string countType = type.Contains("vec3", StringComparison.Ordinal) ? "vec3<u32>" : "u32";
        if (!expression.Contains("<<", StringComparison.Ordinal) && !expression.Contains(">>", StringComparison.Ordinal)) countType = type;
        return $$"""
            @group(0) @binding(0) var<storage, read_write> data: array<{{type}}>;
            @compute @workgroup_size(1) fn main() {
                let x = data[0];
                let y = {{countType}}(data[1]);
                data[2] = {{expression}};
            }
            """;
    }

    internal static SpirvBinary Compile(string expression, string type = "u32")
        => SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(Source(expression, type), SpirvCompilationTarget.Default));
}
