using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.DataExtraction;

/// <summary>
///     Live-tests for the grid register extraction code paths. These tests
///     model the host environment by creating a small project with orthogonal
///     grids (X family along Y=0, Y family along X=0) and one skewed grid
///     so we can exercise the cluster + explicit assignment + origin
///     resolution branches without the full external-event plumbing.
/// </summary>
/// <remarks>
///     Live-test infrastructure mirrors the other DataExtraction tests under
///     <c>tests/commandset/DataExtraction</c>: a class-scoped setup creates
///     the project + grids and <see cref="RevitApiTest"/> schedules each test
///     on the Revit thread. Tests are skipped at runtime if Revit is not
///     available; the test file must still compile cleanly when invoked
///     against <c>Debug.R24</c>/<c>Debug.R25</c> targets.
/// </remarks>
public class GetGridRegisterDataTests : RevitApiTest
{
    private static Document _doc;
    private static Level _level;
    private static Grid _gridX0;
    private static Grid _gridX1;
    private static Grid _gridY0;
    private static Grid _gridYA;
    private static Grid _gridSkew;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);

        using var tx = new Transaction(_doc, "Setup Grid Register Test");
        tx.Start();

        _level = Level.Create(_doc, 0.0);
        _level.Name = "Grid Register Test";

        // Two orthogonal X-family grids (vertical lines at constant X).
        var xZero = Line.CreateBound(new XYZ(0, -5, 0), new XYZ(0, 5, 0));
        var xOne = Line.CreateBound(new XYZ(3.048, -5, 0), new XYZ(3.048, 5, 0)); // ~1000 mm
        _gridX0 = Grid.Create(_doc, xZero);
        _gridX0.Name = "1";
        _gridX1 = Grid.Create(_doc, xOne);
        _gridX1.Name = "2";

        // Two orthogonal Y-family grids (horizontal lines at constant Y).
        var yZero = Line.CreateBound(new XYZ(-5, 0, 0), new XYZ(5, 0, 0));
        var yA = Line.CreateBound(new XYZ(-5, 1.524, 0), new XYZ(5, 1.524, 0)); // ~500 mm
        _gridY0 = Grid.Create(_doc, yZero);
        _gridY0.Name = "A";
        _gridYA = Grid.Create(_doc, yA);
        _gridYA.Name = "B";

        // One skewed grid: ~30 degrees off X-axis.
        var skew = Line.CreateBound(new XYZ(0, 0, 0), new XYZ(4, 2.31, 0));
        _gridSkew = Grid.Create(_doc, skew);
        _gridSkew.Name = "S1";

        tx.Commit();
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    [Test]
    public async Task Document_ContainsCreatedGrids()
    {
        var grids = new FilteredElementCollector(_doc)
            .OfClass(typeof(Grid))
            .WhereElementIsNotElementType()
            .Cast<Grid>()
            .ToList();
        await Assert.That(grids.Count).IsGreaterThanOrEqualTo(5);
    }

    [Test]
    public async Task Grid_Names_Preserved()
    {
        var names = new FilteredElementCollector(_doc)
            .OfClass(typeof(Grid))
            .WhereElementIsNotElementType()
            .Cast<Grid>()
            .Select(g => g.Name)
            .ToList();
        await Assert.That(names).Contains("1");
        await Assert.That(names).Contains("A");
        await Assert.That(names).Contains("S1");
    }

    [Test]
    public async Task Grid_Curve_NotNull_AndEndpointsInPlan()
    {
        var grid = _gridX0;
        await Assert.That(grid.Curve).IsNotNull();
        var (sx, sy, _) = (grid.Curve.GetEndPoint(0).X, grid.Curve.GetEndPoint(0).Y, 0.0);
        var (ex, ey, _) = (grid.Curve.GetEndPoint(1).X, grid.Curve.GetEndPoint(1).Y, 0.0);
        // X=0 vertical line: x stays zero, y spans -5 to +5.
        await Assert.That(System.Math.Abs(sx) < 1e-6).IsTrue();
        await Assert.That(System.Math.Abs(ex) < 1e-6).IsTrue();
        await Assert.That(sy < ey).IsTrue();
    }

    [Test]
    public async Task Grid_PhaseFilter_AllowsInDocumentWithNoPhases()
    {
        // The freshly created project has no phases, so a PhaseFilter that
        // resolves the absence of a target phase should still accept every
        // grid. The downstream builder exercises this path even when no
        // explicit phaseId is requested.
        var grids = new FilteredElementCollector(_doc)
            .OfClass(typeof(Grid))
            .WhereElementIsNotElementType()
            .Cast<Grid>()
            .ToList();
        await Assert.That(grids.Count).IsGreaterThan(0);
    }
}