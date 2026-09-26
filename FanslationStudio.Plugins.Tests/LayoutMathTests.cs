using FanslationStudio.Plugins.Layout;

namespace FanslationStudio.Plugins.Tests;

public class LayoutMathTests
{
    private static readonly float[] Original = [10f, 20f];

    [Fact]
    public void NeitherSet_ReturnsNull()
    {
        Assert.Null(LayoutMath.Resolve(null, null, Original));
    }

    [Fact]
    public void AbsoluteOnly_ReplacesOriginal()
    {
        Assert.Equal([70f, 0f], LayoutMath.Resolve([70f, 0f], null, Original));
    }

    [Fact]
    public void OffsetOnly_AddsToOriginal()
    {
        Assert.Equal([15f, 17f], LayoutMath.Resolve(null, [5f, -3f], Original));
    }

    [Fact]
    public void AbsoluteAndOffset_AddsOffsetToAbsolute()
    {
        Assert.Equal([75f, -3f], LayoutMath.Resolve([70f, 0f], [5f, -3f], Original));
    }

    [Fact]
    public void WrongLength_IsIgnored()
    {
        Assert.Null(LayoutMath.Resolve([1f, 2f, 3f], null, Original));
        Assert.Equal([15f, 25f], LayoutMath.Resolve([1f], [5f, 5f], Original));
        Assert.True(LayoutMath.IsMalformed([1f], 2));
        Assert.False(LayoutMath.IsMalformed(null, 2));
    }

    [Fact]
    public void ReapplyingIsStable_BecauseItAlwaysUsesTheOriginal()
    {
        var first = LayoutMath.Resolve(null, [5f, 5f], Original);
        var second = LayoutMath.Resolve(null, [5f, 5f], Original);
        Assert.Equal(first, second);
    }
}
