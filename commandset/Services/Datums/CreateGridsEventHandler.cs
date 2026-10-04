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
    ///     error, or deleted and recreated ("replace"). Vertical extents are set
    ///     to span all levels (+/- 5000 mm) so the grids show on every plan;
    ///     updateExtents also fixes skipped (or, with no grids, all) grids.
    /// </summary>
    public class CreateGridsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Create Grids";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var updateExtents = parameters.Value<bool?>("updateExtents") == true;
            var extents = ReadVerticalExtents(doc, parameters["verticalExtents"]);

            if (!(parameters["grids"] is JArray gridArray && gridArray.Count > 0))
            {
                if (!updateExtents)
                    throw new ArgumentException("'grids' must be a non-empty array (or set updateExtents:true to fix all existing grids).");
                if (extents == null)
                    throw new ArgumentException("updateExtents needs verticalExtents ('allLevels' or {bottom, top}) and at least one level.");

                var ids = new JArray(new FilteredElementCollector(doc).OfClass(typeof(Grid)).Select(g => g.Id.GetValue()));
                if (ids.Count == 0)
                    return Fail("The document has no grids.");
                var fixedResults = DocumentationUtils.RunBatch(doc, "MCP: Update Grid Extents", ids, token =>
                {
                    var grid = (Grid)doc.GetElement(token.Value<long>().ToRevitElementId());
                    var result = new JObject { ["id"] = grid.Id.GetValue(), ["name"] = grid.Name, ["status"] = "extentsUpdated" };
                    ApplyExtents(grid, extents, result);
                    return result;
                });
                return Ok($"Updated vertical extents of {fixedResults.Count(r => r.Value<bool>("success"))} of {fixedResults.Count} grids " +
                          $"({DocumentationUtils.FeetToMm(extents.Item1)} to {DocumentationUtils.FeetToMm(extents.Item2)} mm).",
                    DocumentationUtils.Summarize(fixedResults));
            }

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
                item => CreateGrid(doc, (JObject)item, ifExists, gridType, extents, updateExtents));

            var created = results.Count(r => r.Value<string>("status") == "created" || r.Value<string>("status") == "replaced");
            var skipped = results.Count(r => r.Value<string>("status") == "skipped");
            return Ok($"Created {created}, skipped {skipped}, failed {results.Count(r => !r.Value<bool>("success"))} of {results.Count} grids.",
                DocumentationUtils.Summarize(results));
        }

        private static object CreateGrid(Document doc, JObject item, string ifExists, GridType gridType,
            Tuple<double, double> extents, bool updateExtents)
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
                    var skipped = new JObject
                    {
                        ["id"] = existing.Id.GetValue(),
                        ["name"] = existing.Name,
                        ["status"] = "skipped",
                        ["message"] = "A grid with this name already exists."
                    };
                    if (updateExtents && extents != null)
                        ApplyExtents(existing, extents, skipped);
                    return skipped;
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
            if (extents != null)
                ApplyExtents(grid, extents, result);
            if (replaced)
                result["warning"] = "The old grid was deleted; dimensions and tags that referenced it are gone.";
            return result;
        }

        /// <summary>
        ///     Reads verticalExtents: "allLevels" (default: lowest level - 5000 mm
        ///     to highest level + 5000 mm), "none" (keep Revit's default) or
        ///     {bottom, top} elevations in mm. Returns feet, or null.
        /// </summary>
        private static Tuple<double, double> ReadVerticalExtents(Document doc, JToken token)
        {
            if (token is JObject range)
            {
                var bottom = range.Value<double?>("bottom") ?? throw new ArgumentException("verticalExtents needs 'bottom' (mm).");
                var top = range.Value<double?>("top") ?? throw new ArgumentException("verticalExtents needs 'top' (mm).");
                if (top - bottom < 1)
                    throw new ArgumentException("verticalExtents 'top' must be above 'bottom'.");
                return Tuple.Create(DocumentationUtils.MmToFeet(bottom), DocumentationUtils.MmToFeet(top));
            }

            var mode = token == null || token.Type == JTokenType.Null ? "allLevels" : token.ToString();
            if (mode == "none")
                return null;
            if (mode != "allLevels")
                throw new ArgumentException("verticalExtents must be 'allLevels', 'none' or {bottom, top}.");

            var elevations = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .Select(l => l.Elevation).ToList();
            if (elevations.Count == 0)
                return null;
            var margin = DocumentationUtils.MmToFeet(5000);
            return Tuple.Create(elevations.Min() - margin, elevations.Max() + margin);
        }

        private static void ApplyExtents(Grid grid, Tuple<double, double> extents, JObject result)
        {
            try
            {
                grid.SetVerticalExtents(extents.Item1, extents.Item2);
                result["verticalExtents"] = new JObject
                {
                    ["bottom"] = DocumentationUtils.FeetToMm(extents.Item1),
                    ["top"] = DocumentationUtils.FeetToMm(extents.Item2)
                };
            }
            catch (Exception ex)
            {
                result["extentsWarning"] = $"Vertical extents not set: {ex.Message}";
            }
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
