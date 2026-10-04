using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Datums
{
    /// <summary>
    ///     Per-view grid display: which end shows the bubble (resolved
    ///     geometrically from the side of the view each datum end lies on, since
    ///     End0/End1 order is arbitrary), 2D extents clipped to the crop box with
    ///     an offset, and optional propagation of the 2D extents to parallel views.
    ///     One undoable transaction; grids that cannot be shown in a view are
    ///     skipped and reported.
    /// </summary>
    public class SetGridDisplayEventHandler : JsonParameterEventHandler
    {
        private static readonly string[] Sides =
        {
            "top", "bottom", "left", "right", "top-left", "top-right", "bottom-left", "bottom-right", "both", "none",
            "start", "end"
        };

        public override string GetName() => "Set Grid Display";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var viewTokens = DocumentationUtils.RequireArray(parameters, "views");
            var defaultSide = ReadSide(parameters.Value<string>("bubbles"));
            var groups = new List<KeyValuePair<List<JToken>, string>>();
            if (parameters["groups"] is JArray groupArray)
            {
                foreach (var group in groupArray.OfType<JObject>())
                {
                    var side = ReadSide(group.Value<string>("bubbles")) ??
                               throw new ArgumentException("Each group needs 'bubbles'.");
                    var members = (group["grids"] as JArray)?.ToList() ??
                                  throw new ArgumentException("Each group needs 'grids'.");
                    groups.Add(new KeyValuePair<List<JToken>, string>(members, side));
                }
            }

            var extents = ReadExtents(parameters["extents"]);
            var requestedGrids = (parameters["grids"] as JArray)?.ToList();
            var propagateTargets = (parameters["propagateToViews"] as JArray)?.Select(t => t.Value<long>()).ToList();

            if (defaultSide == null && groups.Count == 0 && extents == null && (propagateTargets == null || propagateTargets.Count == 0))
                return Fail("Nothing to do: give bubbles, groups, extents or propagateToViews.");

            var allGrids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().ToList();
            var views = new List<View>();
            foreach (var token in viewTokens)
            {
                var view = doc.GetElement(token.Value<long>().ToRevitElementId()) as View;
                if (view == null || view.IsTemplate)
                    return Fail($"View {token} not found (or is a template).");
                views.Add(view);
            }

            var viewResults = new JArray();
            var changedGrids = 0;
            using (var transaction = DocumentationUtils.StartTransaction(doc, "MCP: Set Grid Display"))
            {
                foreach (var view in views)
                {
                    try
                    {
                        viewResults.Add(ProcessView(doc, view, allGrids, requestedGrids, defaultSide, groups, extents, ref changedGrids));
                    }
                    catch (Exception ex)
                    {
                        viewResults.Add(new JObject
                        {
                            ["viewId"] = view.Id.GetValue(),
                            ["viewName"] = view.Name,
                            ["error"] = ex.Message
                        });
                    }
                }

                if (propagateTargets != null && propagateTargets.Count > 0)
                    viewResults[0]["propagation"] = Propagate(doc, views[0], allGrids, requestedGrids, groups, propagateTargets);

                var status = transaction.Commit();
                if (status != TransactionStatus.Committed)
                    return Fail($"Transaction was not committed ({status}).");
            }

            return Ok($"Updated grid display in {views.Count} view(s) ({changedGrids} grid edits).", new { views = viewResults });
        }

        private static JObject ProcessView(Document doc, View view, List<Grid> allGrids, List<JToken> requestedGrids,
            string defaultSide, List<KeyValuePair<List<JToken>, string>> groups, Extents extents, ref int changedGrids)
        {
            var result = new JObject { ["viewId"] = view.Id.GetValue(), ["viewName"] = view.Name };
            var visibleIds = new HashSet<long>(new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid))
                .Select(g => g.Id.GetValue()));

            var notFound = new JArray();
            var notVisible = new JArray();
            var baseTargets = requestedGrids != null && requestedGrids.Count > 0
                ? Resolve(allGrids, requestedGrids, notFound)
                : allGrids.Where(g => visibleIds.Contains(g.Id.GetValue())).ToList();
            var baseIds = new HashSet<long>(baseTargets.Select(g => g.Id.GetValue()));
            var targets = new List<Grid>(baseTargets);
            foreach (var group in groups)
                targets.AddRange(Resolve(allGrids, group.Key, notFound).Where(g => targets.All(t => t.Id != g.Id)));

            var transform = ViewTransform(view);
            double[] rect = null;
            if (extents != null)
            {
                rect = CropRect(view, extents, out var cropWarning);
                if (cropWarning != null)
                    result["extentsWarning"] = cropWarning;
            }

            var gridResults = new JArray();
            var skipped = new JArray();
            foreach (var grid in targets.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!visibleIds.Contains(grid.Id.GetValue()) || !SafeCanBeVisible(grid, view))
                {
                    notVisible.Add(grid.Name);
                    continue;
                }

                var side = baseIds.Contains(grid.Id.GetValue()) ? defaultSide : null;
                foreach (var group in groups)
                {
                    if (Resolve(allGrids, group.Key, null).Any(g => g.Id == grid.Id))
                        side = group.Value;
                }

                var item = new JObject { ["name"] = grid.Name, ["id"] = grid.Id.GetValue() };
                try
                {
                    if (rect != null)
                    {
                        item["extents"] = ClipToRect(grid, view, transform, rect);
                        changedGrids++;
                    }

                    if (side != null)
                    {
                        item["bubbles"] = ApplyBubbles(grid, view, transform, side);
                        changedGrids++;
                    }

                    if (item.Count > 2)
                        gridResults.Add(item);
                }
                catch (Exception ex)
                {
                    var message = ex.Message;
                    if (message.IndexOf("visible", StringComparison.OrdinalIgnoreCase) >= 0)
                        notVisible.Add(grid.Name);
                    else
                        skipped.Add(new JObject { ["name"] = grid.Name, ["reason"] = message });
                }
            }

            result["grids"] = gridResults;
            if (notVisible.Count > 0)
                result["notVisible"] = notVisible;
            if (skipped.Count > 0)
                result["skipped"] = skipped;
            if (notFound.Count > 0)
                result["notFound"] = notFound;
            return result;
        }

        private static bool SafeCanBeVisible(Grid grid, View view)
        {
            try
            {
                return grid.CanBeVisibleInView(view);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static List<Grid> Resolve(List<Grid> allGrids, IEnumerable<JToken> tokens, JArray notFound)
        {
            var result = new List<Grid>();
            foreach (var token in tokens)
            {
                Grid grid;
                if (token.Type == JTokenType.Integer)
                    grid = allGrids.FirstOrDefault(g => g.Id.GetValue() == token.Value<long>());
                else
                {
                    var name = token.ToString().Trim();
                    grid = allGrids.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.Ordinal))
                           ?? allGrids.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
                }

                if (grid == null)
                    notFound?.Add(token.ToString());
                else if (result.All(g => g.Id != grid.Id))
                    result.Add(grid);
            }

            return result;
        }

        // ------------------------------------------------------------- bubbles

        private static JObject ApplyBubbles(Grid grid, View view, Transform toView, string side)
        {
            var curve = GridCurve(grid, view);
            var a = toView.OfPoint(curve.GetEndPoint(0));
            var b = toView.OfPoint(curve.GetEndPoint(1));
            var du = b.X - a.X;
            var dv = b.Y - a.Y;
            var length = Math.Sqrt(du * du + dv * dv);

            bool show0, show1;
            var resolved = side;
            switch (side)
            {
                case "both":
                    show0 = show1 = true;
                    break;
                case "none":
                    show0 = show1 = false;
                    break;
                case "start":
                    show0 = true;
                    show1 = false;
                    break;
                case "end":
                    show0 = false;
                    show1 = true;
                    break;
                default:
                    var parts = side.Split('-');
                    if (parts.Length == 2)
                        resolved = Math.Abs(dv) >= Math.Abs(du) ? parts[0] : parts[1];

                    var vertical = resolved == "top" || resolved == "bottom";
                    var along = vertical ? dv : du;
                    if (length < 1e-9 || Math.Abs(along) < 0.05 * length)
                        return new JObject
                        {
                            ["requested"] = side,
                            ["result"] = "unchanged",
                            ["reason"] = $"grid runs parallel to the {resolved} edge in this view"
                        };

                    // End1 lies further in +v (top) / +u (right) when 'along' is positive.
                    var end1IsHigher = along > 0;
                    var wantHigher = resolved == "top" || resolved == "right";
                    show1 = end1IsHigher == wantHigher;
                    show0 = !show1;
                    break;
            }

            SetBubble(grid, view, DatumEnds.End0, show0);
            SetBubble(grid, view, DatumEnds.End1, show1);
            return new JObject
            {
                ["requested"] = side,
                ["side"] = resolved,
                ["end0"] = show0,
                ["end1"] = show1
            };
        }

        private static void SetBubble(Grid grid, View view, DatumEnds end, bool show)
        {
            if (show)
                grid.ShowBubbleInView(end, view);
            else
                grid.HideBubbleInView(end, view);
        }

        // ------------------------------------------------------------- extents

        private sealed class Extents
        {
            public double OffsetFeet;
            public bool Paper;
        }

        private static Extents ReadExtents(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token.Type == JTokenType.String)
            {
                if (token.ToString() != "clipToCrop")
                    throw new ArgumentException("'extents' must be \"clipToCrop\" or {offsetMm} / {offsetPaperMm}.");
                return new Extents();
            }

            if (token is JObject obj)
            {
                var paper = obj.Value<double?>("offsetPaperMm");
                var model = obj.Value<double?>("offsetMm");
                if (paper.HasValue && model.HasValue)
                    throw new ArgumentException("Give offsetMm or offsetPaperMm, not both.");
                return paper.HasValue
                    ? new Extents { OffsetFeet = DocumentationUtils.MmToFeet(paper.Value), Paper = true }
                    : new Extents { OffsetFeet = DocumentationUtils.MmToFeet(model ?? 0) };
            }

            throw new ArgumentException("'extents' must be \"clipToCrop\" or {offsetMm} / {offsetPaperMm}.");
        }

        /// <summary>Model-to-view transform: X = view right, Y = view up (crop box frame when available).</summary>
        private static Transform ViewTransform(View view)
        {
            var crop = view.CropBox;
            if (crop?.Transform != null)
                return crop.Transform.Inverse;

            var transform = Transform.CreateTranslation(XYZ.Zero);
            transform.BasisX = view.RightDirection;
            transform.BasisY = view.UpDirection;
            transform.BasisZ = view.ViewDirection;
            transform.Origin = view.Origin;
            return transform.Inverse;
        }

        /// <summary>Crop rectangle [umin, vmin, umax, vmax] in view coordinates, shrunk by the offset.</summary>
        private static double[] CropRect(View view, Extents extents, out string warning)
        {
            warning = null;
            var crop = view.CropBox ?? throw new ArgumentException($"View '{view.Name}' has no crop box.");
            if (!view.CropBoxActive)
                warning = "Crop region is not active; extents follow the inactive crop box.";

            var offset = extents.Paper ? extents.OffsetFeet * Math.Max(1, view.Scale) : extents.OffsetFeet;
            var rect = new[]
            {
                Math.Min(crop.Min.X, crop.Max.X) + offset,
                Math.Min(crop.Min.Y, crop.Max.Y) + offset,
                Math.Max(crop.Min.X, crop.Max.X) - offset,
                Math.Max(crop.Min.Y, crop.Max.Y) - offset
            };
            if (rect[2] - rect[0] < 1e-3 || rect[3] - rect[1] < 1e-3)
                throw new ArgumentException($"Offset is larger than half the crop box of '{view.Name}'.");
            return rect;
        }

        private static string ClipToRect(Grid grid, View view, Transform toView, double[] rect)
        {
            if (!(GridCurve(grid, view) is Line line))
                return "unchanged (arc grid)";

            var p0 = line.GetEndPoint(0);
            var p1 = line.GetEndPoint(1);
            var a = toView.OfPoint(p0);
            var b = toView.OfPoint(p1);
            double tMin = double.NegativeInfinity, tMax = double.PositiveInfinity;
            if (!Clip(a.X, b.X - a.X, rect[0], rect[2], ref tMin, ref tMax) ||
                !Clip(a.Y, b.Y - a.Y, rect[1], rect[3], ref tMin, ref tMax) ||
                double.IsInfinity(tMin) || double.IsInfinity(tMax) || (tMax - tMin) * line.Length < 1e-3)
                return "unchanged (grid does not cross the crop region)";

            var start = p0 + (p1 - p0) * tMin;
            var end = p0 + (p1 - p0) * tMax;
            var clipped = Line.CreateBound(start, end);

            foreach (var datumEnd in new[] { DatumEnds.End0, DatumEnds.End1 })
            {
                if (grid.GetDatumExtentTypeInView(datumEnd, view) != DatumExtentType.ViewSpecific)
                    grid.SetDatumExtentType(datumEnd, view, DatumExtentType.ViewSpecific);
            }

            if (!grid.IsCurveValidInView(DatumExtentType.ViewSpecific, view, clipped))
                return "unchanged (Revit rejected the clipped curve for this view)";
            grid.SetCurveInView(DatumExtentType.ViewSpecific, view, clipped);
            return $"clipped ({DocumentationUtils.FeetToMm(clipped.Length)} mm)";
        }

        /// <summary>Liang-Barsky slab clip of origin + t * delta against [min, max].</summary>
        private static bool Clip(double origin, double delta, double min, double max, ref double tMin, ref double tMax)
        {
            if (Math.Abs(delta) < 1e-12)
                return origin >= min && origin <= max;
            var t1 = (min - origin) / delta;
            var t2 = (max - origin) / delta;
            if (t1 > t2)
            {
                var swap = t1;
                t1 = t2;
                t2 = swap;
            }

            tMin = Math.Max(tMin, t1);
            tMax = Math.Min(tMax, t2);
            return tMax > tMin;
        }

        private static Curve GridCurve(Grid grid, View view)
        {
            var curves = grid.GetCurvesInView(DatumExtentType.ViewSpecific, view);
            if (curves == null || curves.Count == 0)
                curves = grid.GetCurvesInView(DatumExtentType.Model, view);
            return curves?.FirstOrDefault() ?? throw new InvalidOperationException("The grid has no curve in this view.");
        }

        // ---------------------------------------------------------- propagation

        private static JObject Propagate(Document doc, View source, List<Grid> allGrids, List<JToken> requestedGrids,
            List<KeyValuePair<List<JToken>, string>> groups, List<long> targetIds)
        {
            var visibleIds = new HashSet<long>(new FilteredElementCollector(doc, source.Id).OfClass(typeof(Grid))
                .Select(g => g.Id.GetValue()));
            var grids = requestedGrids != null && requestedGrids.Count > 0
                ? Resolve(allGrids, requestedGrids, null)
                : allGrids.Where(g => visibleIds.Contains(g.Id.GetValue())).ToList();
            foreach (var group in groups)
                grids.AddRange(Resolve(allGrids, group.Key, null).Where(g => grids.All(x => x.Id != g.Id)));

            var invalid = new HashSet<long>();
            var propagated = 0;
            var failed = new JArray();
            foreach (var grid in grids.Where(g => visibleIds.Contains(g.Id.GetValue())))
            {
                try
                {
                    var valid = new HashSet<long>(grid.GetPropagationViews(source).Select(id => id.GetValue()));
                    var targets = new HashSet<ElementId>();
                    foreach (var id in targetIds)
                    {
                        if (valid.Contains(id))
                            targets.Add(id.ToRevitElementId());
                        else
                            invalid.Add(id);
                    }

                    if (targets.Count == 0)
                        continue;
                    grid.PropagateToViews(source, targets);
                    propagated++;
                }
                catch (Exception ex)
                {
                    failed.Add(new JObject { ["name"] = grid.Name, ["reason"] = ex.Message });
                }
            }

            var result = new JObject { ["sourceViewId"] = source.Id.GetValue(), ["gridsPropagated"] = propagated };
            if (invalid.Count > 0)
                result["invalidTargets"] = new JArray(invalid.Select(id => new JObject
                {
                    ["viewId"] = id,
                    ["reason"] = "not a parallel view where this grid is visible (or not a valid propagation target)"
                }));
            if (failed.Count > 0)
                result["failed"] = failed;
            return result;
        }

        private static string ReadSide(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            var side = value.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
            if (!Sides.Contains(side))
                throw new ArgumentException($"'bubbles' must be one of: {string.Join(", ", Sides)}.");
            return side;
        }
    }
}
