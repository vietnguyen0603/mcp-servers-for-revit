using RegisterGeometry;
using TUnit.Core;
using TUnit.Assertions;

namespace RegisterGeometry.Tests;

public class Point2Vector2Tests
{
    [Test]
    public async Task Point2_DistanceTo_IsEuclidean()
    {
        var a = new Point2(0, 0);
        var b = new Point2(3, 4);
        await Assert.That(a.DistanceTo(b)).IsEqualTo(5);
        await Assert.That(a.DistanceSquaredTo(b)).IsEqualTo(25);
    }

    [Test]
    public async Task Point2_Lerp_HalfwayInterpolates()
    {
        var a = new Point2(0, 0);
        var b = new Point2(10, 20);
        var mid = a.Lerp(b, 0.5);
        await Assert.That(mid.X).IsEqualTo(5);
        await Assert.That(mid.Y).IsEqualTo(10);
    }

    [Test]
    public async Task Vector2_Normalized_PreservesDirection()
    {
        var v = new Vector2(3, 4);
        var n = v.Normalized();
        await Assert.That(n.Length).IsEqualTo(1);
        await Assert.That(n.X).IsEqualTo(0.6);
        await Assert.That(n.Y).IsEqualTo(0.8);
    }

    [Test]
    public async Task Vector2_Normalized_OnZero_ReturnsZero()
    {
        var n = Vector2.Zero.Normalized();
        await Assert.That(n).IsEqualTo(Vector2.Zero);
    }

    [Test]
    public async Task Vector2_Dot_AndCross_Work()
    {
        var x = Vector2.UnitX;
        var y = Vector2.UnitY;
        await Assert.That(x.Dot(y)).IsEqualTo(0);
        await Assert.That(x.Cross(y)).IsEqualTo(1);
        await Assert.That(y.Cross(x)).IsEqualTo(-1);
    }

    [Test]
    public async Task Vector2_Equality_ComparesComponents()
    {
        var a = new Vector2(1, 2);
        var b = new Vector2(1, 2);
        var c = new Vector2(2, 1);
        await Assert.That(a == b).IsTrue();
        await Assert.That(a != c).IsTrue();
        await Assert.That(a.Equals(b)).IsTrue();
    }

    [Test]
    public async Task Point2_OffsetByVector_Translates()
    {
        var p = new Point2(1, 2);
        var moved = p.Offset(new Vector2(10, 20));
        await Assert.That(moved.X).IsEqualTo(11);
        await Assert.That(moved.Y).IsEqualTo(22);
    }
}
