using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Creates chain and overall dimensions between parallel linear grids in
    ///     a plan, section or elevation. Grids are grouped by their in-view
    ///     direction, ordered across that direction, and dimensioned near the
    ///     requested grid end(s). Offsets are model millimetres measured inward
    ///     from the grid end, so dimension strings sit between the bubbles and
    ///     the model.
    /// </summary>
    public class CreateGridDimensionsEventHandler : JsonParameterEventHandler
    {
        private const double ParallelToleranceDegrees = 0.5;
        private const double DefaultOffsetMm = 1500;
        private const double DefaultOverallOffsetMm = 800;

        public override string GetName() => "Create Grid Dimensions";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var view = DocumentationUtils.GetElement<View>(doc, DocumentationUtils.ReadId(parameters, "viewId"))
                       ?? uiDoc.ActiveView;
            if (view.IsTemplate || !(view is ViewPlan || view is ViewSection))
                return Fail($"View '{view.Name}' must be a plan, section or elevation view.");

            DimensionType dimensionType = null;
            var dimensionTypeId = DocumentationUtils.ReadId(parameters, "dimensionTypeId");
            if (dimensionTypeId != null)
            {
                dimensionType = DocumentationUtils.GetElement<DimensionType>(doc, dimensionTypeId);
                if (dimensionType == null)
                    return Fail($"dimensionTypeId {dimensionTypeId} is not a dimension type.");
            }

            var chain = parameters.Value<bool?>("chain") ?? true;
            var overall = parameters.Value<bool?>("overall") ?? true;
            if (!chain && !overall)
                return Fail("At least one of 'chain' or 'overall' must be true.");
            var offset = DocumentationUtils.MmToFeet(parameters.Value<double?>("offsetMm") ?? DefaultOffsetMm);
            var overallOffset = DocumentationUtils.MmToFeet(parameters.Value<double?>("overallOffsetMm") ?? DefaultOverallOffsetMm);
            var skipExisting = parameters.Value<bool?>("skipExisting") ?? true;
            var sides = (parameters["sides"]?.ToObject<List<string>>() ?? new List<string> { "Start" })
                .Select(s => DocumentationUtils.ParseEnum(s, GridEnd.Start))
                .Distinct()
                .ToList();
            if (sides.Count == 0)
                sides.Add(GridEnd.Start);

            var skipped = new List<object>();
            var gridLines = CollectGridLines(doc, view, parameters, skipped);
            var groups = GroupByDirection(gridLines, skipped);
            if (groups.Count == 0)
                return Ok("No group of two or more parallel grids to dimension.", new { viewId = view.Id.GetValue(), groups = new object[0], skipped });

            var existing = skipExisting ? ExistingReferenceSets(doc, view) : new List<HashSet<long>>();

            var items = new JArray(Enumerable.Range(0, groups.Count));
            var results = DocumentationUtils.RunBatch(doc, "MCP: Create Grid Dimensions", items, token =>
            {
                var group = groups[token.Value<int>()];
                var created = new List<object>();
                var groupSkipped = new List<string>();

                foreach (var side in sides)
                {
                    if (chain)
                    {
                        var id = CreateDimension(doc, view, group, group, side, offset, dimensionType, existing);
                        if (id == null) groupSkipped.Add($"{side} chain already exists");
                        else created.Add(new { side = side.ToString(), kind = "chain", dimensionId = id });
                    }

                    if (overall && (group.Count > 2 || !chain))
                    {
                        var ends = new List<GridLine> { group.First(), group.Last() };
                        var id = CreateDimension(doc, view, group, ends, side, offset - overallOffset, dimensionType, existing);
                        if (id == null) groupSkipped.Add($"{side} overall already exists");
                        else created.Add(new { side = side.ToString(), kind = "overall", dimensionId = id });
                    }
                }

                return new
                {
                    direction = new { x = Math.Round(group[0].Direction.X, 4), y = Math.Round(group[0].Direction.Y, 4), z = Math.Round(group[0].Direction.Z, 4) },
                    grids = group.Select(g => g.Grid.Name).ToList(),
                    created,
                    skipped = groupSkipped.Count > 0 ? groupSkipped : null
                };
            });

            var count = results.Where(r => r.Value<bool>("success"))
                .Sum(r => (r["created"] as JArray)?.Count ?? 0);
            return Ok($"Created {count} grid dimensions in '{view.Name}' across {groups.Count} direction groups.", new
            {
                viewId = view.Id.GetValue(),
                groups = results,
                skipped
            });
        }

        /// <returns>The new dimension id, or null when an identical dimension already exists.</returns>
        private static long? CreateDimension(Document doc, View view, List<GridLine> group, List<GridLine> referenced,
            GridEnd side, double inwardOffset, DimensionType dimensionType, List<HashSet<long>> existing)
        {
            var referencedIds = new HashSet<long>(referenced.Select(g => g.Grid.Id.GetValue()));
            if (existing.Any(set => set.SetEquals(referencedIds)))
                return null;

            var axis = group[0].Direction;
            var across = group[0].Across;

            // Along-axis coordinate of the grid ends on the requested side, then
            // move inward towards the model by the offset.
            double along;
            if (side == GridEnd.Start)
                along = group.Min(g => g.Start.DotProduct(axis)) + inwardOffset;
            else
                along = group.Max(g => g.End.DotProduct(axis)) - inwardOffset;

            var first = referenced.First();
            var last = referenced.Last();
            var origin = first.Start + axis * (along - first.Start.DotProduct(axis));
            var span = last.Position - first.Position;
            var end = origin + across * span;
            if (origin.DistanceTo(end) < 1e-6)
                throw new InvalidOperationException("Grids in the group are coincident.");

            var references = new ReferenceArray();
            foreach (var grid in referenced)
                references.Append(new Reference(grid.Grid));

            var line = Line.CreateBound(origin, end);
            var dimension = dimensionType != null
                ? doc.Create.NewDimension(view, line, references, dimensionType)
                : doc.Create.NewDimension(view, line, references);
            if (dimension == null)
                throw new InvalidOperationException("Revit did not create the dimension.");

            existing.Add(referencedIds);
            return dimension.Id.GetValue();
        }

        private static List<GridLine> CollectGridLines(Document doc, View view, JObject parameters, List<object> skipped)
        {
            IEnumerable<Grid> grids;
            if (parameters["gridIds"] is JArray ids && ids.Count > 0)
            {
                var list = new List<Grid>();
                foreach (var id in ids.Select(t => t.Value<long>()).Distinct())
                {
                    if (doc.GetElement(id.ToRevitElementId()) is Grid grid)
                        list.Add(grid);
                    else
                        skipped.Add(new { gridId = id, reason = "Not a grid." });
                }

                grids = list;
            }
            else
            {
                grids = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>();
            }

            var viewDirection = view.ViewDirection.Normalize();
            var lines = new List<GridLine>();
            foreach (var grid in grids)
            {
                Curve curve = null;
                try
                {
                    curve = grid.GetCurvesInView(DatumExtentType.ViewSpecific, view).FirstOrDefault();
                }
                catch (Exception)
                {
                    // Grid not visible in this view; fall back to the model curve below.
                }

                curve = curve ?? grid.Curve;
                if (!(curve is Line line))
                {
                    skipped.Add(new { gridId = grid.Id.GetValue(), name = grid.Name, reason = "Arc grids are not supported." });
                    continue;
                }

                // Project onto the view plane so section/elevation grids compare correctly.
                var start = ProjectToViewPlane(line.GetEndPoint(0), view.Origin, viewDirection);
                var end = ProjectToViewPlane(line.GetEndPoint(1), view.Origin, viewDirection);
                if (start.DistanceTo(end) < 1e-6)
                {
                    skipped.Add(new { gridId = grid.Id.GetValue(), name = grid.Name, reason = "Grid is parallel to the view direction." });
                    continue;
                }

                lines.Add(new GridLine(grid, start, end, viewDirection));
            }

            return lines;
        }

        private static List<List<GridLine>> GroupByDirection(List<GridLine> lines, List<object> skipped)
        {
            var cosTolerance = Math.Cos(ParallelToleranceDegrees * Math.PI / 180);
            var groups = new List<List<GridLine>>();

            foreach (var line in lines.OrderBy(l => l.Grid.Id.GetValue()))
            {
                var group = groups.FirstOrDefault(g => Math.Abs(g[0].Direction.DotProduct(line.Direction)) >= cosTolerance);
                if (group == null)
                {
                    groups.Add(new List<GridLine> { line });
                    continue;
                }

                line.AlignTo(group[0]);
                group.Add(line);
            }

            var result = new List<List<GridLine>>();
            foreach (var group in groups)
            {
                if (group.Count < 2)
                {
                    skipped.Add(new { gridId = group[0].Grid.Id.GetValue(), name = group[0].Grid.Name, reason = "No parallel grid to dimension to." });
                    continue;
                }

                foreach (var line in group)
                    line.Position = line.Start.DotProduct(group[0].Across);

                var ordered = group.OrderBy(l => l.Position).ToList();
                var distinct = new List<GridLine>();
                foreach (var line in ordered)
                {
                    if (distinct.Count > 0 && Math.Abs(line.Position - distinct.Last().Position) < 1e-6)
                    {
                        skipped.Add(new { gridId = line.Grid.Id.GetValue(), name = line.Grid.Name, reason = $"Coincident with grid {distinct.Last().Grid.Name}." });
                        continue;
                    }

                    distinct.Add(line);
                }

                if (distinct.Count >= 2)
                    result.Add(distinct);
            }

            return result;
        }

        private static List<HashSet<long>> ExistingReferenceSets(Document doc, View view)
        {
            var sets = new List<HashSet<long>>();
            foreach (var dimension in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                var set = new HashSet<long>();
                foreach (Reference reference in dimension.References)
                    set.Add(reference.ElementId.GetValue());
                sets.Add(set);
            }

            return sets;
        }

        private static XYZ ProjectToViewPlane(XYZ point, XYZ origin, XYZ normal)
        {
            return point - normal * (point - origin).DotProduct(normal);
        }

        private enum GridEnd
        {
            Start,
            End
        }

        private class GridLine
        {
            public GridLine(Grid grid, XYZ start, XYZ end, XYZ viewDirection)
            {
                Grid = grid;
                Start = start;
                End = end;
                Direction = (end - start).Normalize();
                Across = viewDirection.CrossProduct(Direction).Normalize();
            }

            public Grid Grid { get; }
            public XYZ Start { get; private set; }
            public XYZ End { get; private set; }
            public XYZ Direction { get; private set; }
            public XYZ Across { get; private set; }
            public double Position { get; set; }

            /// <summary>Flips this line, if needed, to run the same way as the group reference.</summary>
            public void AlignTo(GridLine reference)
            {
                if (Direction.DotProduct(reference.Direction) < 0)
                    (Start, End) = (End, Start);
                Direction = reference.Direction;
                Across = reference.Across;
            }
        }
    }
}
