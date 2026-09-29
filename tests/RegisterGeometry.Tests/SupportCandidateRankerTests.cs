using System.Linq;
using RegisterGeometry;
using TUnit.Core;
using TUnit.Assertions;

namespace RegisterGeometry.Tests;

public class SupportCandidateRankerTests
{
    private static SupportCandidate MakeCandidate(
        string id,
        SupportCategory category,
        Point2 contact,
        Point2 min,
        Point2 max)
    {
        var bounds = new Bounds(min.X, min.Y, max.X, max.Y);
        return new SupportCandidate(id, category, bounds, contact, contact.DistanceTo(new Point2(0, 0)));
    }

    [Test]
    public async Task Select_PicksNearestByDistance()
    {
        var near = MakeCandidate("near", SupportCategory.Wall,
            contact: new Point2(0, 0),
            min: new Point2(-100, -100), max: new Point2(100, 100));
        var far = MakeCandidate("far", SupportCategory.Column,
            contact: new Point2(50, 50),
            min: new Point2(0, 0), max: new Point2(200, 200));
        var ranker = new SupportCandidateRanker(
            new ToleranceSettings(1, 0, 0, 1000, 0));
        var selection = ranker.Select(new[] { far, near }, new Point2(0, 0));
        await Assert.That(selection.Selected).IsNotNull();
        await Assert.That(selection.Selected!.UniqueId).IsEqualTo("near");
    }

    [Test]
    public async Task Select_BreaksTiesByCategoryPrecedence()
    {
        var beam = MakeCandidate("beam", SupportCategory.Beam,
            contact: new Point2(0, 0),
            min: new Point2(-100, -100), max: new Point2(100, 100));
        var wall = MakeCandidate("wall", SupportCategory.Wall,
            contact: new Point2(0, 0),
            min: new Point2(-100, -100), max: new Point2(100, 100));
        // Equal distance, equal bounds, equal contact. Wall should win.
        var ranker = new SupportCandidateRanker(
            new ToleranceSettings(1, 0, 0, 1000, 0));
        var selection = ranker.Select(new[] { beam, wall }, new Point2(0, 0));
        await Assert.That(selection.Selected!.Category).IsEqualTo(SupportCategory.Wall);
    }

    [Test]
    public async Task Select_BreaksRemainingTiesByUniqueId()
    {
        var a = MakeCandidate("A", SupportCategory.Wall,
            contact: new Point2(0, 0),
            min: new Point2(-100, -100), max: new Point2(100, 100));
        var z = MakeCandidate("Z", SupportCategory.Wall,
            contact: new Point2(0, 0),
            min: new Point2(-100, -100), max: new Point2(100, 100));
        var ranker = new SupportCandidateRanker(
            new ToleranceSettings(1, 0, 0, 1000, 0));
        var selection = ranker.Select(new[] { z, a }, new Point2(0, 0));
        await Assert.That(selection.Selected!.UniqueId).IsEqualTo("A");
    }

    [Test]
    public async Task Select_RejectsCandidatesOutsideSupportSearch()
    {
        var candidate = MakeCandidate("c", SupportCategory.Wall,
            contact: new Point2(2000, 0),
            min: new Point2(1900, -50), max: new Point2(2100, 50));
        var ranker = new SupportCandidateRanker(
            new ToleranceSettings(1, 0, 0, 100, 0));
        var selection = ranker.Select(new[] { candidate }, new Point2(0, 0));
        await Assert.That(selection.Selected).IsNull();
    }

    [Test]
    public async Task Prune_RespectsSupportSearchMm()
    {
        var near = MakeCandidate("near", SupportCategory.Wall,
            contact: new Point2(50, 0),
            min: new Point2(-50, -50), max: new Point2(150, 50));
        var far = MakeCandidate("far", SupportCategory.Wall,
            contact: new Point2(5000, 0),
            min: new Point2(4950, -50), max: new Point2(5050, 50));
        var ranker = new SupportCandidateRanker(
            new ToleranceSettings(1, 0, 0, 100, 0));
        var pruned = ranker.Prune(new[] { near, far }, new Point2(0, 0));
        await Assert.That(pruned.Count).IsEqualTo(1);
        await Assert.That(pruned[0].UniqueId).IsEqualTo("near");
    }

    [Test]
    public async Task ContactStation_OnAxis_ReturnsParameterAlongBeam()
    {
        var station = SupportCandidateRanker.ComputeContactStation(
            beamStart: new Point2(0, 0),
            beamEnd: new Point2(1000, 0),
            beamEndSample: new Point2(1000, 0),
            contactPoint: new Point2(250, 0));
        await Assert.That(station).IsEqualTo(250);
    }

    [Test]
    public async Task ContactStation_SnapsToBeamStartAndEnd()
    {
        var atStart = SupportCandidateRanker.ComputeContactStation(
            new Point2(0, 0), new Point2(1000, 0),
            new Point2(1000, 0),
            new Point2(1e-6, 0));
        var atEnd = SupportCandidateRanker.ComputeContactStation(
            new Point2(0, 0), new Point2(1000, 0),
            new Point2(1000, 0),
            new Point2(999.999, 0));
        await Assert.That(atStart).IsEqualTo(0);
        await Assert.That(atEnd).IsEqualTo(1000);
    }

    [Test]
    public async Task ContactStation_NearMiss_ReturnsNull()
    {
        var station = SupportCandidateRanker.ComputeContactStation(
            new Point2(0, 0), new Point2(1000, 0),
            new Point2(1000, 0),
            new Point2(2000, 0));
        await Assert.That(station).IsNull();
    }
}
