using Sia.Spirv.Bootstrap;

namespace Sia.Spirv.Tests.Bootstrap;

public sealed class DotNetSdkTests
{
    [Theory]
    [InlineData("11.0.100-preview.7.26381.103", "11.0.100-preview.7")]
    [InlineData("11.0.100", "11.0.100")]
    public void GetsFeatureBand(string sdkVersion, string expected)
    {
        Assert.Equal(expected, DotNetSdk.GetFeatureBand(sdkVersion));
    }

    [Fact]
    public void GetsSelectedSdkDirectory()
    {
        const string installedSdks = "10.0.100 [C:\\dotnet\\sdk]\n" +
            "11.0.100-preview.7.26381.103 [C:\\portable\\sdk]\n";

        var directory = DotNetSdk.GetSdkDirectory(
            installedSdks, "11.0.100-preview.7.26381.103");

        Assert.Equal("C:\\portable\\sdk", directory);
    }
}
