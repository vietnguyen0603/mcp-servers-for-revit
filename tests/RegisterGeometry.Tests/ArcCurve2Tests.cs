using System;
using RegisterGeometry;
using TUnit.Core;
using TUnit.Assertions;

namespace RegisterGeometry.Tests;

public class ArcCurve2Tests
{
    [Test]
    public async Task QuarterArc_HasExpectedLengthAndBounds()
    {
        var centre = new Point2(0, 0);
        var start = new Point2(10, 0); // angle 0
        var end = new Point2(0, 10); // angle 90deg (CCW)
        var arc = new ArcCurve2(start, end, centre, 10, isFullCircle: false);

        var expectedSweep = Math.PI / 2;
        await Assert.That(arc.Length).IsEqualTo(expectedSweep * 10).Within(1e-9);
        await Assert.That(arc.SweepAngleDeg).IsEqualTo(90).Within(1e-9);

        var bounds = arc.Bounds;
        // Quarter arc from (10,0) to (0,10) sweeps through (sqrt(50), sqrt(50))
        await Assert.That(bounds.MinX).IsEqualTo(0);
        await Assert.That(bounds.MinY).IsEqualTo(0);
        await Assert.That(bounds.MaxX).IsEqualTo(10);
        await Assert.That(bounds.MaxY).IsEqualTo(10);
    }

    [Test]
    public async Task FullCircle_HasCompleteBounds()
    {
        var arc = new ArcCurve2(
            new Point2(5, 0), new Point2(5, 0),
            new Point2(0, 0), 5, isFullCircle: true);
        var b = arc.Bounds;
        await Assert.That(b.MinX).IsEqualTo(-5);
        await Assert.That(b.MaxX).IsEqualTo(5);
        await Assert.That(b.MinY).IsEqualTo(-5);
        await Assert.That(b.MaxY).IsEqualTo(5);
        await Assert.That(arc.Length).IsEqualTo(2 * Math.PI * 5).Within(1e-9);
    }

    [Test]
    public async Task SamplePoint_QuarterArc_SitsOnDiagonal()
    {
        var arc = new ArcCurve2(
            new Point2(10, 0),
            new Point2(0, 10),
            new Point2(0, 0),
            10,
            isFullCircle: false);
        var mid = arc.SamplePoint;
        await Assert.That(mid.X).IsEqualTo(10 * Math.Cos(Math.PI / 4)).Within(1e-9);
        await Assert.That(mid.Y).IsEqualTo(10 * Math.Sin(Math.PI / 4)).Within(1e-9);
    }

    [Test]
    public async Task Tangent_QuarterArc_IsRotated45Deg()
    {
        var arc = new ArcCurve2(
            new Point2(10, 0),
            new Point2(0, 10),
            new Point2(0, 0),
            10,
            isFullCircle: false);
        var t = arc.Tangent;
        // Tangent at angle 45deg (CCW) points at -sin(45), cos(45).
        var expectedX = -Math.Sin(Math.PI / 4);
        var expectedY = Math.Cos(Math.PI / 4);
        await Assert.That(t.X).IsEqualTo(expectedX).Within(1e-9);
        await Assert.That(t.Y).IsEqualTo(expectedY).Within(1e-9);
    }
}
