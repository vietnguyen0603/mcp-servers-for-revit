using RegisterGeometry;
using TUnit.Core;
using TUnit.Assertions;

namespace RegisterGeometry.Tests;

public class SegmentTests
{
    [Test]
    public async Task Length_IsEuclidean()
    {
        var s = new Segment(new Point2(0, 0), new Point2(3, 4));
        await Assert.That(s.Length).IsEqualTo(5);
    }

    [Test]
    public async Task Tangent_IsNormalized()
    {
        var s = new Segment(new Point2(0, 0), new Point2(3, 4));
        var t = s.Tangent;
        await Assert.That(t.Length).IsEqualTo(1);
    }

    [Test]
    public async Task Project_Inside_ReturnsInterpolatedPoint()
    {
        var s = new Segment(new Point2(0, 0), new Point2(10, 0));
        var projected = s.Project(new Point2(5, 7));
        await Assert.That(projected.X).IsEqualTo(5);
        await Assert.That(projected.Y).IsEqualTo(0);
    }

    [Test]
    public async Task Project_Outside_ClampsToNearestEndpoint()
    {
        var s = new Segment(new Point2(0, 0), new Point2(10, 0));
        var before = s.Project(new Point2(-5, 3));
        var after = s.Project(new Point2(15, -2));
        await Assert.That(before).IsEqualTo(new Point2(0, 0));
        await Assert.That(after).IsEqualTo(new Point2(10, 0));
    }

    [Test]
    public async Task SignedDistance_PositiveOnLeft_NegativeOnRight()
    {
        var s = new Segment(new Point2(0, 0), new Point2(10, 0));
        // Directed along +X: +Y is left.
        await Assert.That(s.SignedDistance(new Point2(5, 3))).IsEqualTo(3);
        await Assert.That(s.SignedDistance(new Point2(5, -3))).IsEqualTo(-3);
    }

    [Test]
    public async Task Offset_TranslatesPerpendicular()
    {
        var s = new Segment(new Point2(0, 0), new Point2(10, 0));
        var offset = s.Offset(5);
        // Left of +X is +Y.
        await Assert.That(offset.Start).IsEqualTo(new Point2(0, 5));
        await Assert.That(offset.End).IsEqualTo(new Point2(10, 5));
    }

    [Test]
    public async Task Canonicalise_NormalisesEndpointOrder()
    {
        var ab = new Segment(new Point2(0, 0), new Point2(10, 5));
        var ba = new Segment(new Point2(10, 5), new Point2(0, 0));
        await Assert.That(ab.Canonicalise()).IsEqualTo(ba.Canonicalise());
    }

    [Test]
    public async Task Bounds_AreAxisAlignedTight()
    {
        var s = new Segment(new Point2(-2, 5), new Point2(4, -3));
        var b = s.Bounds;
        await Assert.That(b.MinX).IsEqualTo(-2);
        await Assert.That(b.MaxX).IsEqualTo(4);
        await Assert.That(b.MinY).IsEqualTo(-3);
        await Assert.That(b.MaxY).IsEqualTo(5);
    }

    [Test]
    public async Task DegenerateSegment_HasZeroTangentSafely()
    {
        var s = new Segment(new Point2(3, 3), new Point2(3, 3));
        // Length is zero, but operations should not throw.
        await Assert.That(s.Length).IsEqualTo(0);
        var t = s.Tangent;
        await Assert.That(t).IsEqualTo(Vector2.Zero);
    }
}
