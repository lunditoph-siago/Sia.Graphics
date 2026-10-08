using Sia.Spirv.Naga.Front;
using Sia.Spirv.Naga.Valid;

namespace Sia.Spirv.Naga.Tests;

public class AbstractAccessTests
{
    [Theory]
    [InlineData("array(1, 2, 3, 4)")][InlineData("vec4(1, 2, 3, 4)")]
    public void DynamicIndexMaterializesAbstractContainerBeforeAccess(string container)
    {
        string source = $"const values = {container}; @compute @workgroup_size(1) fn main(@builtin(local_invocation_index) index: u32) {{ let dynamic: i32 = values[index]; let constant: f32 = values[0]; }}";
        var bytes = ShaderTranslator.WgslToSpirv(source);
        ModuleValidator.Validate(WgslReader.Parse(ShaderTranslator.SpirvToWgsl(bytes)));
    }

    [Theory]
    [InlineData("array(1, 2, 3, 4)")][InlineData("vec4(1, 2, 3, 4)")]
    public void DynamicIndexDoesNotRetainAbstractElementConversion(string container) =>
        Assert.Throws<ShaderException>(() => ShaderTranslator.WgslToSpirv($"const values = {container}; @compute @workgroup_size(1) fn main(@builtin(local_invocation_index) index: u32) {{ let bad: f32 = values[index]; }}"));
}
