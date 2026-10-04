using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Datums
{
    /// <summary>
    ///     Creates named grids from straight lines ({name, start, end}) or arcs
    ///     ({name, center, radius, startAngleDeg, endAngleDeg}), all in millimetres.
    ///     Existing grids with the same name are skipped (default), reported as an
    ///     error, or deleted and recreated ("replace").
    /// </summary>
    public class CreateGridsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Create Grids";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "grids");
            var ifExists = (parameters.Value<string>("ifExists") ?? "skip").Trim().ToLowerInvariant();
            if (ifExists != "skip" && ifExists != "error" && ifExists != "replace")
                throw new ArgumentException("'ifExists' must be skip, error or replace.");

            GridType gridType = null;
            var gridTypeName = parameters.Value<string>("gridTypeName");
            if (!string.IsNullOrWhiteSpace(gridTypeName))
            {
                var types = new FilteredElementCollector(doc).OfClass(typeof(GridType)).Cast<GridType>().ToList();
                gridType = types.FirstOrDefault(t => string.Equals(t.Name, gridTypeName, StringComparison.OrdinalIgnoreCase))
                           ?? throw new ArgumentException(
                               $"Grid type '{gridTypeName}' not found. Available: {string.Join(", ", types.Select(t => t.Name))}.");
            }

            var results = DocumentationUtils.RunBatch(doc, "MCP: Create Grids", items,
                item => CreateGrid(doc, (JObject)item, ifExists, gridType));

            var created = results.Count(r => r.Value<string>("status") == "created" || r.Value<string>("status") == "replaced");
            var skipped = results.Count(r => r.Value<string>("status") == "skipped");
            return Ok($"Created {created}, skipped {skipped}, failed {results.Count(r => !r.Value<bool>("success"))} of {results.Count} grids.",
                DocumentationUtils.Summarize(results));
        }

        private static object CreateGrid(Document doc, JObject item, string ifExists, GridType gridType)
        {
            var name = item.Value<string>("name");
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("'name' is required.");

            var existing = FindGrid(doc, name);
            var replaced = false;
            if (existing != null)
            {
                if (ifExists == "skip")
                {
                    return new JObject
                    {
                        ["id"] = existing.Id.GetValue(),
                        ["name"] = existing.Name,
                        ["status"] = "skipped",
                        ["message"] = "A grid with this name already exists."
                    };
                }

                if (ifExists == "error")
                    throw new ArgumentException($"Grid '{name}' already exists (id {existing.Id.GetValue()}).");

                doc.Delete(existing.Id);
                replaced = true;
            }

            var curve = ReadCurve(item);
            var grid = curve is Arc arc ? Grid.Create(doc, arc) : Grid.Create(doc, (Line)curve);
            if (grid.Name != name)
                grid.Name = name;
            if (gridType != null && grid.GetTypeId() != gridType.Id)
                grid.ChangeTypeId(gridType.Id);

            var result = new JObject
            {
                ["id"] = grid.Id.GetValue(),
                ["name"] = grid.Name,
                ["status"] = replaced ? "replaced" : "created"
            };
            if (replaced)
                result["warning"] = "The old grid was deleted; dimensions and tags that referenced it are gone.";
            return result;
        }

        private static Grid FindGrid(Document doc, string name)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
                .FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.Ordinal));
        }

        private static Curve ReadCurve(JObject item)
        {
            if (item["center"] != null)
            {
                var center = DocumentationUtils.ReadPointMm(item["center"]);
                var radius = item.Value<double?>("radius") ?? throw new ArgumentException("Arc grids need 'radius' (mm).");
                var start = item.Value<double?>("startAngleDeg") ?? throw new ArgumentException("Arc grids need 'startAngleDeg'.");
                var end = item.Value<double?>("endAngleDeg") ?? throw new ArgumentException("Arc grids need 'endAngleDeg'.");
                if (radius <= 0)
                    throw new ArgumentException("'radius' must be positive.");
                if (end <= start || end - start >= 360)
                    throw new ArgumentException("endAngleDeg must be greater than startAngleDeg with a sweep below 360 degrees.");

                return Arc.Create(new XYZ(center.X, center.Y, 0), DocumentationUtils.MmToFeet(radius),
                    start * Math.PI / 180.0, end * Math.PI / 180.0, XYZ.BasisX, XYZ.BasisY);
            }

            var p0 = DocumentationUtils.ReadPointMm(item["start"]) ?? throw new ArgumentException("Line grids need 'start' and 'end'.");
            var p1 = DocumentationUtils.ReadPointMm(item["end"]) ?? throw new ArgumentException("Line grids need 'start' and 'end'.");
            p0 = new XYZ(p0.X, p0.Y, 0);
            p1 = new XYZ(p1.X, p1.Y, 0);
            if (p0.DistanceTo(p1) < DocumentationUtils.MmToFeet(1))
                throw new ArgumentException("Grid start and end are less than 1 mm apart.");
            return Line.CreateBound(p0, p1);
        }
    }
}
