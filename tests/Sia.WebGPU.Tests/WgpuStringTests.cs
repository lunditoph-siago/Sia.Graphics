namespace Sia.WebGPU.Tests;

public sealed class WgpuStringTests
{
    [Fact]
    public void OwnedStringRoundTripsUtf8AndDisposesIdempotently()
    {
        var text = WgpuOwnedString.Create("Sia · WebGPU");

        Assert.Equal("Sia · WebGPU", WgpuStringViewText.ToString(text.View));

        text.Dispose();
        text.Dispose();
    }

    [Fact]
    public void NullAndEmptyViewsRemainDistinctAtTheAbiBoundary()
    {
        using var nullText = WgpuOwnedString.Create(null);
        using var emptyText = WgpuOwnedString.Create(string.Empty);

        Assert.Equal(nuint.MaxValue, nullText.View.Length);
        Assert.Equal((nuint)0, emptyText.View.Length);
        Assert.Equal(string.Empty, WgpuStringViewText.ToString(nullText.View));
        Assert.Equal(string.Empty, WgpuStringViewText.ToString(emptyText.View));
    }
}
