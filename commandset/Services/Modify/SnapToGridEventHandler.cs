using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Modify
{
    /// <summary>
    ///     snap_to_grid: removes small position errors of extracted models. For every selected
    ///     element and each of its local plan axes d, the offset of the element from the nearest
    ///     grid running across d is rounded to stepMm and the element is moved by the difference
    ///     when it is at most maxShiftMm. Point-based instances (columns, footings, piles) use
    ///     their transform's BasisX / BasisY; straight line-based elements (walls, beams) snap
    ///     across their line (normal) and each end point along it. axes "global" rounds X / Y
    ///     relative to the project base point instead. dryRun (default) only reports.
    ///     Point elements are moved first, in batches of identical delta (fast for thousands of
    ///     joined columns); line elements are then evaluated on the regenerated model.
    /// </summary>
    public class SnapToGridEventHandler : JsonParameterEventHandler
    {
        private const double MmPerFoot = 304.8;
        private const double AlignedToleranceMm = 0.01;
        private const int SampleSize = 15;
        private static readonly double ParallelTolerance = Math.Sin(0.5 * Math.PI / 180.0);

        public override string GetName() => "Snap To Grid";

        private class GridLine
        {
            public string Name;
            public long Id;
            public XYZ Origin;
            public XYZ Direction;
        }

        private class AxisResult
        {
            public string Axis;
            public string Grid;
            public double OffsetMm;
            public double SnappedMm;
            public double DeltaMm;
            public bool TooLarge;
            public bool NoReference;
        }

        private class Plan
        {
            public Element Element;
            public string Category;
            public bool IsLine;
            public XYZ Move = XYZ.Zero; // point elements / pure translation of lines
            public XYZ NewStart, NewEnd; // line elements
            public double ShiftMm;
            public string Skip;
            public readonly List<AxisResult> Axes = new List<AxisResult>();
            public bool AnyTooLarge => Axes.Any(a => a.TooLarge);
            public bool AnyNoReference => Axes.Any(a => a.NoReference);
            public bool Moves => Skip == null && ShiftMm > AlignedToleranceMm;
        }

        private class Settings
        {
            public bool Global;
            public double Step;
            public double MaxShift;
            public double Reach;
            public XYZ Origin = XYZ.Zero;
            public List<GridLine> Grids = new List<GridLine>();
        }

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            if (doc.IsFamilyDocument) return Fail("snap_to_grid works in project documents only.");

            var warnings = new List<string>();
            if (parameters["categories"] == null && parameters["elementIds"] == null)
                parameters["categories"] = new JArray("StructuralColumns", "StructuralFoundation", "Walls");

            var levels = ModelSelectionUtils.SortedLevels(doc);
            var elements = ModelSelectionUtils.Collect(doc, parameters, levels, warnings);
            var typeContains = parameters.Value<string>("typeNameContains");
            if (!string.IsNullOrWhiteSpace(typeContains))
                elements = elements.Where(e => ModelSelectionUtils.TypeName(doc, e)
                    .IndexOf(typeContains.Trim(), StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            elements = elements.OrderBy(e => e.Id.GetValue()).ToList();

            var settings = ReadSettings(doc, parameters, warnings);
            if (!settings.Global && settings.Grids.Count == 0)
                return Fail("The model has no straight grids; use axes:'global' or create grids first.");

            var dryRun = parameters.Value<bool?>("dryRun") ?? true;

            var pointElements = new List<Element>();
            var lineElements = new List<Element>();
            var plans = new List<Plan>();
            foreach (var element in elements)
            {
                if (element.Location is LocationPoint) pointElements.Add(element);
                else if (element.Location is LocationCurve) lineElements.Add(element);
                else plans.Add(new Plan { Element = element, Category = ModelSelectionUtils.CategoryKey(element), Skip = "no location point or curve" });
            }

            var pointPlans = pointElements.Select(e => PlanPoint(e, settings)).ToList();
            var applied = new Dictionary<string, int>();
            var failed = new List<object>();
            var lineMoveMode = 0;
            List<Plan> linePlans;

            if (dryRun)
            {
                linePlans = lineElements.Select(e => PlanLine(e, settings)).ToList();
            }
            else
            {
                using (var transaction = ModelSelectionUtils.StartTransaction(doc, "MCP: Snap To Grid", warnings))
                {
                    ApplyTranslations(doc, pointPlans.Where(p => p.Moves).ToList(), applied, failed);
                    doc.Regenerate();

                    // Line elements are evaluated after the columns moved: joined beam ends may have followed them.
                    linePlans = lineElements.Select(e => PlanLine(e, settings)).ToList();
                    var moving = linePlans.Where(p => p.Moves).ToList();
                    var translations = moving.Where(p => p.NewStart == null).ToList();
                    ApplyTranslations(doc, translations, applied, failed);
                    foreach (var plan in moving.Where(p => p.NewStart != null))
                    {
                        try
                        {
                            ((LocationCurve)plan.Element.Location).Curve = Line.CreateBound(plan.NewStart, plan.NewEnd);
                            Count(applied, plan.Category);
                            lineMoveMode++;
                        }
                        catch (Exception ex)
                        {
                            failed.Add(new { elementId = plan.Element.Id.GetValue(), reason = ex.Message });
                        }
                    }

                    var status = transaction.Commit();
                    if (status != TransactionStatus.Committed)
                        return Fail($"Snap transaction was not committed ({status}). {string.Join(" ", warnings)}");
                }
            }

            plans.AddRange(pointPlans);
            plans.AddRange(linePlans);

            var byCategory = new JObject();
            foreach (var group in plans.GroupBy(p => p.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var moving = group.Where(p => p.Moves).ToList();
                var entry = new JObject
                {
                    ["count"] = group.Count(),
                    [dryRun ? "wouldMove" : "moved"] = dryRun ? moving.Count : (applied.TryGetValue(group.Key, out var n) ? n : 0),
                    ["alreadyAligned"] = group.Count(p => p.Skip == null && !p.Moves && !p.AnyTooLarge && !p.AnyNoReference),
                    ["maxShiftMm"] = moving.Count == 0 ? 0 : Math.Round(moving.Max(p => p.ShiftMm), 2),
                    ["avgShiftMm"] = moving.Count == 0 ? 0 : Math.Round(moving.Average(p => p.ShiftMm), 2),
                    ["tooLarge"] = group.Count(p => p.AnyTooLarge),
                    ["noGridOnSomeAxis"] = group.Count(p => p.Skip == null && p.AnyNoReference),
                    ["skipped"] = group.Count(p => p.Skip != null),
                    ["samples"] = new JArray(moving.OrderByDescending(p => p.ShiftMm).Take(SampleSize).Select(Describe))
                };
                var tooLarge = group.Where(p => p.AnyTooLarge).Take(SampleSize).ToList();
                if (tooLarge.Count > 0) entry["tooLargeSamples"] = new JArray(tooLarge.Select(Describe));
                var skipped = group.Where(p => p.Skip != null).Take(SampleSize).ToList();
                if (skipped.Count > 0)
                    entry["skippedSamples"] = new JArray(skipped.Select(p => new JObject { ["elementId"] = p.Element.Id.GetValue(), ["reason"] = p.Skip }));
                byCategory[group.Key] = entry;
            }

            var totalMoving = plans.Count(p => p.Moves);
            var response = new JObject
            {
                ["dryRun"] = dryRun,
                ["axes"] = settings.Global ? "global" : "grid",
                ["stepMm"] = settings.Step * MmPerFoot,
                ["maxShiftMm"] = settings.MaxShift * MmPerFoot,
                ["matched"] = plans.Count,
                [dryRun ? "wouldMove" : "moved"] = dryRun ? totalMoving : applied.Values.Sum(),
                ["tooLarge"] = plans.Count(p => p.AnyTooLarge),
                ["byCategory"] = byCategory
            };
            if (!dryRun && lineMoveMode > 0) response["lineEndsReshaped"] = lineMoveMode;
            if (failed.Count > 0) response["failed"] = JArray.FromObject(failed.Take(50));
            if (warnings.Count > 0) response["warnings"] = new JArray(warnings.Distinct().Take(30));

            var message = dryRun
                ? $"Dry run: {totalMoving} of {plans.Count} element(s) would move (step {settings.Step * MmPerFoot} mm, max shift {settings.MaxShift * MmPerFoot} mm); {plans.Count(p => p.AnyTooLarge)} have larger offsets that are not snapped. Pass dryRun:false to apply."
                : $"Moved {applied.Values.Sum()} of {plans.Count} element(s); {failed.Count} failed; {plans.Count(p => p.AnyTooLarge)} have offsets larger than maxShiftMm (not snapped on that axis).";
            return Ok(message, response);
        }

        // ---------------------------------------------------------------- settings

        private static Settings ReadSettings(Document doc, JObject parameters, List<string> warnings)
        {
            var settings = new Settings
            {
                Global = string.Equals(parameters.Value<string>("axes")?.Trim(), "global", StringComparison.OrdinalIgnoreCase),
                Step = Clamp(parameters.Value<double?>("stepMm") ?? 50, 0.1, 10000) / MmPerFoot,
                MaxShift = Clamp(parameters.Value<double?>("maxShiftMm") ?? 30, 0, 10000) / MmPerFoot,
                Reach = Clamp(parameters.Value<double?>("maxGridDistanceMm") ?? 5000, 1, 1000000) / MmPerFoot
            };

            if (settings.Global)
            {
                var origin = parameters.Value<string>("globalOrigin")?.Trim();
                if (!string.Equals(origin, "internal", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var basePoint = BasePoint.GetProjectBasePoint(doc);
                        if (basePoint != null)
                            settings.Origin = new XYZ(basePoint.Position.X, basePoint.Position.Y, 0);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Project base point not readable ({ex.Message}); rounding relative to the internal origin.");
                    }
                }
                return settings;
            }

            foreach (var grid in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
            {
                if (!(grid.Curve is Line line)) continue;
                var direction = new XYZ(line.Direction.X, line.Direction.Y, 0);
                if (direction.GetLength() < 1e-9) continue;
                settings.Grids.Add(new GridLine
                {
                    Name = grid.Name,
                    Id = grid.Id.GetValue(),
                    Origin = new XYZ(line.Origin.X, line.Origin.Y, 0),
                    Direction = direction.Normalize()
                });
            }
            return settings;
        }

        private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));

        // ---------------------------------------------------------------- planning

        /// <summary>Snaps the offset of <paramref name="point" /> along unit plan axis <paramref name="d" />.</summary>
        private static AxisResult SnapAxis(XYZ point, XYZ d, string axis, Settings settings)
        {
            var flat = new XYZ(point.X, point.Y, 0);
            var result = new AxisResult { Axis = axis };
            double offset;
            if (settings.Global)
            {
                offset = (flat - settings.Origin).DotProduct(d);
                result.Grid = "(origin)";
            }
            else
            {
                GridLine best = null;
                var bestOffset = 0.0;
                foreach (var grid in settings.Grids)
                {
                    // The grid must run across d: its direction is perpendicular to d.
                    if (Math.Abs(grid.Direction.DotProduct(d)) > ParallelTolerance) continue;
                    var o = (flat - grid.Origin).DotProduct(d);
                    if (Math.Abs(o) > settings.Reach) continue;
                    if (best == null || Math.Abs(o) < Math.Abs(bestOffset))
                    {
                        best = grid;
                        bestOffset = o;
                    }
                }

                if (best == null)
                {
                    result.NoReference = true;
                    return result;
                }

                offset = bestOffset;
                result.Grid = best.Name;
            }

            var snapped = Math.Round(offset / settings.Step, MidpointRounding.AwayFromZero) * settings.Step;
            var delta = snapped - offset;
            result.OffsetMm = Math.Round(offset * MmPerFoot, 2);
            result.SnappedMm = Math.Round(snapped * MmPerFoot, 2);
            result.DeltaMm = Math.Round(delta * MmPerFoot, 3);
            if (Math.Abs(delta) > settings.MaxShift + 1e-9)
            {
                result.TooLarge = true;
                result.DeltaMm = 0;
            }
            return result;
        }

        private static Plan NewPlan(Element element)
        {
            var plan = new Plan { Element = element, Category = ModelSelectionUtils.CategoryKey(element) };
            if (element.Pinned) plan.Skip = "pinned";
            else if (element.GroupId != null && element.GroupId != ElementId.InvalidElementId) plan.Skip = "member of a group";
            return plan;
        }

        private static Plan PlanPoint(Element element, Settings settings)
        {
            var plan = NewPlan(element);
            if (plan.Skip != null) return plan;
            var point = ((LocationPoint)element.Location).Point;

            XYZ ax = XYZ.BasisX, ay = XYZ.BasisY;
            if (!settings.Global && element is FamilyInstance instance)
            {
                var transform = instance.GetTransform();
                var bx = new XYZ(transform.BasisX.X, transform.BasisX.Y, 0);
                if (bx.GetLength() > 1e-6)
                {
                    ax = bx.Normalize();
                    ay = new XYZ(-ax.Y, ax.X, 0);
                }
            }

            var rx = SnapAxis(point, ax, settings.Global ? "x" : "localX", settings);
            var ry = SnapAxis(point, ay, settings.Global ? "y" : "localY", settings);
            plan.Axes.Add(rx);
            plan.Axes.Add(ry);
            plan.Move = ax * (rx.DeltaMm / MmPerFoot) + ay * (ry.DeltaMm / MmPerFoot);
            plan.ShiftMm = plan.Move.GetLength() * MmPerFoot;
            return plan;
        }

        private static Plan PlanLine(Element element, Settings settings)
        {
            var plan = NewPlan(element);
            plan.IsLine = true;
            if (plan.Skip != null) return plan;
            if (!(((LocationCurve)element.Location).Curve is Line line))
            {
                plan.Skip = "curved location line";
                return plan;
            }

            var start = line.GetEndPoint(0);
            var end = line.GetEndPoint(1);
            var flat = new XYZ(end.X - start.X, end.Y - start.Y, 0);
            if (flat.GetLength() < 1e-6)
            {
                plan.Skip = "vertical location line";
                return plan;
            }

            XYZ startMove, endMove;
            if (settings.Global)
            {
                var sx = SnapAxis(start, XYZ.BasisX, "startX", settings);
                var sy = SnapAxis(start, XYZ.BasisY, "startY", settings);
                var ex = SnapAxis(end, XYZ.BasisX, "endX", settings);
                var ey = SnapAxis(end, XYZ.BasisY, "endY", settings);
                plan.Axes.AddRange(new[] { sx, sy, ex, ey });
                startMove = new XYZ(sx.DeltaMm, sy.DeltaMm, 0) / MmPerFoot;
                endMove = new XYZ(ex.DeltaMm, ey.DeltaMm, 0) / MmPerFoot;
            }
            else
            {
                var along = flat.Normalize();
                var normal = new XYZ(-along.Y, along.X, 0);
                var cross = SnapAxis((start + end) / 2, normal, "across", settings);
                var s = SnapAxis(start, along, "start", settings);
                var e = SnapAxis(end, along, "end", settings);
                plan.Axes.AddRange(new[] { cross, s, e });
                var crossMove = normal * (cross.DeltaMm / MmPerFoot);
                startMove = crossMove + along * (s.DeltaMm / MmPerFoot);
                endMove = crossMove + along * (e.DeltaMm / MmPerFoot);
            }

            plan.ShiftMm = Math.Max(startMove.GetLength(), endMove.GetLength()) * MmPerFoot;
            if (startMove.IsAlmostEqualTo(endMove, 1e-7))
            {
                plan.Move = startMove;
            }
            else
            {
                plan.NewStart = start + startMove;
                plan.NewEnd = end + endMove;
                if (plan.NewStart.DistanceTo(plan.NewEnd) < 0.01)
                {
                    plan.Skip = "snapping would collapse the element";
                    plan.NewStart = plan.NewEnd = null;
                }
            }
            return plan;
        }

        // ---------------------------------------------------------------- applying

        /// <summary>Moves plans with a pure translation, one MoveElements call per identical delta.</summary>
        private static void ApplyTranslations(Document doc, List<Plan> plans, Dictionary<string, int> applied, List<object> failed)
        {
            foreach (var group in plans.GroupBy(p => Key(p.Move)))
            {
                var list = group.ToList();
                var ids = list.Select(p => p.Element.Id).ToList();
                var sub = new SubTransaction(doc);
                sub.Start();
                try
                {
                    ElementTransformUtils.MoveElements(doc, ids, list[0].Move);
                    sub.Commit();
                    foreach (var plan in list) Count(applied, plan.Category);
                    continue;
                }
                catch (Exception)
                {
                    if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                }

                // One blocked element must not stop the rest of the batch.
                foreach (var plan in list)
                {
                    var single = new SubTransaction(doc);
                    single.Start();
                    try
                    {
                        ElementTransformUtils.MoveElement(doc, plan.Element.Id, plan.Move);
                        single.Commit();
                        Count(applied, plan.Category);
                    }
                    catch (Exception ex)
                    {
                        if (single.HasStarted() && !single.HasEnded()) single.RollBack();
                        failed.Add(new { elementId = plan.Element.Id.GetValue(), reason = ex.Message });
                    }
                }
            }
        }

        private static string Key(XYZ v) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F4}|{1:F4}|{2:F4}",
                v.X * MmPerFoot, v.Y * MmPerFoot, v.Z * MmPerFoot);

        private static void Count(Dictionary<string, int> counts, string key) =>
            counts[key] = (counts.TryGetValue(key, out var n) ? n : 0) + 1;

        private static JObject Describe(Plan plan) =>
            new JObject
            {
                ["elementId"] = plan.Element.Id.GetValue(),
                ["shiftMm"] = Math.Round(plan.ShiftMm, 2),
                ["axes"] = new JArray(plan.Axes.Select(a => a.NoReference
                    ? new JObject { ["axis"] = a.Axis, ["grid"] = null }
                    : new JObject
                    {
                        ["axis"] = a.Axis,
                        ["grid"] = a.Grid,
                        ["offsetMm"] = a.OffsetMm,
                        ["snappedMm"] = a.SnappedMm,
                        ["deltaMm"] = a.TooLarge ? (JToken)"too large" : a.DeltaMm
                    }))
            };
    }
}
