using System.Linq;
using RegisterGeometry;
using TUnit.Core;
using TUnit.Assertions;

namespace RegisterGeometry.Tests;

public class WallTopologyAndGroupingTests
{
    [Test]
    public async Task Topology_SingleLeg_IsPlanar()
    {
        var leg = new WallLeg(
            "leg-1",
            "W1",
            new Segment(new Point2(0, 0), new Point2(1000, 0)),
            thicknessMm: 200);
        var topology = WallTopology.FromLegs(new[] { leg }, toleranceMm: 50);
        await Assert.That(topology.Shape).IsEqualTo(WallShape.Planar);
        await Assert.That(topology.LegCount).IsEqualTo(1);
        await Assert.That(topology.FreeEndCount).IsEqualTo(2);
    }

    [Test]
    public async Task Topology_TwoLegCorner_IsLShape()
    {
        var a = new WallLeg("a", "W1", new Segment(new Point2(0, 0), new Point2(1000, 0)), 200);
        var b = new WallLeg("b", "W1", new Segment(new Point2(1000, 0), new Point2(1000, 500)), 200);
        var topology = WallTopology.FromLegs(new[] { a, b }, toleranceMm: 50);
        await Assert.That(topology.Shape).IsEqualTo(WallShape.L);
        await Assert.That(topology.CornerCount).IsEqualTo(1);
        await Assert.That(topology.FreeEndCount).IsEqualTo(2);
    }

    [Test]
    public async Task Topology_ClosedBox_IsBox()
    {
        var legs = new[]
        {
            new WallLeg("a", "W1", new Segment(new Point2(0, 0), new Point2(1000, 0)), 200),
            new WallLeg("b", "W1", new Segment(new Point2(1000, 0), new Point2(1000, 500)), 200),
            new WallLeg("c", "W1", new Segment(new Point2(1000, 500), new Point2(0, 500)), 200),
            new WallLeg("d", "W1", new Segment(new Point2(0, 500), new Point2(0, 0)), 200),
        };
        var topology = WallTopology.FromLegs(legs, toleranceMm: 50);
        await Assert.That(topology.Shape).IsEqualTo(WallShape.Box);
        await Assert.That(topology.IsClosedLoop).IsTrue();
    }

    [Test]
    public async Task Topology_UTShape_IsUorT()
    {
        var legs = new[]
        {
            new WallLeg("a", "W1", new Segment(new Point2(0, 0), new Point2(1000, 0)), 200),
            new WallLeg("b", "W1", new Segment(new Point2(0, 0), new Point2(0, 500)), 200),
            new WallLeg("c", "W1", new Segment(new Point2(1000, 0), new Point2(1000, 500)), 200),
        };
        var topology = WallTopology.FromLegs(legs, toleranceMm: 50);
        await Assert.That(topology.Shape).IsEqualTo(WallShape.UorT);
    }

    [Test]
    public async Task Grouping_ExplicitKey_MergesLegsWithFullConfidence()
    {
        var legs = new[]
        {
            new WallLeg("a", "W1", new Segment(new Point2(0, 0), new Point2(1000, 0)), 200),
            new WallLeg("b", "W1", new Segment(new Point2(1000, 0), new Point2(1000, 500)), 200),
        };
        var builder = new WallGroupBuilder(ToleranceSettings.Default);
        var groups = builder.Build(legs, explicitGroupKey: l => "model-group-1");
        await Assert.That(groups.Count).IsEqualTo(1);
        await Assert.That(groups[0].Legs.Count).IsEqualTo(2);
        await Assert.That(groups[0].GroupingConfidence).IsEqualTo(1.0);
        await Assert.That(groups[0].GroupingMethod).IsEqualTo("explicit");
        await Assert.That(groups[0].Shape).IsEqualTo(WallShape.L);
    }

    [Test]
    public async Task Grouping_MarkConnectivity_MergesLegsSharingMarkWhenConnected()
    {
        var legs = new[]
        {
            new WallLeg("a", "CW1", new Segment(new Point2(0, 0), new Point2(1000, 0)), 200),
            new WallLeg("b", "CW1", new Segment(new Point2(1000, 0), new Point2(1000, 500)), 200),
            new WallLeg("c", "CW2", new Segment(new Point2(5000, 5000), new Point2(6000, 5000)), 200),
        };
        var builder = new WallGroupBuilder(ToleranceSettings.Default);
        var groups = builder.Build(legs);
        // CW1 forms a 2-leg L-shape; CW2 is an isolated leg.
        await Assert.That(groups.Count).IsEqualTo(2);
        var cw1 = groups.First(g => g.Legs[0].Mark == "CW1");
        await Assert.That(cw1.Legs.Count).IsEqualTo(2);
        await Assert.That(cw1.GroupingConfidence).IsEqualTo(0.8);
        await Assert.That(cw1.GroupingMethod).IsEqualTo("mark_connectivity");
        await Assert.That(cw1.Shape).IsEqualTo(WallShape.L);
    }

    [Test]
    public async Task Grouping_NoMarkOrKey_EmitsSingleGroupsAtZeroConfidence()
    {
        var legs = new[]
        {
            new WallLeg("a", "", new Segment(new Point2(0, 0), new Point2(1000, 0)), 200),
        };
        var builder = new WallGroupBuilder(ToleranceSettings.Default);
        var groups = builder.Build(legs);
        await Assert.That(groups.Count).IsEqualTo(1);
        await Assert.That(groups[0].GroupingConfidence).IsEqualTo(0.0);
        await Assert.That(groups[0].GroupingMethod).IsEqualTo("ungrouped");
    }

    [Test]
    public async Task Grouping_LegWithIsolatedMark_IsEmittedAsSingleGroup()
    {
        var legs = new[]
        {
            new WallLeg("a", "WS", new Segment(new Point2(0, 0), new Point2(1000, 0)), 200),
        };
        var builder = new WallGroupBuilder(ToleranceSettings.Default);
        var groups = builder.Build(legs);
        await Assert.That(groups.Count).IsEqualTo(1);
        await Assert.That(groups[0].Legs.Count).IsEqualTo(1);
        await Assert.That(groups[0].GroupingMethod).IsEqualTo("mark_isolated");
        await Assert.That(groups[0].GroupingConfidence).IsEqualTo(0.5);
    }

    [Test]
    public async Task Grouping_RespectsToleranceForConnectivity()
    {
        // Two legs that share a mark but their endpoints are far apart.
        var legs = new[]
        {
            new WallLeg("a", "W1", new Segment(new Point2(0, 0), new Point2(1000, 0)), 200),
            new WallLeg("b", "W1", new Segment(new Point2(5000, 5000), new Point2(6000, 5000)), 200),
        };
        var builder = new WallGroupBuilder(new ToleranceSettings(
            angularDegrees: 1, intersectionMm: 0,
            groupingMm: 10, supportSearchMm: 0, snapMm: 0));
        var groups = builder.Build(legs);
        // Each leg forms its own group.
        await Assert.That(groups.Count).IsEqualTo(2);
        await Assert.That(groups.All(g => g.Legs.Count == 1)).IsTrue();
    }
}
