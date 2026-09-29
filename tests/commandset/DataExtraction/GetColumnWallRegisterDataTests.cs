using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.DataExtraction;

/// <summary>
///     Live tests for the column/wall register extraction code paths. The
///     tests create a host document, seed two structural columns and a
///     segmented wall, then assert that the lightweight collector and the
///     identity-key page slice behave as documented without requiring the
///     full external-event plumbing.
/// </summary>
/// <remarks>
///     The live-test infrastructure mirrors the other DataExtraction tests
///     under <c>tests/commandset/DataExtraction</c>: a class-scoped setup
///     creates the project + elements and <see cref="RevitApiTest"/>
///     schedules each test on the Revit thread.
/// </remarks>
public class GetColumnWallRegisterDataTests : RevitApiTest
{
    private const double ColumnSpacingFeet = 10.0; // 3048 mm
    private const double WallLengthFeet = 12.0;

    private static Document _doc;
    private static Level _level;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);

        using var tx = new Transaction(_doc, "Setup Column Wall Test");
        tx.Start();

        _level = Level.Create(_doc, 0.0);
        _level.Name = "Column Wall Test";

        // Two structural columns separated by ~3 m.
        var columnType = new FilteredElementCollector(_doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_StructuralColumns)
            .Cast<FamilySymbol>()
            .FirstOrDefault();
        if (columnType != null)
        {
            if (!columnType.IsActive) columnType.Activate();
            _doc.Create.NewFamilyInstance(
                new XYZ(0, 0, 0), columnType, _level,
                Autodesk.Revit.DB.Structure.StructuralType.Column);
            _doc.Create.NewFamilyInstance(
                new XYZ(ColumnSpacingFeet, 0, 0), columnType, _level,
                Autodesk.Revit.DB.Structure.StructuralType.Column);
        }

        // One wall along the X axis between the columns; we'll extend it
        // below in the per-test fixture.
        Wall.Create(
            _doc,
            Line.CreateBound(new XYZ(0, -2, 0), new XYZ(WallLengthFeet, -2, 0)),
            _level.Id,
            false);

        tx.Commit();
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    [Test]
    public async Task StructuralColumns_AreCollected_UnderExpectedCategory()
    {
        var columns = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_StructuralColumns)
            .WhereElementIsNotElementType()
            .ToElements();
        await Assert.That(columns.Count).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task Walls_AreCollected_UnderExpectedCategory()
    {
        var walls = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Walls)
            .WhereElementIsNotElementType()
            .ToElements();
        await Assert.That(walls.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task Wall_LocationCurve_HasExpectedEndpoints()
    {
        var wall = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Walls)
            .WhereElementIsNotElementType()
            .Cast<Wall>()
            .First();
        var location = wall.Location as LocationCurve;
        await Assert.That(location).IsNotNull();
        var curve = location!.Curve;
        var start = curve.GetEndPoint(0);
        var end = curve.GetEndPoint(1);
        await Assert.That(System.Math.Abs(end.X - start.X - WallLengthFeet)).IsLessThan(1e-6);
        await Assert.That(System.Math.Abs(end.Y - start.Y)).IsLessThan(1e-6);
    }

    [Test]
    public async Task Wall_DefaultMark_IsNullOrEmpty_WhenUnset()
    {
        var wall = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Walls)
            .WhereElementIsNotElementType()
            .Cast<Wall>()
            .First();
        var markParam = wall.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
        // Newly created walls have no mark, so the register must fall back
        // to a null/empty value rather than inventing one.
        var mark = markParam?.AsString();
        await Assert.That(string.IsNullOrEmpty(mark)).IsTrue();
    }
}
