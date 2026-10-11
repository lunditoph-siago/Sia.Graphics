using Sia.Spirv.Compiler.Compilation;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class AbstractAccessTests
{
    [Theory]
    [InlineData("array(1, 2, 3, 4)")][InlineData("vec4(1, 2, 3, 4)")]
    public void DynamicIndexMaterializesAbstractContainerBeforeAccess(string container)
    {
        string source = $"const values = {container}; @compute @workgroup_size(1) fn main(@builtin(local_invocation_index) index: u32) {{ let dynamic: i32 = values[index]; let constant: f32 = values[0]; }}";
        var bytes = ShaderTranslator.WgslToSpirv(source, SpirvCompilationTarget.Default);
        ModuleValidator.Validate(WgslReader.Parse(ShaderTranslator.SpirvToWgsl(bytes, SpirvCompilationTarget.Default)));
    }

    [Theory]
    [InlineData("array(1, 2, 3, 4)")][InlineData("vec4(1, 2, 3, 4)")]
    public void DynamicIndexDoesNotRetainAbstractElementConversion(string container) =>
        Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv($"const values = {container}; @compute @workgroup_size(1) fn main(@builtin(local_invocation_index) index: u32) {{ let bad: f32 = values[index]; }}", SpirvCompilationTarget.Default));
}
