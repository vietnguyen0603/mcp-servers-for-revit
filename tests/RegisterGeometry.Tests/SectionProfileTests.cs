using RegisterGeometry;
using TUnit.Core;
using TUnit.Assertions;

namespace RegisterGeometry.Tests;

public class SectionProfileTests
{
    private static Dictionary<string, double> V(params (string Key, double Value)[] values) =>
        values.ToDictionary(x => x.Key, x => x.Value);

    [Test]
    public async Task I_AreaMatchesPlateSumAndPlanesAreUnique()
    {
        var p = SectionProfile.Build(SectionProfile.I, V(("d", 600), ("bf", 300), ("tw", 10), ("tf", 16)));
        await Assert.That(p.Loops.Count).IsEqualTo(1);
        await Assert.That(p.Loops[0].Count).IsEqualTo(12);
        await Assert.That(Math.Abs(p.Area - (2 * 300 * 16 + (600 - 32) * 10))).IsLessThan(1e-6);
        await Assert.That(p.Planes.Count).IsEqualTo(8);
        await Assert.That(p.Labels.Count).IsEqualTo(5);
        await Assert.That(p.EqualConstraints.Count).IsEqualTo(3);
        await Assert.That(p.TopPlaneId).IsEqualTo("d:V:+");
        await Assert.That(p.MaxV).IsEqualTo(300);
        await Assert.That(p.MaxU).IsEqualTo(150);
    }

    [Test]
    public async Task Cruciform_AreaMatchesPlatesAndEveryEdgeHasAPlane()
    {
        var values = V(("d", 1000), ("bf", 400), ("tw", 40), ("tf", 40), ("d2", 1000), ("bf2", 400), ("tw2", 40), ("tf2", 40));
        var p = SectionProfile.Build(SectionProfile.Cruciform, values);
        var expected = 2 * 400 * 40 + (1000 - 80) * 40 + 2 * 400 * 40 + (1000 - 80 - 40) * 40;
        await Assert.That(Math.Abs(p.Area - expected)).IsLessThan(1e-6);
        await Assert.That(p.Loops[0].Count).IsEqualTo(28);
        await AssertEveryEdgeOnAPlane(p);
    }

    [Test]
    public async Task Cruciform_SecondIDefaultsToTheFirstAndTouchingFlangesAreRejected()
    {
        var error = SectionProfile.Validate(SectionProfile.Cruciform, V(("d", 1000), ("bf", 1000), ("tw", 40), ("tf", 40)));
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("touch");
        var ok = SectionProfile.Validate(SectionProfile.Cruciform, V(("d", 1000), ("bf", 500), ("tw", 40), ("tf", 40)));
        await Assert.That(ok).IsNull();
    }

    [Test]
    public async Task Box_WithAndWithoutOverhang()
    {
        var closed = SectionProfile.Build(SectionProfile.Box, V(("B", 400), ("H", 800), ("tw", 12), ("tf", 20)));
        await Assert.That(closed.Loops.Count).IsEqualTo(2);
        await Assert.That(Math.Abs(closed.Area - (2 * 400 * 20 + 2 * (800 - 40) * 12))).IsLessThan(1e-6);
        await Assert.That(closed.Planes.Any(x => x.Key == "overhang")).IsFalse();
        await AssertEveryEdgeOnAPlane(closed);

        var open = SectionProfile.Build(SectionProfile.Box, V(("B", 500), ("H", 800), ("tw", 12), ("tf", 20), ("overhang", 50)));
        await Assert.That(open.Loops[0].Count).IsEqualTo(12);
        await Assert.That(Math.Abs(open.Area - (2 * 500 * 20 + 2 * (800 - 40) * 12))).IsLessThan(1e-6);
        await Assert.That(SectionProfile.Keys(SectionProfile.Box, V(("overhang", 50)))).Contains("overhang");
        await AssertEveryEdgeOnAPlane(open);
    }

    [Test]
    public async Task Pipe_AndCircle()
    {
        var pipe = SectionProfile.Build(SectionProfile.Pipe, V(("D", 500), ("t", 10)));
        await Assert.That(pipe.CircleDiameters.Count).IsEqualTo(2);
        await Assert.That(Math.Abs(pipe.Area - Math.PI * (500 * 500 - 480 * 480) / 4)).IsLessThan(1e-6);
        await Assert.That(SectionProfile.Validate(SectionProfile.Pipe, V(("D", 500), ("t", 250)))).IsNotNull();
        var circle = SectionProfile.Build("circular", V(("D", 800)));
        await Assert.That(circle.MaxU).IsEqualTo(400);
    }

    [Test]
    public async Task Validation_RejectsBadInput()
    {
        await Assert.That(SectionProfile.Validate(SectionProfile.I, V(("d", 600), ("bf", 300), ("tw", 10)))).Contains("tf");
        await Assert.That(SectionProfile.Validate(SectionProfile.I, V(("d", 600), ("bf", 300), ("tw", 300), ("tf", 16)))).IsNotNull();
        await Assert.That(SectionProfile.Validate(SectionProfile.I, V(("d", 30), ("bf", 300), ("tw", 10), ("tf", 16)))).IsNotNull();
        await Assert.That(SectionProfile.Validate(SectionProfile.Box, V(("B", 100), ("H", 800), ("tw", 30), ("tf", 20), ("overhang", 25)))).IsNotNull();
        await Assert.That(SectionProfile.Validate(SectionProfile.Polygon, V(), new[] { new Point2(0, 0), new Point2(1, 1) })).IsNotNull();
        await Assert.That(SectionProfile.Validate("T", V())).IsNotNull();
    }

    [Test]
    public async Task Normalize_AcceptsAliases()
    {
        await Assert.That(SectionProfile.Normalize("rectangular")).IsEqualTo("rect");
        await Assert.That(SectionProfile.Normalize("i")).IsEqualTo("I");
        await Assert.That(SectionProfile.Normalize("BOX")).IsEqualTo("box");
        await Assert.That(SectionProfile.Normalize("zed")).IsNull();
    }

    [Test]
    public async Task AreaFormula_UsesParameterNames()
    {
        var formula = SectionProfile.AreaFormula(SectionProfile.I, k => k == "d" ? "H" : k);
        await Assert.That(formula).IsEqualTo("2 * bf * tf + (H - 2 * tf) * tw");
        await Assert.That(SectionProfile.AreaFormula(SectionProfile.Polygon, k => k)).IsNull();
    }

    /// <summary>Every straight outline edge must lie on a reference plane, or its face cannot be locked.</summary>
    private static async Task AssertEveryEdgeOnAPlane(SectionProfile p)
    {
        foreach (var loop in p.Loops)
            for (var i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                var plane = Math.Abs(a.X - b.X) < 1e-9
                    ? p.FindPlane(SectionAxis.U, a.X, 1e-6)
                    : p.FindPlane(SectionAxis.V, a.Y, 1e-6);
                await Assert.That(plane).IsNotNull();
            }
    }
}
