using RegisterGeometry;
using TUnit.Core;
using TUnit.Assertions;

namespace RegisterGeometry.Tests;

public class DirectionClassifierTests
{
    [Test]
    public async Task Classify_OnXAxis_IsX()
    {
        var c = new DirectionClassifier(ToleranceSettings.Default);
        await Assert.That(c.Classify(Vector2.UnitX)).IsEqualTo(AxisFamily.X);
        await Assert.That(c.Classify(-Vector2.UnitX)).IsEqualTo(AxisFamily.X);
    }

    [Test]
    public async Task Classify_OnYAxis_IsY()
    {
        var c = new DirectionClassifier(ToleranceSettings.Default);
        await Assert.That(c.Classify(Vector2.UnitY)).IsEqualTo(AxisFamily.Y);
        await Assert.That(c.Classify(-Vector2.UnitY)).IsEqualTo(AxisFamily.Y);
    }

    [Test]
    public async Task Classify_DiagonalBeyondTolerance_IsSkew()
    {
        var c = new DirectionClassifier(ToleranceSettings.Default);
        // 45° to both axes — outside 1° tolerance.
        var dir = new Vector2(1, 1).Normalized();
        await Assert.That(c.Classify(dir)).IsEqualTo(AxisFamily.Skew);
    }

    [Test]
    public async Task Classify_CloseToXWithinTolerance_IsX()
    {
        // 0.5° tilt within 1° tolerance.
        var angle = 0.5 * Math.PI / 180;
        var dir = new Vector2(Math.Cos(angle), Math.Sin(angle));
        var c = new DirectionClassifier(ToleranceSettings.Default);
        await Assert.That(c.Classify(dir)).IsEqualTo(AxisFamily.X);
    }

    [Test]
    public async Task Classify_ZeroVector_IsUnknown()
    {
        var c = new DirectionClassifier(ToleranceSettings.Default);
        await Assert.That(c.Classify(Vector2.Zero)).IsEqualTo(AxisFamily.Unknown);
    }

    [Test]
    public async Task AcuteAngle_TreatsOppositeDirectionsAsParallel()
    {
        var a = Vector2.UnitX;
        var b = -Vector2.UnitX;
        await Assert.That(DirectionClassifier.AcuteAngle(a, b)).IsEqualTo(0).Within(1e-9);
    }

    [Test]
    public async Task CanonicalDirection_FamilyX_AlignsWithQuery()
    {
        var pos = DirectionClassifier.CanonicalDirection(AxisFamily.X, Vector2.UnitX);
        var neg = DirectionClassifier.CanonicalDirection(AxisFamily.X, -Vector2.UnitX);
        await Assert.That(pos).IsEqualTo(Vector2.UnitX);
        await Assert.That(neg).IsEqualTo(-Vector2.UnitX);
    }
}
