using Sia.Spirv.Compiler.Compilation;

namespace Sia.Spirv.Compiler.Tests;

public sealed class SpirvPassConfigurationTests
{
    [Fact]
    public void LoadReadsAndTrimsLlvmPasses()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sia-spirv-passes-{Guid.NewGuid():N}.json");
        try {
            File.WriteAllText(path, "{\"llvmPasses\": \"  sroa,mem2reg  \"}");

            var configuration = SpirvPassConfiguration.Load(path);

            Assert.Equal("sroa,mem2reg", configuration.LlvmPasses);
        }
        finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadRejectsAnEmptyPipeline()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sia-spirv-passes-{Guid.NewGuid():N}.json");
        try {
            File.WriteAllText(path, "{\"llvmPasses\": \" \"}");

            Assert.Throws<InvalidDataException>(() => SpirvPassConfiguration.Load(path));
        }
        finally {
            File.Delete(path);
        }
    }
}
