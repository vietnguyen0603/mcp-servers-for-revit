using RegisterGeometry;
using TUnit.Core;
using TUnit.Assertions;

namespace RegisterGeometry.Tests;

public class BoundsTests
{
    [Test]
    public async Task Empty_DefaultsToInvertedExtents()
    {
        var b = Bounds.Empty;
        await Assert.That(b.IsEmpty).IsTrue();
        await Assert.That(double.IsPositiveInfinity(b.MinX)).IsTrue();
        await Assert.That(double.IsNegativeInfinity(b.MaxX)).IsTrue();
    }

    [Test]
    public async Task FromPoints_TightBoxAroundPoints()
    {
        var points = new[]
        {
            new Point2(0, 0),
            new Point2(10, -5),
            new Point2(-3, 7),
        };
        var b = Bounds.FromPoints(points);
        await Assert.That(b.MinX).IsEqualTo(-3);
        await Assert.That(b.MinY).IsEqualTo(-5);
        await Assert.That(b.MaxX).IsEqualTo(10);
        await Assert.That(b.MaxY).IsEqualTo(7);
        await Assert.That(b.Width).IsEqualTo(13);
        await Assert.That(b.Height).IsEqualTo(12);
        await Assert.That(b.Centre.X).IsEqualTo(3.5);
        await Assert.That(b.Centre.Y).IsEqualTo(1);
    }

    [Test]
    public async Task Union_MergesExtents()
    {
        var a = new Bounds(0, 0, 10, 10);
        var b = new Bounds(5, -5, 20, 20);
        var u = a.Union(b);
        await Assert.That(u.MinX).IsEqualTo(0);
        await Assert.That(u.MinY).IsEqualTo(-5);
        await Assert.That(u.MaxX).IsEqualTo(20);
        await Assert.That(u.MaxY).IsEqualTo(20);
    }

    [Test]
    public async Task Union_WithEmpty_ReturnsTheOther()
    {
        var a = Bounds.Empty;
        var b = new Bounds(0, 0, 5, 5);
        await Assert.That(a.Union(b)).IsEqualTo(b);
        await Assert.That(b.Union(Bounds.Empty)).IsEqualTo(b);
    }

    [Test]
    public async Task Contains_HonoursEpsilon()
    {
        var b = new Bounds(0, 0, 10, 10);
        await Assert.That(b.Contains(new Point2(5, 5))).IsTrue();
        await Assert.That(b.Contains(new Point2(11, 5))).IsFalse();
        await Assert.That(b.Contains(new Point2(10.0005, 5), epsilon: 0.001)).IsTrue();
    }

    [Test]
    public async Task Intersects_DetectsOverlap()
    {
        var a = new Bounds(0, 0, 10, 10);
        var b = new Bounds(5, 5, 15, 15);
        var c = new Bounds(20, 20, 30, 30);
        await Assert.That(a.Intersects(b)).IsTrue();
        await Assert.That(a.Intersects(c)).IsFalse();
        await Assert.That(a.Intersects(c, epsilon: 15)).IsTrue();
    }

    [Test]
    public async Task ChebyshevDistance_ZeroInsideBox_PositiveOutside()
    {
        var b = new Bounds(0, 0, 10, 10);
        await Assert.That(b.ChebyshevDistanceTo(new Point2(5, 5))).IsEqualTo(0);
        await Assert.That(b.ChebyshevDistanceTo(new Point2(12, 5))).IsEqualTo(2);
        await Assert.That(b.ChebyshevDistanceTo(new Point2(-3, 7))).IsEqualTo(3);
    }

    [Test]
    public async Task Inflated_ExpandsByPadding()
    {
        var b = new Bounds(0, 0, 10, 10);
        var inflated = b.Inflated(2);
        await Assert.That(inflated.MinX).IsEqualTo(-2);
        await Assert.That(inflated.MaxX).IsEqualTo(12);
    }
}
