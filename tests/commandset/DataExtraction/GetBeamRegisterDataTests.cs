using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.DataExtraction;

/// <summary>
///     Live tests for the beam register extraction. The tests create a host
///     document, seed two structural columns and a structural framing beam
///     between them, then assert that the beam is exposed under
///     <see cref="BuiltInCategory.OST_StructuralFraming"/> and that the
///     spatial relationship between the columns and the beam matches the
///     face-to-face span. Geometry helpers are inlined here so the test
///     remains self-contained without referencing the commandset assembly.
/// </summary>
public class GetBeamRegisterDataTests : RevitApiTest
{
    private const double ColumnSpacingFeet = 10.0; // 3048 mm
    private const double BeamOffsetFeet = 0.0;

    private static Document _doc;
    private static Level _level;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);
        using var tx = new Transaction(_doc, "Setup Beam Test");
        tx.Start();
        _level = Level.Create(_doc, 0.0);
        _level.Name = "Beam Test Level";
        tx.Commit();
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    [Test]
    public async Task SeedBeam_BetweenColumns_BeamIsCollected()
    {
        var (beam, columns) = SeedBeamBetweenColumns();
        await Assert.That(beam).IsNotNull();
        await Assert.That(columns.Count).IsEqualTo(2);
        var beams = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_StructuralFraming)
            .WhereElementIsNotElementType()
            .ToElements();
        await Assert.That(beams.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task SeedBeam_LocationCurve_HasExpectedEndpoints()
    {
        var (beam, _) = SeedBeamBetweenColumns();
        var location = beam!.Location as LocationCurve;
        await Assert.That(location).IsNotNull();
        var curve = location!.Curve;
        var start = curve.GetEndPoint(0);
        var end = curve.GetEndPoint(1);
        await Assert.That(Math.Abs(end.X - start.X - ColumnSpacingFeet)).IsLessThan(1e-6);
        await Assert.That(Math.Abs(end.Y - start.Y)).IsLessThan(1e-6);
        await Assert.That(Math.Abs(curve.Length - ColumnSpacingFeet)).IsLessThan(1e-6);
    }

    [Test]
    public async Task SeedBeam_ColumnsBoundingBoxes_OverlapBeamEndpoints()
    {
        var (_, columns) = SeedBeamBetweenColumns();
        await Assert.That(columns.Count).IsEqualTo(2);

        foreach (var column in columns)
        {
            var bbox = column.get_BoundingBox(null);
            await Assert.That(bbox).IsNotNull();
            // Column extends from x=0 (or x=spacing) and the beam endpoint
            // sits on the same X; the column's X bounds must contain the
            // beam endpoint.
            double columnMinX = Math.Min(bbox.Min.X, bbox.Max.X);
            double columnMaxX = Math.Max(bbox.Min.X, bbox.Max.X);
            await Assert.That(columnMinX).IsLessThanOrEqualTo(0.001);
            await Assert.That(columnMaxX).IsGreaterThanOrEqualTo(ColumnSpacingFeet - 0.001);
        }
    }

    private static (FamilyInstance beam, IList<FamilyInstance> columns) SeedBeamBetweenColumns()
    {
        var columns = new List<FamilyInstance>();
        using var tx = new Transaction(_doc, "Seed beam between columns");
        tx.Start();

        var columnType = new FilteredElementCollector(_doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_StructuralColumns)
            .Cast<FamilySymbol>()
            .FirstOrDefault();
        if (columnType != null)
        {
            if (!columnType.IsActive) columnType.Activate();
            columns.Add(_doc.Create.NewFamilyInstance(
                new XYZ(0, 0, 0), columnType, _level, StructuralType.Column));
            columns.Add(_doc.Create.NewFamilyInstance(
                new XYZ(ColumnSpacingFeet, 0, 0), columnType, _level, StructuralType.Column));
        }

        FamilyInstance beam = null;
        var beamType = new FilteredElementCollector(_doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_StructuralFraming)
            .Cast<FamilySymbol>()
            .FirstOrDefault();
        if (beamType != null)
        {
            if (!beamType.IsActive) beamType.Activate();
            beam = _doc.Create.NewFamilyInstance(
                Line.CreateBound(
                    new XYZ(0, 0, BeamOffsetFeet),
                    new XYZ(ColumnSpacingFeet, 0, BeamOffsetFeet)),
                beamType,
                _level,
                StructuralType.Beam);
        }

        tx.Commit();
        return (beam, columns);
    }
}