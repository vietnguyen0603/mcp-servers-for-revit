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
    ///     Walls follow wallMode: "run" (default) groups joined / touching collinear walls into
    ///     runs that move across together, then rebuilds each joined end from the snapped lines
    ///     so corners and T-junctions stay closed; free ends snap along. "acrossOnly" shifts each
    ///     wall perpendicular to itself only; "ends" is the per-wall behaviour of beams.
    /// </summary>
    public class SnapToGridEventHandler : JsonParameterEventHandler
    {
        private const double MmPerFoot = 304.8;
        private const double AlignedToleranceMm = 0.01;
        private const int SampleSize = 15;
        private const double RunToleranceMm = 5.0;
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
            public int RunSize; // walls in the run (wallMode "run")
            public readonly List<string> Notes = new List<string>();
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
            public string WallMode = "run";
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
                linePlans = PlanLines(lineElements, settings);
            }
            else
            {
                using (var transaction = ModelSelectionUtils.StartTransaction(doc, "MCP: Snap To Grid", warnings))
                {
                    ApplyTranslations(doc, pointPlans.Where(p => p.Moves).ToList(), applied, failed);
                    doc.Regenerate();

                    // Line elements are evaluated after the columns moved: joined beam ends may have followed them.
                    linePlans = PlanLines(lineElements, settings);
                    var moving = linePlans.Where(p => p.Moves).ToList();
                    var translations = moving.Where(p => p.NewStart == null).ToList();
                    ApplyTranslations(doc, translations, applied, failed);
                    var reshaped = new List<Plan>();
                    foreach (var plan in moving.Where(p => p.NewStart != null))
                    {
                        try
                        {
                            ((LocationCurve)plan.Element.Location).Curve = Line.CreateBound(plan.NewStart, plan.NewEnd);
                            Count(applied, plan.Category);
                            lineMoveMode++;
                            reshaped.Add(plan);
                        }
                        catch (Exception ex)
                        {
                            failed.Add(new { elementId = plan.Element.Id.GetValue(), reason = ex.Message });
                        }
                    }
                    RestoreJoinedEnds(doc, reshaped, warnings);

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
                ["wallMode"] = settings.WallMode,
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

            var wallMode = parameters.Value<string>("wallMode")?.Trim();
            if (!string.IsNullOrEmpty(wallMode))
            {
                if (string.Equals(wallMode, "run", StringComparison.OrdinalIgnoreCase)) settings.WallMode = "run";
                else if (string.Equals(wallMode, "acrossOnly", StringComparison.OrdinalIgnoreCase)) settings.WallMode = "acrossOnly";
                else if (string.Equals(wallMode, "ends", StringComparison.OrdinalIgnoreCase)) settings.WallMode = "ends";
                else throw new ArgumentException($"Invalid wallMode '{wallMode}' (run, acrossOnly, ends).");
            }

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

        /// <summary>Plans for line elements: walls by wallMode, everything else per element.</summary>
        private static List<Plan> PlanLines(List<Element> elements, Settings settings)
        {
            if (settings.WallMode == "ends")
                return elements.Select(e => PlanLine(e, settings, false)).ToList();
            var walls = elements.Where(e => e is Wall).ToList();
            var plans = settings.WallMode == "acrossOnly"
                ? walls.Select(e => PlanLine(e, settings, true)).ToList()
                : PlanWallRuns(walls, settings);
            plans.AddRange(elements.Where(e => !(e is Wall)).Select(e => PlanLine(e, settings, false)));
            return plans;
        }

        private static Plan PlanLine(Element element, Settings settings, bool acrossOnly)
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
            if (acrossOnly)
            {
                // Whole-element shift perpendicular to the line (relative to the grid parallel to it, or the origin)
                var along = flat.Normalize();
                var normal = new XYZ(-along.Y, along.X, 0);
                var cross = SnapAxis((start + end) / 2, normal, "across", settings);
                plan.Axes.Add(cross);
                startMove = endMove = normal * (cross.DeltaMm / MmPerFoot);
            }
            else if (settings.Global)
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

        private static JObject Describe(Plan plan)
        {
            var entry = new JObject
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
            if (plan.RunSize > 0) entry["runWalls"] = plan.RunSize;
            if (plan.Notes.Count > 0) entry["joinedEnds"] = new JArray(plan.Notes);
            return entry;
        }

        // ---------------------------------------------------------------- wall runs

        private class WallLine
        {
            public Plan Plan;
            public XYZ Start, End; // original location line ends
            public XYZ Dir; // unit plan direction
            public double Length; // plan length
            public RunLine Run;
            public readonly List<Element>[] Joined = { new List<Element>(), new List<Element>() };
            public readonly List<WallLine>[] Touching = { new List<WallLine>(), new List<WallLine>() };
            public XYZ EndPoint(int end) => end == 0 ? Start : End;
        }

        private class RunLine
        {
            public XYZ Point; // a point on the snapped run line (z = 0)
            public XYZ Dir;
            public AxisResult Cross;
            public int Count;
        }

        private static XYZ Flat(XYZ p) => new XYZ(p.X, p.Y, 0);

        private static double Cross2(XYZ a, XYZ b) => a.X * b.Y - a.Y * b.X;

        /// <summary>
        ///     wallMode "run": walls joined or touching end-to-end and collinear (within RunToleranceMm) form a
        ///     run. Each run snaps across once (length-weighted mean line), every wall is laid on the snapped
        ///     run line, ends joined to a non-parallel wall are rebuilt from the other wall's snapped line
        ///     (keeping their original distance to it, 0 at a corner), ends shared inside a run meet at their
        ///     common point, and free ends snap along like beams.
        /// </summary>
        private static List<Plan> PlanWallRuns(List<Element> walls, Settings settings)
        {
            var tolerance = RunToleranceMm / MmPerFoot;
            var plans = new List<Plan>();
            var lines = new List<WallLine>();
            foreach (var wall in walls)
            {
                var plan = NewPlan(wall);
                plan.IsLine = true;
                plans.Add(plan);
                if (plan.Skip != null) continue;
                var location = (LocationCurve)wall.Location;
                if (!(location.Curve is Line line))
                {
                    plan.Skip = "curved location line";
                    continue;
                }

                var start = line.GetEndPoint(0);
                var end = line.GetEndPoint(1);
                var flat = new XYZ(end.X - start.X, end.Y - start.Y, 0);
                if (flat.GetLength() < 1e-6)
                {
                    plan.Skip = "vertical location line";
                    continue;
                }

                var info = new WallLine { Plan = plan, Start = start, End = end, Dir = flat.Normalize(), Length = flat.GetLength() };
                for (var i = 0; i < 2; i++)
                {
                    try
                    {
                        foreach (Element other in location.get_ElementsAtJoin(i))
                            if (other != null && other.Id != wall.Id && info.Joined[i].All(o => o.Id != other.Id))
                                info.Joined[i].Add(other);
                    }
                    catch (Exception)
                    {
                        // no join information; coincident end points below still connect the walls
                    }
                }
                lines.Add(info);
            }

            var byId = lines.ToDictionary(l => l.Plan.Element.Id.GetValue());

            // Ends within tolerance of each other (sweep along X)
            var ends = lines.SelectMany(l => new[] { (Wall: l, End: 0), (Wall: l, End: 1) })
                .OrderBy(t => t.Wall.EndPoint(t.End).X).ToList();
            for (var i = 0; i < ends.Count; i++)
            {
                var p = ends[i].Wall.EndPoint(ends[i].End);
                for (var j = i + 1; j < ends.Count; j++)
                {
                    var q = ends[j].Wall.EndPoint(ends[j].End);
                    if (q.X - p.X > tolerance) break;
                    if (ends[i].Wall == ends[j].Wall || p.DistanceTo(q) > tolerance) continue;
                    ends[i].Wall.Touching[ends[i].End].Add(ends[j].Wall);
                    ends[j].Wall.Touching[ends[j].End].Add(ends[i].Wall);
                }
            }

            // Runs: union of connected collinear walls
            var index = new Dictionary<WallLine, int>();
            for (var i = 0; i < lines.Count; i++) index[lines[i]] = i;
            var parent = Enumerable.Range(0, lines.Count).ToArray();

            int Find(int x)
            {
                while (parent[x] != x) x = parent[x] = parent[parent[x]];
                return x;
            }

            foreach (var line in lines)
                for (var end = 0; end < 2; end++)
                    foreach (var other in Neighbours(line, end, byId))
                        if (Collinear(line, other, tolerance))
                            parent[Find(index[line])] = Find(index[other]);

            foreach (var group in lines.GroupBy(l => Find(index[l])))
            {
                var members = group.ToList();
                var reference = members.OrderByDescending(m => m.Length).ThenBy(m => m.Plan.Element.Id.GetValue()).First();
                var u = reference.Dir;
                var n = new XYZ(-u.Y, u.X, 0);
                var p0 = Flat((reference.Start + reference.End) / 2);
                var mean = members.Sum(m => m.Length * (Flat((m.Start + m.End) / 2) - p0).DotProduct(n)) / members.Sum(m => m.Length);
                var runPoint = p0 + n * mean;
                var cross = SnapAxis(runPoint, n, "across", settings);
                var run = new RunLine { Point = runPoint + n * (cross.DeltaMm / MmPerFoot), Dir = u, Cross = cross, Count = members.Count };
                foreach (var member in members) member.Run = run;
            }

            foreach (var line in lines)
                PlanRunWall(line, byId, settings, tolerance);
            return plans;
        }

        private static IEnumerable<WallLine> Neighbours(WallLine line, int end, Dictionary<long, WallLine> byId) =>
            line.Joined[end].Select(e => byId.TryGetValue(e.Id.GetValue(), out var w) ? w : null).Where(w => w != null)
                .Concat(line.Touching[end]).Distinct();

        private static bool Collinear(WallLine a, WallLine b, double tolerance)
        {
            if (Math.Abs(Cross2(a.Dir, b.Dir)) > ParallelTolerance) return false;
            var n = new XYZ(-a.Dir.Y, a.Dir.X, 0);
            var origin = Flat(a.Start);
            return Math.Abs((Flat(b.Start) - origin).DotProduct(n)) <= tolerance &&
                   Math.Abs((Flat(b.End) - origin).DotProduct(n)) <= tolerance;
        }

        private static XYZ OnRun(RunLine run, XYZ p)
        {
            var t = (Flat(p) - run.Point).DotProduct(run.Dir);
            var q = run.Point + run.Dir * t;
            return new XYZ(q.X, q.Y, p.Z);
        }

        private static void PlanRunWall(WallLine line, Dictionary<long, WallLine> byId, Settings settings, double tolerance)
        {
            var plan = line.Plan;
            var run = line.Run;
            plan.RunSize = run.Count;
            plan.Axes.Add(run.Cross);

            var newEnds = new XYZ[2];
            for (var end = 0; end < 2; end++)
            {
                var p = line.EndPoint(end);
                var label = end == 0 ? "start" : "end";
                var projected = OnRun(run, p);

                var joined = JoinedEnd(line, end, byId, tolerance, out var note);
                if (joined != null)
                {
                    if (joined.DistanceTo(projected) <= 2 * settings.MaxShift + tolerance)
                    {
                        newEnds[end] = joined;
                        plan.Notes.Add($"{label}: {note}");
                    }
                    else
                    {
                        newEnds[end] = projected;
                        plan.Notes.Add($"{label}: {note}; rebuilding it would move the end more than 2 x maxShiftMm, kept along the run");
                    }
                    continue;
                }

                var partner = Neighbours(line, end, byId).FirstOrDefault(w => w.Run == run);
                if (partner != null)
                {
                    // End shared inside a run: both walls meet at the midpoint of their two ends
                    var q = partner.Start.DistanceTo(p) <= partner.End.DistanceTo(p) ? partner.Start : partner.End;
                    newEnds[end] = OnRun(run, new XYZ((p.X + q.X) / 2, (p.Y + q.Y) / 2, p.Z));
                    continue;
                }

                if (line.Joined[end].Count > 0)
                {
                    // Joined to something that is not a straight non-parallel wall: keep the end, only lay it on the run
                    newEnds[end] = projected;
                    continue;
                }

                var along = SnapAxis(projected, run.Dir, label, settings);
                plan.Axes.Add(along);
                newEnds[end] = projected + run.Dir * (along.DeltaMm / MmPerFoot);
            }

            plan.ShiftMm = Math.Max(newEnds[0].DistanceTo(line.Start), newEnds[1].DistanceTo(line.End)) * MmPerFoot;
            if (plan.ShiftMm <= AlignedToleranceMm) return;
            if (newEnds[0].DistanceTo(newEnds[1]) < 0.01)
            {
                plan.Skip = "snapping would collapse the element";
                return;
            }
            plan.NewStart = newEnds[0];
            plan.NewEnd = newEnds[1];
        }

        /// <summary>
        ///     New position of an end joined to (or touching) a non-parallel straight wall: the point on this
        ///     wall's snapped run line whose signed distance from the other wall's snapped line equals the
        ///     original one (0 for corners whose location lines meet). The closest such wall wins.
        /// </summary>
        private static XYZ JoinedEnd(WallLine line, int end, Dictionary<long, WallLine> byId, double tolerance, out string note)
        {
            note = null;
            var p = Flat(line.EndPoint(end));
            var others = line.Joined[end].Concat(line.Touching[end].Select(w => w.Plan.Element))
                .GroupBy(e => e.Id.GetValue()).Select(g => g.First());

            XYZ best = null;
            var bestGap = double.MaxValue;
            foreach (var other in others)
            {
                XYZ oldPoint, oldDir, newPoint, newDir;
                if (byId.TryGetValue(other.Id.GetValue(), out var wall))
                {
                    oldPoint = Flat(wall.Start);
                    newPoint = wall.Run.Point;
                    newDir = wall.Run.Dir;
                    oldDir = wall.Dir.DotProduct(newDir) >= 0 ? wall.Dir : wall.Dir.Negate();
                }
                else if (other is Wall && other.Location is LocationCurve location && location.Curve is Line otherLine)
                {
                    var flat = Flat(otherLine.GetEndPoint(1) - otherLine.GetEndPoint(0));
                    if (flat.GetLength() < 1e-6) continue;
                    oldDir = newDir = flat.Normalize();
                    oldPoint = newPoint = Flat(otherLine.GetEndPoint(0));
                }
                else
                {
                    continue;
                }

                if (Math.Abs(Cross2(line.Run.Dir, newDir)) <= ParallelTolerance) continue;
                var gap = (p - oldPoint).DotProduct(new XYZ(-oldDir.Y, oldDir.X, 0));
                if (Math.Abs(gap) >= Math.Abs(bestGap)) continue;

                // Solve (run.Point + run.Dir t - newPoint) . newNormal = gap
                var newNormal = new XYZ(-newDir.Y, newDir.X, 0);
                var t = (gap - (line.Run.Point - newPoint).DotProduct(newNormal)) / line.Run.Dir.DotProduct(newNormal);
                var q = line.Run.Point + line.Run.Dir * t;
                best = new XYZ(q.X, q.Y, line.EndPoint(end).Z);
                bestGap = gap;
                note = Math.Abs(gap) <= tolerance
                    ? $"corner with {other.Id.GetValue()}"
                    : $"joined to {other.Id.GetValue()} ({Math.Round(gap * MmPerFoot, 1)} mm from its line, kept)";
            }
            return best;
        }

        /// <summary>
        ///     Setting one wall's curve can drag the ends of walls joined to it. Re-applies the planned
        ///     lines (up to two more passes) so every reshaped element ends where it was planned.
        /// </summary>
        private static void RestoreJoinedEnds(Document doc, List<Plan> reshaped, List<string> warnings)
        {
            if (reshaped.Count == 0) return;
            List<Plan> drifted = null;
            for (var pass = 0; pass < 3; pass++)
            {
                doc.Regenerate();
                drifted = reshaped.Where(p => !AtPlannedEnds(p)).ToList();
                if (drifted.Count == 0 || pass == 2) break;
                foreach (var plan in drifted)
                {
                    try
                    {
                        ((LocationCurve)plan.Element.Location).Curve = Line.CreateBound(plan.NewStart, plan.NewEnd);
                    }
                    catch (Exception)
                    {
                        // reported below if it stays off
                    }
                }
            }

            if (drifted != null && drifted.Count > 0)
                warnings.Add($"{drifted.Count} reshaped element(s) did not keep the planned ends after rejoining (e.g. {string.Join(", ", drifted.Take(5).Select(p => p.Element.Id.GetValue()))}); check them.");
        }

        private static bool AtPlannedEnds(Plan plan)
        {
            if (!(plan.Element.Location is LocationCurve location) || !(location.Curve is Line line)) return true;
            const double tolerance = 0.01 / MmPerFoot;
            return line.GetEndPoint(0).DistanceTo(plan.NewStart) <= tolerance && line.GetEndPoint(1).DistanceTo(plan.NewEnd) <= tolerance;
        }
    }
}
