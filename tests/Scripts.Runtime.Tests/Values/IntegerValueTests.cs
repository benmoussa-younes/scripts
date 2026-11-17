namespace Scripts.Runtime.Tests.Values;

/// <summary>
/// The small-integer cache (<c>-128..255</c>) is a pure optimization: instances are shared within the
/// range, but the value, structural equality, hashing and map-key role remain identical
/// (DESIGN §3.6). An integer outside the range stays a fresh instance.
/// </summary>
public sealed class IntegerValueTests
{
    [Theory]
    [InlineData(-128L)]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(255L)]
    public void SmallIntegersAreSharedInstances(long number) =>
        Assert.Same(IntegerValue.From(number), IntegerValue.From(number));

    [Theory]
    [InlineData(-129L)]
    [InlineData(256L)]
    [InlineData(1000L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void OutOfRangeIntegersAreDistinctInstances(long number) =>
        Assert.NotSame(IntegerValue.From(number), IntegerValue.From(number));

    [Theory]
    [InlineData(-128L)]
    [InlineData(0L)]
    [InlineData(255L)]
    [InlineData(256L)]
    [InlineData(-129L)]
    [InlineData(1000L)]
    public void ValueEqualityAndHashAreUnchanged(long number)
    {
        var left = IntegerValue.From(number);
        var right = IntegerValue.From(number);
        Assert.Equal(left, right);
        Assert.Equal(number, left.Number);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Theory]
    [InlineData(-128L)]
    [InlineData(255L)]
    [InlineData(1000L)]
    public void CachedInstanceWorksAsAMapKey(long number)
    {
        var map = new MapValue();
        map.Set(IntegerValue.From(number), new StringValue("value"));
        Assert.True(map.TryGet(IntegerValue.From(number), out var stored));
        Assert.Equal("value", ((StringValue)stored).Text);
    }
}
