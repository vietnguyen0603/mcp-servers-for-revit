using System.Linq;
using RegisterGeometry;
using TUnit.Core;
using TUnit.Assertions;

namespace RegisterGeometry.Tests;

public class DirectionClusterTests
{
    [Test]
    public async Task Cluster_MixedAxes_SplitsIntoTwoClusters()
    {
        var directions = new[]
        {
            Vector2.UnitX,
            new Vector2(10, 0),
            -Vector2.UnitX,
            Vector2.UnitY,
            new Vector2(0, 5),
            -Vector2.UnitY,
        };
        var clusters = DirectionClusterer.Cluster(directions, ToleranceSettings.Default);
        await Assert.That(clusters.Count).IsEqualTo(2);
        var families = clusters.Select(c => c.Family).OrderBy(f => f).ToList();
        await Assert.That(families).IsEquivalentTo(new[] { AxisFamily.X, AxisFamily.Y });
        foreach (var cluster in clusters)
        {
            await Assert.That(cluster.Count).IsEqualTo(3);
        }
    }

    [Test]
    public async Task Cluster_SkewDirection_FormsSkewCluster()
    {
        var directions = new[] { new Vector2(1, 1).Normalized() };
        var clusters = DirectionClusterer.Cluster(directions, ToleranceSettings.Default);
        await Assert.That(clusters.Count).IsEqualTo(1);
        await Assert.That(clusters[0].Family).IsEqualTo(AxisFamily.Skew);
        await Assert.That(clusters[0].Members[0]).IsEqualTo(0);
    }

    [Test]
    public async Task Cluster_DropsZeroDirections()
    {
        var directions = new[]
        {
            Vector2.Zero,
            Vector2.UnitX,
        };
        var clusters = DirectionClusterer.Cluster(directions, ToleranceSettings.Default);
        await Assert.That(clusters.Count).IsEqualTo(1);
        await Assert.That(clusters[0].Count).IsEqualTo(1);
    }

    [Test]
    public async Task Cluster_PreservesSourceIndices()
    {
        var directions = new[]
        {
            Vector2.UnitX,    // 0 -> X
            Vector2.UnitY,    // 1 -> Y
            new Vector2(10, 0), // 2 -> X (joins 0)
        };
        var clusters = DirectionClusterer.Cluster(directions, ToleranceSettings.Default);
        var xCluster = clusters.First(c => c.Family == AxisFamily.X);
        await Assert.That(xCluster.Members.OrderBy(i => i).ToList()).IsEquivalentTo(new[] { 0, 2 });
    }

    [Test]
    public async Task Cluster_UpdatesRepresentativeTowardsCentreOfMass()
    {
        // Two near-parallel vectors within 5° tolerance should cluster, and the
        // representative should update towards their normalised sum.
        var directions = new[]
        {
            new Vector2(1, 0),
            new Vector2(0.99, 0.05),
        };
        var clusters = DirectionClusterer.Cluster(directions, new ToleranceSettings(
            angularDegrees: 5,
            intersectionMm: 0,
            groupingMm: 0,
            supportSearchMm: 0,
            snapMm: 0));
        await Assert.That(clusters.Count).IsEqualTo(1);
        await Assert.That(clusters[0].Family).IsEqualTo(AxisFamily.X);
        // Seed was (1,0). Sum is (1, 0) + (0.99, 0.05)/|(0.99,0.05)| = approx (1.995, 0.05),
        // normalised => (1, 0.025). Check Y moves slightly positive.
        await Assert.That(clusters[0].Representative.Y).IsGreaterThan(0);
    }
}
