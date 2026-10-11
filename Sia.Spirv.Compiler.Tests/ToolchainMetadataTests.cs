using System.Text.Json;
using Sia.Spirv.Runtime;

namespace Sia.Spirv.Compiler.Tests;

public sealed class ToolchainMetadataTests
{
    [Fact]
    public void ToolchainMetadataRecordsOnlyActiveCompilerIdentities()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var toolchain = new SpirvToolchainInfo("llvm", "spirv-tools", "managed", "hash");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(toolchain, options));
        Assert.Equal(["llvm", "spirvTools", "translator", "translatorSha256"],
            document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(toolchain, JsonSerializer.Deserialize<SpirvToolchainInfo>(
            """
            {"llvm":"llvm","spirvTools":"spirv-tools","translator":"managed",
             "translatorSha256":"hash","retiredTranslator":"ignored","retiredHash":"ignored"}
            """, options));
    }
}
