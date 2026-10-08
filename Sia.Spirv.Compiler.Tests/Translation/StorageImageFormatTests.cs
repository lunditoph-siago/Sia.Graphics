using Sia.Spirv.Compiler.Translation.Back;
using Sia.Spirv.Compiler.Translation.Front;
using Sia.Spirv.Compiler.Translation.IR;
using Sia.Spirv.Compiler.Translation.Spirv;
using Sia.Spirv.Compiler.Translation.Valid;

namespace Sia.Spirv.Compiler.Translation.Tests;

public class StorageImageFormatTests
{
    public static IEnumerable<object[]> Formats()
    {
        // Expected WGSL names and SPIR-V ImageFormat codes from the pinned reference.
        string[] names = ["bgra8unorm", "rgba32float", "rgba16float", "r32float", "rgba8unorm", "rgba8snorm", "rg32float", "rg16float", "rg11b10ufloat", "r16float", "rgba16unorm", "rgb10a2unorm", "rg16unorm", "rg8unorm", "r16unorm", "r8unorm", "rgba16snorm", "rg16snorm", "rg8snorm", "r16snorm", "r8snorm", "rgba32sint", "rgba16sint", "rgba8sint", "r32sint", "rg32sint", "rg16sint", "rg8sint", "r16sint", "r8sint", "rgba32uint", "rgba16uint", "rgba8uint", "r32uint", "rgb10a2uint", "rg32uint", "rg16uint", "rg8uint", "r16uint", "r8uint", "r64uint"];
        for (uint i = 0; i < names.Length; i++) yield return [names[i], i];
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void AllReferenceFormatsHaveCorrectTypesAndSpirvCodes(string format, uint code)
    {
        string component = format == "r64uint" ? "u64" : format.EndsWith("uint", StringComparison.Ordinal) ? "u32" : format.EndsWith("sint", StringComparison.Ordinal) ? "i32" : "f32";
        string source = $"@group(0) @binding(0) var image: texture_storage_2d<{format}, read_write>; @compute @workgroup_size(1) fn main() {{ textureStore(image, vec2i(0), vec4<{component}>({component}(1))); let value = textureLoad(image, vec2i(0)); }}";
        var module = WgslReader.Parse(source); ModuleValidator.Validate(module);
        var image = Assert.IsType<ShaderType.Image>(Assert.Single(module.Globals).Type);
        Assert.Equal(component == "u64" ? new ShaderType.Scalar(ScalarKind.Uint, 8) : component == "u32" ? ShaderType.U32 : component == "i32" ? ShaderType.I32 : ShaderType.F32, image.Component);
        var binary = SpirvBinary.Parse(SpirvWriter.Write(module));
        Assert.Equal(code, Assert.Single(binary.Instructions, i => (Op)i.Opcode == Op.TypeImage).Operands[7]);
        bool extended = code is >= 6 and <= 20 or >= 25 and <= 29 or >= 34 and <= 39;
        Assert.Equal(extended, binary.Instructions.Any(i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 49));
        if (code == 0)
        {
            Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 55);
            Assert.Contains(binary.Instructions, i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 56);
            Assert.Contains("bgra8unorm", WgslWriter.Write(module));
            Assert.Contains("Formatless storage image", Assert.Throws<ShaderException>(() => SpirvReader.Parse(binary.ToBytes())).Message);
        }
        else
        {
            var back = SpirvReader.Parse(binary.ToBytes()); ModuleValidator.Validate(back);
            Assert.Equal(format, Assert.IsType<ShaderType.Image>(Assert.Single(back.Globals).Type).StorageFormat);
            ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(back)));
        }
    }

    [Theory]
    [InlineData("read", true, false)][InlineData("write", false, true)][InlineData("read_write", true, true)]
    public void FormatlessCapabilitiesFollowActualOperations(string access, bool read, bool write)
    {
        string body = (write ? "textureStore(image, vec2i(0), vec4f(1));" : "") + (read ? "let value = textureLoad(image, vec2i(0));" : "");
        byte[] bytes = ShaderTranslator.WgslToSpirv($"@group(0) @binding(0) var image:texture_storage_2d<bgra8unorm,{access}>; @compute @workgroup_size(1) fn main() {{{body}}}");
        var binary = SpirvBinary.Parse(bytes);
        Assert.Equal(read, binary.Instructions.Any(i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 55));
        Assert.Equal(write, binary.Instructions.Any(i => (Op)i.Opcode == Op.Capability && i.Operands[0] == 56));
    }

    [Theory]
    [InlineData("r32unorm")][InlineData("r16ufloat")][InlineData("rgba64uint")][InlineData("RGBA8unorm")][InlineData("not_a_format")]
    public void UnknownFormatsAreRejectedAtParsingAndIrValidation(string format)
    {
        Assert.Throws<ShaderException>(() => WgslReader.Parse($"@group(0) @binding(0) var image:texture_storage_2d<{format},write>;"));
        var module = new Module(); module.Globals.Add(new("image", new ShaderType.Image(ImageDimension.D2, ShaderType.F32, StorageFormat: format, Access: StorageAccess.Write), AddressSpace.Handle, Binding: new(0, 0)));
        Assert.Throws<ShaderException>(() => ModuleValidator.Validate(module));
    }

    [Theory]
    [InlineData("r32sint")][InlineData("r32uint")][InlineData("rgb10a2uint")][InlineData("r64uint")]
    public void IrImageComponentsMustAgreeWithTheFormat(string format)
    {
        var module = new Module(); module.Globals.Add(new("image", new ShaderType.Image(ImageDimension.D2, ShaderType.F32, StorageFormat: format), AddressSpace.Handle, Binding: new(0, 0)));
        Assert.Throws<ShaderException>(() => SpirvWriter.Write(module));
        Assert.Throws<ShaderException>(() => WgslWriter.Write(module));
    }

    [Fact]
    public void MisdeclaredSpirvImageComponentsAreRejected()
    {
        const string source = "@group(0) @binding(0) var image:texture_storage_2d<r32float,write>; @compute @workgroup_size(1) fn main(){ textureStore(image,vec2i(0),vec4f(1)); }";
        var binary = SpirvBinary.Parse(ShaderTranslator.WgslToSpirv(source));
        var invalid = new SpirvBinary { Bound = binary.Bound, Instructions = binary.Instructions.Select(i => (Op)i.Opcode == Op.TypeImage ? i with { Operands = i.Operands.Take(7).Append(24u).ToArray() } : i).ToArray() };
        Assert.Throws<ShaderException>(() => SpirvReader.Parse(invalid.ToBytes()));
    }
}
