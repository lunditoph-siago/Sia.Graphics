using System.Text;
using Xunit;

namespace Sia.WebGPU.Tests;

public sealed unsafe class CanvasSelectorTests
{
    [Theory]
    [InlineData("#canvas")]
    [InlineData("#画布-雪")]
    [InlineData("#canvas-🌍")]
    public void SelectorSupportsLengthDelimitedAndCStringReaders(string selector)
    {
        using var owned = WgpuOwnedString.CreateNullTerminated(selector);
        var expected = Encoding.UTF8.GetBytes(selector);
        var view = owned.View;

        Assert.Equal((nuint)expected.Length, view.Length);
        Assert.Equal(expected, new ReadOnlySpan<byte>(view.Data, expected.Length).ToArray());
        Assert.Equal(0, view.Data[expected.Length]);
    }

    [Fact]
    public void LongSelectorRemainsPinnedForOwnerLifetime()
    {
        var selector = "#" + new string('x', 4097) + "雪";
        var expected = Encoding.UTF8.GetBytes(selector);
        using var owned = WgpuOwnedString.CreateNullTerminated(selector);
        var pointer = (nint)owned.View.Data;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.Equal(pointer, (nint)owned.View.Data);
        Assert.Equal(expected, new ReadOnlySpan<byte>(owned.View.Data, expected.Length).ToArray());
        Assert.Equal(0, owned.View.Data[owned.View.Length]);
    }

    [Fact]
    public void EmbeddedNulIsRejectedBeforeCallingNativeSurfaceCreation()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            Wgpu.CreateCanvasSurface(default, "#canvas\0other"));
        Assert.Equal("selector", error.ParamName);
    }

    [Fact]
    public void OrdinaryStringViewsPreserveNullAndExplicitLengthSemantics()
    {
        using var missing = WgpuOwnedString.Create(null);
        using var empty = WgpuOwnedString.Create("");
        using var label = WgpuOwnedString.Create("雪\0label");

        Assert.Equal(nint.Zero, (nint)missing.View.Data);
        Assert.Equal(WgpuConstants.StrLen, missing.View.Length);
        Assert.Equal(nint.Zero, (nint)empty.View.Data);
        Assert.Equal((nuint)0, empty.View.Length);
        Assert.Equal((nuint)Encoding.UTF8.GetByteCount("雪\0label"), label.View.Length);
        Assert.Equal(0, label.View.Data[3]);
    }
}
