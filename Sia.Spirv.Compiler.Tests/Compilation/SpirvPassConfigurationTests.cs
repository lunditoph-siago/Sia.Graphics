using Sia.Spirv.Compiler.Compilation;

namespace Sia.Spirv.Compiler.Tests.Compilation;

public sealed class SpirvPassConfigurationTests
{
    [Fact]
    public void LoadReadsAndTrimsLlvmPasses()
    {
        using var directory = new TestDirectory();
        string path = directory.File("passes.json");
        File.WriteAllText(path, """{"llvmPasses": "  sroa,mem2reg  "}""");

        Assert.Equal("sroa,mem2reg", SpirvPassConfiguration.Load(path).LlvmPasses);
    }

    [Fact]
    public void LoadRejectsAnEmptyPipeline()
    {
        using var directory = new TestDirectory();
        string path = directory.File("passes.json");
        File.WriteAllText(path, """{"llvmPasses": " "}""");

        Assert.Throws<InvalidDataException>(() => SpirvPassConfiguration.Load(path));
    }
}
