using System.Linq;
using RegisterGeometry;
using TUnit.Core;
using TUnit.Assertions;

namespace RegisterGeometry.Tests;

public class GridRegistryAndFinderTests
{
    private static GridLine GridX(double x, string id, string name = "")
    {
        var seg = new Segment(new Point2(x, -1000), new Point2(x, 1000));
        return new GridLine(id, name, seg, AxisFamily.X);
    }

    private static GridLine GridY(double y, string id, string name = "")
    {
        var seg = new Segment(new Point2(-1000, y), new Point2(1000, y));
        return new GridLine(id, name, seg, AxisFamily.Y);
    }

    [Test]
    public async Task Registry_LookupByUniqueId_Works()
    {
        var grids = new[]
        {
            GridX(0, "x-1", "1"),
            GridX(1000, "x-2", "2"),
            GridY(500, "y-A", "A"),
        };
        var registry = new GridRegistry(grids);
        await Assert.That(registry.Count).IsEqualTo(3);
        await Assert.That(registry.TryGet("x-2", out var found)).IsTrue();
        await Assert.That(found!.Name).IsEqualTo("2");
    }

    [Test]
    public async Task Registry_InFamily_OrdersByUniqueIdDeterministically()
    {
        var grids = new[]
        {
            GridX(0, "z-9", "9"),
            GridX(1000, "a-1", "1"),
            GridX(500, "m-5", "5"),
        };
        var registry = new GridRegistry(grids);
        var xFamily = registry.InFamily(AxisFamily.X);
        var orderedIds = xFamily.Select(g => g.UniqueId).ToList();
        await Assert.That(orderedIds).IsEquivalentTo(new[] { "a-1", "m-5", "z-9" });
    }

    [Test]
    public async Task NearestGrid_FindsClosestGridInFamily()
    {
        var registry = new GridRegistry(new[]
        {
            GridX(0, "X1"),
            GridX(1000, "X2"),
            GridX(2000, "X3"),
        });
        var finder = new NearestGridFinder(new ToleranceSettings(
            angularDegrees: 1,
            intersectionMm: 50,
            groupingMm: 0,
            supportSearchMm: 0,
            snapMm: 0));

        var hit = finder.Find(registry, AxisFamily.X, new Point2(1042, 123));
        await Assert.That(hit.IsHit).IsTrue();
        await Assert.That(hit.Grid.UniqueId).IsEqualTo("X2");
        await Assert.That(hit.SignedOffsetMm).IsEqualTo(42);
        await Assert.That(hit.DistanceMm).IsEqualTo(42);
    }

    [Test]
    public async Task NearestGrid_OutsideTolerance_ReturnsNoHit()
    {
        var registry = new GridRegistry(new[] { GridX(0, "X1"), GridX(1000, "X2") });
        var finder = new NearestGridFinder(new ToleranceSettings(
            angularDegrees: 1,
            intersectionMm: 1,
            groupingMm: 0,
            supportSearchMm: 0,
            snapMm: 0));
        // Way off the 1000 mm line.
        var hit = finder.Find(registry, AxisFamily.X, new Point2(7000, 0));
        await Assert.That(hit.IsHit).IsFalse();
    }

    [Test]
    public async Task NearestGrid_EmptyFamily_ReturnsNoHit()
    {
        var registry = new GridRegistry(new[] { GridX(0, "X1") });
        var finder = new NearestGridFinder(ToleranceSettings.Default);
        var hit = finder.Find(registry, AxisFamily.Y, new Point2(0, 0));
        await Assert.That(hit.IsHit).IsFalse();
    }
}
