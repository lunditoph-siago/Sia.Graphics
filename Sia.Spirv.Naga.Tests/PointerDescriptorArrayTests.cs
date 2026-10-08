using Sia.Spirv.Naga.Back;
using Sia.Spirv.Naga.Front;
using Sia.Spirv.Naga.Spirv;
using Sia.Spirv.Naga.Valid;

namespace Sia.Spirv.Naga.Tests;

public class PointerDescriptorArrayTests
{
    internal static SpirvBinary Fixture(string kind, string mode, bool privateSlot = false) => mode switch
    {
        "phi" => PointerPhiTests.Fixture(kind, "branch", false),
        "slot" => PointerMemoryTests.Fixture(kind, "select", false, privateSlot),
        "loop" => PointerMemoryTests.Fixture(kind, "swap", false, privateSlot),
        "return" or "nested" => PointerReturnTests.Fixture(kind, "select", false, mode == "nested", true),
        _ => PointerSelectionTests.Fixture(kind, false, false)
    };

    [Theory]
    [InlineData("descriptor", "select", false)] [InlineData("descriptor", "phi", false)]
    [InlineData("descriptor", "slot", true)] [InlineData("descriptor", "loop", false)]
    [InlineData("descriptor", "return", false)] [InlineData("descriptor", "nested", false)]
    [InlineData("descriptor-same", "select", false)] [InlineData("descriptor-same", "phi", false)]
    [InlineData("descriptor-same", "slot", true)] [InlineData("descriptor-same", "loop", false)]
    [InlineData("descriptor-same", "return", false)] [InlineData("descriptor-same", "nested", false)]
    [InlineData("descriptor-atomic", "select", false)] [InlineData("descriptor-atomic", "loop", true)]
    [InlineData("descriptor-atomic", "nested", false)]
    [InlineData("descriptor-dynamic", "select", false)] [InlineData("descriptor-dynamic", "phi", false)]
    [InlineData("descriptor-dynamic", "slot", true)] [InlineData("descriptor-dynamic", "loop", false)]
    [InlineData("descriptor-dynamic", "return", false)] [InlineData("descriptor-dynamic", "nested", false)]
    [InlineData("descriptor-dynamic-atomic", "select", false)] [InlineData("descriptor-dynamic-atomic", "loop", true)]
    [InlineData("descriptor-dynamic-atomic", "nested", false)]
    public void DescriptorElementAddressesRetainSelectionAndCapturedIndices(string kind, string mode, bool privateSlot)
    {
        var module = SpirvReader.Parse(Fixture(kind, mode, privateSlot).ToBytes()); ModuleValidator.Validate(module);
        ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Theory] [InlineData("select")] [InlineData("loop")] [InlineData("nested")]
    public void OneDescriptorElementAllowsFiniteAddressComparison(string mode)
    {
        var module = SpirvReader.Parse(PointerComparisonTests.Fixture("descriptor-same", mode).ToBytes());
        ModuleValidator.Validate(module); ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Theory] [InlineData("select")] [InlineData("loop")] [InlineData("nested")]
    public void ARepeatedDescriptorPointerValueComparesReflexively(string mode)
    {
        var module = SpirvReader.Parse(PointerComparisonTests.Fixture("descriptor", mode, "self").ToBytes());
        ModuleValidator.Validate(module); ModuleValidator.Validate(SpirvReader.Parse(SpirvWriter.Write(module)));
        ModuleValidator.Validate(WgslReader.Parse(WgslWriter.Write(module)));
    }

    [Theory] [InlineData("select")] [InlineData("loop")] [InlineData("nested")]
    public void DifferentDescriptorElementsDoNotProveDifferentBufferAddresses(string mode)
    {
        var error = Assert.Throws<ShaderException>(() => SpirvReader.Parse(PointerComparisonTests.Fixture("descriptor", mode, "different-field").ToBytes()));
        Assert.Equal(DiagnosticStage.SpirvParse, error.Diagnostic.Stage); Assert.Contains("potentially aliased", error.Message);
    }
}
