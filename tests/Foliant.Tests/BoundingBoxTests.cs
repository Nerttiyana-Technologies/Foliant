using Xunit;

namespace Foliant.Tests;

public class BoundingBoxTests
{
    [Fact]
    public void Dimensions_AreComputedFromCorners()
    {
        var b = new BoundingBox(10, 20, 110, 70);
        Assert.Equal(100, b.Width);
        Assert.Equal(50, b.Height);
        Assert.Equal(60, b.CenterX);
        Assert.Equal(45, b.CenterY);
        Assert.Equal(5000, b.Area);
    }

    [Fact]
    public void Contains_IsInclusiveOfEdges()
    {
        var b = new BoundingBox(0, 0, 10, 10);
        Assert.True(b.Contains(0, 0));
        Assert.True(b.Contains(10, 10));
        Assert.True(b.Contains(5, 5));
        Assert.False(b.Contains(10.01f, 5));
        Assert.False(b.Contains(5, -0.01f));
    }

    [Fact]
    public void ContainsCenterOf_UsesOtherBoxCenter()
    {
        var region = new BoundingBox(0, 0, 100, 100);
        var insideEdge = new BoundingBox(90, 90, 130, 130);   // center (110,110) outside
        var spanning = new BoundingBox(40, 90, 60, 130);      // center (50,110) outside
        var contained = new BoundingBox(40, 40, 60, 60);      // center (50,50) inside

        Assert.False(region.ContainsCenterOf(insideEdge));
        Assert.False(region.ContainsCenterOf(spanning));
        Assert.True(region.ContainsCenterOf(contained));
    }

    [Fact]
    public void Union_CoversBothBoxes()
    {
        var u = BoundingBox.Union(new BoundingBox(0, 0, 10, 10), new BoundingBox(5, -5, 20, 8));
        Assert.Equal(new BoundingBox(0, -5, 20, 10), u);
    }

    [Fact]
    public void IntersectionOverMinArea_FullOverlapIsOne()
    {
        var big = new BoundingBox(0, 0, 100, 100);
        var small = new BoundingBox(10, 10, 20, 20);
        Assert.Equal(1f, BoundingBox.IntersectionOverMinArea(big, small), 3);
    }

    [Fact]
    public void IntersectionOverMinArea_DisjointIsZero()
    {
        var a = new BoundingBox(0, 0, 10, 10);
        var b = new BoundingBox(20, 20, 30, 30);
        Assert.Equal(0f, BoundingBox.IntersectionOverMinArea(a, b));
    }

    [Fact]
    public void IntersectionOverMinArea_EmptyBoxIsZero()
    {
        var a = new BoundingBox(0, 0, 0, 0);
        var b = new BoundingBox(0, 0, 10, 10);
        Assert.Equal(0f, BoundingBox.IntersectionOverMinArea(a, b));
    }
}
