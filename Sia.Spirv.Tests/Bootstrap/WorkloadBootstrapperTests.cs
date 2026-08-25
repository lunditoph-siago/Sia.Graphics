using System.Text.Json;
using Sia.Spirv.Bootstrap;

namespace Sia.Spirv.Tests.Bootstrap;

public sealed class WorkloadBootstrapperTests
{
    [Fact]
    public void CreatesVersionedRidAwareManifest()
    {
        using var document = JsonDocument.Parse(
            WorkloadBootstrapper.CreateManifestJson("1.2.3-preview.4"));
        var root = document.RootElement;
        var toolchain = root.GetProperty("packs").GetProperty(
            "Sia.Spirv.Toolchain");
        var aliases = toolchain.GetProperty("alias-to");

        Assert.Equal("1.2.3-preview.4", root.GetProperty("version").GetString());
        Assert.Equal(
            "Sia.Spirv.Toolchain.win-x64",
            aliases.GetProperty("win-x64").GetString());
        Assert.Equal(
            "Sia.Spirv.Toolchain.linux-x64",
            aliases.GetProperty("linux-x64").GetString());
    }
}
