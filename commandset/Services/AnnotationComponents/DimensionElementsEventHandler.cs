using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     dimension_elements: drafts the dimensions of a column / shear wall layout plan.
    ///     Columns and footings: per local axis, a chain from face to face through the nearest
    ///     parallel grid (e.g. 550 | 550 from grid, or the size alone). Walls: collinear pieces of the
    ///     same thickness joined end to end are merged into runs; each run gets its thickness once
    ///     (with the nearest parallel grid) and its length once, read to the outer face of a
    ///     perpendicular wall at corners (near face at a T). Dimension lines sit a paper distance
    ///     outside the element on the chosen side.
    ///     action "retype": changes the DimensionType of existing linear dimensions in views.
    /// </summary>
    public class DimensionElementsEventHandler : JsonParameterEventHandler
    {
        private const double Mm = 1 / 304.8;
        private const double Tol = 5 * Mm;

        public override string GetName() => "Dimension Elements";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject p)
        {
            var doc = uiDoc.Document;
            var action = (p.Value<string>("action") ?? "dimension").ToLowerInvariant();
            if (action == "retype") return Retype(uiDoc, p);
            if (action != "dimension") throw new ArgumentException($"Unknown action '{action}' (dimension or retype).");

            var viewId = p.Value<long?>("viewId");
            var view = viewId.HasValue ? doc.GetElement(viewId.Value.ToRevitElementId()) as View : uiDoc.ActiveView;
            if (!(view is ViewPlan plan) || view.IsTemplate)
                throw new ArgumentException("dimension_elements needs a plan view (viewId).");

            var gridDistance = (p.Value<double?>("maxGridDistanceMm") ?? 3000) * Mm;
            var offset = (p.Value<double?>("offsetPaperMm") ?? 6) * view.Scale * Mm;
            var wallLengthMin = (p.Value<double?>("minWallLengthMm") ?? 1500) * Mm;
            var toGrids = p.Value<bool?>("toGrids") ?? true;
            var mergeRuns = p.Value<bool?>("wallRuns") ?? true;
            var side = (p.Value<string>("side") ?? "bottomLeft").ToLowerInvariant();
            var replace = p.Value<bool?>("replaceExisting") ?? false;
            var cats = (p["categories"] as JArray)?.Select(t => t.Value<string>()).ToList()
                       ?? new List<string> { "StructuralColumns", "Walls" };
            var wallParts = (p["wallDimensions"] as JArray)?.Select(t => t.Value<string>().ToLowerInvariant()).ToList()
                            ?? new List<string> { "thickness", "length" };
            var typeFilter = Strings(p["typeNameContains"]);
            var familyFilter = Strings(p["familyNameContains"]);

            DimensionType dimType = null;
            var typeName = p.Value<string>("dimensionType");
            if (!string.IsNullOrWhiteSpace(typeName))
                dimType = FindLinearType(doc, typeName) ?? throw new ArgumentException($"Dimension type '{typeName}' not found.");

            var z = plan.GenLevel?.Elevation ?? 0;
            var grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().Where(g => g.Curve is Line).ToList();
            var ids = (p["elementIds"] as JArray)?.Select(t => t.Value<long>()).ToHashSet();
            var bics = cats.Select(ParseCategory).Distinct().ToList();

            bool Selected(Element e) =>
                (ids == null || ids.Contains(e.Id.GetValue())) && PassesNameFilters(doc, e, typeFilter, familyFilter);

            var elements = new List<Element>();
            foreach (var bic in bics)
                foreach (var e in new FilteredElementCollector(doc, view.Id).OfCategory(bic).WhereElementIsNotElementType())
                    if (Selected(e))
                        elements.Add(e);

            var warnings = new List<string>();
            int columns = 0, walls = 0, runs = 0, created = 0, removed = 0;
            using (var t = new Transaction(doc, "MCP: Dimension Elements"))
            {
                t.Start();
                if (replace)
                {
                    // Every element of the selected categories passing the filters, visible or not, so
                    // dimensions of elements skipped or merged on an earlier run are removed too.
                    var targets = new HashSet<long>();
                    foreach (var bic in bics)
                        foreach (var e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
                            if (Selected(e))
                                targets.Add(e.Id.GetValue());
                    var old = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Dimension)).Cast<Dimension>()
                        .Where(d => References(d).Any(id => targets.Contains(id)))
                        .Select(d => d.Id).ToList();
                    if (old.Count > 0) removed = doc.Delete(old).Count;
                }

                foreach (var e in elements)
                {
                    if (e is Wall) continue;
                    try
                    {
                        if (e is FamilyInstance fi && fi.Location is LocationPoint)
                        {
                            created += DimensionPointElement(doc, view, fi, z, grids, toGrids, gridDistance, offset, side, dimType, warnings);
                            columns++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Warn(warnings, $"{e.Id.GetValue()}: {ex.Message}");
                    }
                }

                var selectedWalls = elements.OfType<Wall>().Select(w => WallInfo.From(w, z)).Where(w => w != null).ToList();
                walls = selectedWalls.Count;
                if (selectedWalls.Count > 0)
                {
                    var wallRuns = BuildRuns(selectedWalls, mergeRuns);
                    // Every straight wall in the view (any type) can terminate a run.
                    var allWalls = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Wall)).Cast<Wall>()
                        .Select(w => WallInfo.From(w, z)).Where(w => w != null).ToList();
                    var allRuns = BuildRuns(allWalls, true);
                    foreach (var run in wallRuns)
                    {
                        try
                        {
                            created += DimensionRun(doc, view, run, allRuns, z, grids, toGrids, gridDistance, offset, wallLengthMin, wallParts, dimType, warnings);
                            runs++;
                        }
                        catch (Exception ex)
                        {
                            Warn(warnings, $"{run.Label}: {ex.Message}");
                        }
                    }
                }

                t.Commit();
            }

            var result = new JObject
            {
                ["viewId"] = view.Id.GetValue(),
                ["pointElements"] = columns,
                ["walls"] = walls,
                ["wallRuns"] = runs,
                ["dimensionsCreated"] = created,
                ["dimensionsReplaced"] = removed
            };
            if (warnings.Count > 0) result["warnings"] = new JArray(warnings);
            return Ok($"Created {created} dimensions for {columns} columns/footings and {walls} walls in {runs} runs; removed {removed} old dimensions.", result);
        }

        // ---------------------------------------------------------------- retype

        private AIResult<object> Retype(UIDocument uiDoc, JObject p)
        {
            var doc = uiDoc.Document;
            var typeName = p.Value<string>("dimensionType");
            if (string.IsNullOrWhiteSpace(typeName))
                throw new ArgumentException("action 'retype' needs dimensionType.");
            var target = FindLinearType(doc, typeName);
            if (target == null || target.StyleType != DimensionStyleType.Linear)
                throw new ArgumentException($"Linear dimension type '{typeName}' not found.");
            var fromTypes = Strings(p["onlyFromTypes"]);

            var viewIds = (p["viewIds"] as JArray)?.Select(t => t.Value<long>()).ToList();
            if (viewIds == null || viewIds.Count == 0)
            {
                var single = p.Value<long?>("viewId");
                viewIds = new List<long> { single ?? uiDoc.ActiveView.Id.GetValue() };
            }

            var warnings = new List<string>();
            var perView = new JArray();
            int changed = 0, unchanged = 0;
            using (var t = new Transaction(doc, "MCP: Retype Dimensions"))
            {
                t.Start();
                foreach (var vid in viewIds.Distinct())
                {
                    var view = doc.GetElement(vid.ToRevitElementId()) as View;
                    if (view == null || view.IsTemplate)
                    {
                        Warn(warnings, $"{vid}: not a view.");
                        continue;
                    }

                    int c = 0, u = 0;
                    var dims = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Dimension)).Cast<Dimension>()
                        .Where(d => !(d is SpotDimension) && d.OwnerViewId == view.Id).ToList();
                    foreach (var d in dims)
                    {
                        var current = d.DimensionType;
                        if (current == null || current.StyleType != DimensionStyleType.Linear) continue;
                        if (current.Id == target.Id)
                        {
                            u++;
                            continue;
                        }

                        if (fromTypes.Count > 0 && !fromTypes.Any(f => string.Equals(f, current.Name, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        try
                        {
                            d.ChangeTypeId(target.Id);
                            c++;
                        }
                        catch (Exception ex)
                        {
                            Warn(warnings, $"{d.Id.GetValue()}: {ex.Message}");
                        }
                    }

                    perView.Add(new JObject { ["viewId"] = vid, ["viewName"] = view.Name, ["changed"] = c, ["alreadyTarget"] = u });
                    changed += c;
                    unchanged += u;
                }

                t.Commit();
            }

            var result = new JObject
            {
                ["dimensionType"] = target.Name,
                ["changed"] = changed,
                ["alreadyTarget"] = unchanged,
                ["views"] = perView
            };
            if (warnings.Count > 0) result["warnings"] = new JArray(warnings);
            return Ok($"Retyped {changed} linear dimensions to '{target.Name}' in {perView.Count} views.", result);
        }

        // ---------------------------------------------------------------- helpers

        private static void Warn(List<string> warnings, string message)
        {
            if (warnings.Count < 50) warnings.Add(message);
        }

        private static List<string> Strings(JToken token) =>
            (token as JArray)?.Select(t => t.Value<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList()
            ?? new List<string>();

        /// <summary>Dimension type by name, preferring linear styles when names repeat across styles.</summary>
        private static DimensionType FindLinearType(Document doc, string name) =>
            new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                .Where(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.StyleType == DimensionStyleType.Linear ? 0 : 1)
                .FirstOrDefault();

        private static bool PassesNameFilters(Document doc, Element e, List<string> typeFilter, List<string> familyFilter)
        {
            if (typeFilter.Count == 0 && familyFilter.Count == 0) return true;
            var type = doc.GetElement(e.GetTypeId()) as ElementType;
            if (typeFilter.Count > 0)
            {
                var name = type?.Name ?? e.Name ?? "";
                if (!typeFilter.Any(f => name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)) return false;
            }

            if (familyFilter.Count > 0)
            {
                var family = type?.FamilyName ?? (e as FamilyInstance)?.Symbol?.FamilyName ?? "";
                if (!familyFilter.Any(f => family.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)) return false;
            }

            return true;
        }

        private static IEnumerable<long> References(Dimension d)
        {
            ReferenceArray refs;
            try
            {
                refs = d.References;
            }
            catch
            {
                yield break;
            }

            if (refs == null) yield break;
            foreach (Reference r in refs)
                if (r != null)
                    yield return r.ElementId.GetValue();
        }

        private static BuiltInCategory ParseCategory(string name)
        {
            var s = name.Trim().Replace(" ", "");
            if (!s.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)) s = "OST_" + s;
            if (Enum.TryParse(s, true, out BuiltInCategory bic)) return bic;
            throw new ArgumentException($"Unknown category '{name}'.");
        }

        /// <summary>Vertical planar faces with references (instance geometry included).</summary>
        private static List<PlanarFace> SideFaces(Element e, View view)
        {
            var faces = new List<PlanarFace>();
            var opt = new Options { ComputeReferences = true, View = view };
            foreach (var go in e.get_Geometry(opt))
            {
                if (go is Solid s) Collect(s, faces);
                else if (go is GeometryInstance gi)
                    foreach (var g2 in gi.GetInstanceGeometry())
                        if (g2 is Solid s2) Collect(s2, faces);
            }

            return faces;
        }

        private static void Collect(Solid s, List<PlanarFace> faces)
        {
            foreach (Face f in s.Faces)
                if (f is PlanarFace pf && pf.Reference != null && Math.Abs(pf.FaceNormal.Z) < 1e-3)
                    faces.Add(pf);
        }

        /// <summary>Nearest grid perpendicular to <paramref name="d" /> (its line runs across d) within reach.</summary>
        private static Grid NearestGrid(List<Grid> grids, XYZ d, double at, double reach, out double gridAt)
        {
            Grid best = null;
            gridAt = 0;
            var bestDist = reach;
            foreach (var g in grids)
            {
                var gl = (Line)g.Curve;
                var gd = new XYZ(gl.Direction.X, gl.Direction.Y, 0).Normalize();
                if (Math.Abs(gd.DotProduct(d)) > 1e-3) continue;
                var s = new XYZ(gl.Origin.X, gl.Origin.Y, 0).DotProduct(d);
                if (Math.Abs(s - at) < bestDist)
                {
                    bestDist = Math.Abs(s - at);
                    best = g;
                    gridAt = s;
                }
            }

            return best;
        }

        private static int DimensionPointElement(Document doc, View view, FamilyInstance fi, double z, List<Grid> grids,
            bool toGrids, double reach, double offset, string side, DimensionType dimType, List<string> warnings)
        {
            var faces = SideFaces(fi, view);
            if (faces.Count == 0)
            {
                Warn(warnings, $"{fi.Id.GetValue()}: no face references in this view.");
                return 0;
            }

            var lp = ((LocationPoint)fi.Location).Point;
            var center = new XYZ(lp.X, lp.Y, z);
            var tr = fi.GetTransform();
            var made = 0;
            foreach (var axis in new[] { tr.BasisX, tr.BasisY })
            {
                var d = new XYZ(axis.X, axis.Y, 0).Normalize();
                if (d.X < -1e-6 || (Math.Abs(d.X) < 1e-6 && d.Y < 0)) d = d.Negate();
                var along = faces.Where(f => Math.Abs(Math.Abs(f.FaceNormal.DotProduct(d)) - 1) < 1e-3)
                    .OrderBy(f => f.Origin.DotProduct(d)).ToList();
                if (along.Count < 2) continue;

                var refs = new List<Tuple<double, Reference>>
                {
                    Tuple.Create(along.First().Origin.DotProduct(d), along.First().Reference),
                    Tuple.Create(along.Last().Origin.DotProduct(d), along.Last().Reference)
                };
                if (toGrids)
                {
                    var g = NearestGrid(grids, d, center.DotProduct(d), reach, out var gAt);
                    if (g != null && refs.All(r => Math.Abs(r.Item1 - gAt) > Mm))
                        refs.Add(Tuple.Create(gAt, new Reference(g)));
                }

                var perp = Side(d, side);
                var ext = faces.Max(f => (f.Origin - center).DotProduct(perp));
                var basePt = center + perp * (ext + offset);
                made += Create(doc, view, basePt - d * 3000 * Mm, basePt + d * 3000 * Mm, refs, dimType, fi, warnings);
            }

            return made;
        }

        // ---------------------------------------------------------------- walls

        /// <summary>A straight wall in plan: canonical direction U, normal N, centre-line offset C along N, span S0..S1 along U.</summary>
        private class WallInfo
        {
            public Wall Wall;
            public XYZ U, N;
            public double C, Width, S0, S1;
            public List<Tuple<XYZ, Reference>> Sides = new List<Tuple<XYZ, Reference>>();

            public static WallInfo From(Wall w, double z)
            {
                if (!(w.Location is LocationCurve loc) || !(loc.Curve is Line lc)) return null;
                var u = new XYZ(lc.Direction.X, lc.Direction.Y, 0);
                if (u.GetLength() < 1e-9) return null;
                u = u.Normalize();
                if (u.X < -1e-6 || (Math.Abs(u.X) < 1e-6 && u.Y < 0)) u = u.Negate();
                var n = new XYZ(-u.Y, u.X, 0);
                var a = new XYZ(lc.GetEndPoint(0).X, lc.GetEndPoint(0).Y, z);
                var b = new XYZ(lc.GetEndPoint(1).X, lc.GetEndPoint(1).Y, z);
                var info = new WallInfo
                {
                    Wall = w, U = u, N = n,
                    S0 = Math.Min(a.DotProduct(u), b.DotProduct(u)),
                    S1 = Math.Max(a.DotProduct(u), b.DotProduct(u)),
                    C = a.DotProduct(n), Width = w.Width
                };

                // Side faces come from HostObjectUtils: view-specific wall geometry has no face references.
                try
                {
                    foreach (var shell in new[] { ShellLayerType.Exterior, ShellLayerType.Interior })
                    foreach (var r in HostObjectUtils.GetSideFaces(w, shell))
                        if (w.GetGeometryObjectFromReference(r) is PlanarFace pf && Math.Abs(Math.Abs(pf.FaceNormal.DotProduct(n)) - 1) < 1e-3)
                            info.Sides.Add(Tuple.Create(pf.Origin, r));
                }
                catch
                {
                    // Curtain / stacked walls without side faces: centre from the location line.
                }

                if (info.Sides.Count >= 2)
                {
                    var lo = info.Sides.Min(s => s.Item1.DotProduct(n));
                    var hi = info.Sides.Max(s => s.Item1.DotProduct(n));
                    info.C = (lo + hi) / 2;
                    info.Width = hi - lo;
                }

                return info;
            }
        }

        private class WallRun
        {
            public List<WallInfo> Pieces = new List<WallInfo>();
            public XYZ U, N;
            public double C, Width, SMin, SMax;

            public string Label => Pieces.Count == 1
                ? Pieces[0].Wall.Id.GetValue().ToString()
                : "run " + string.Join("+", Pieces.Select(w => w.Wall.Id.GetValue()));

            public XYZ At(double s, double c, double z) => new XYZ(U.X * s + N.X * c, U.Y * s + N.Y * c, z);
        }

        /// <summary>Union collinear, equally thick pieces whose spans touch or overlap.</summary>
        private static List<WallRun> BuildRuns(List<WallInfo> walls, bool merge)
        {
            var parent = Enumerable.Range(0, walls.Count).ToArray();
            int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
            if (merge)
                for (var i = 0; i < walls.Count; i++)
                for (var j = i + 1; j < walls.Count; j++)
                {
                    WallInfo a = walls[i], b = walls[j];
                    if (a.U.DotProduct(b.U) < 1 - 1e-6) continue;
                    if (Math.Abs(a.C - b.C) > Tol || Math.Abs(a.Width - b.Width) > Mm) continue;
                    if (a.S0 > b.S1 + Tol || b.S0 > a.S1 + Tol) continue;
                    parent[Find(i)] = Find(j);
                }

            return walls.Select((w, i) => new { w, root = Find(i) })
                .GroupBy(x => x.root)
                .Select(g =>
                {
                    var pieces = g.Select(x => x.w).OrderBy(w => w.S0).ToList();
                    return new WallRun
                    {
                        Pieces = pieces, U = pieces[0].U, N = pieces[0].N,
                        C = pieces.Average(w => w.C), Width = pieces[0].Width,
                        SMin = pieces.Min(w => w.S0), SMax = pieces.Max(w => w.S1)
                    };
                })
                .OrderBy(r => r.Pieces[0].Wall.Id.GetValue())
                .ToList();
        }

        private static int DimensionRun(Document doc, View view, WallRun run, List<WallRun> allRuns, double z, List<Grid> grids,
            bool toGrids, double reach, double offset, double minLength, List<string> parts, DimensionType dimType, List<string> warnings)
        {
            var made = 0;
            var owner = run.Pieces[0].Wall;

            if (parts.Contains("thickness"))
            {
                var piece = run.Pieces.Where(w => w.Sides.Count >= 2).OrderByDescending(w => w.S1 - w.S0).FirstOrDefault();
                if (piece != null)
                {
                    var ordered = piece.Sides.Select(s => Tuple.Create(s.Item1.DotProduct(run.N), s.Item2)).OrderBy(s => s.Item1).ToList();
                    var refs = new List<Tuple<double, Reference>> { ordered.First(), ordered.Last() };
                    if (toGrids)
                    {
                        var g = NearestGrid(grids, run.N, run.C, reach, out var gAt);
                        if (g != null && refs.All(r => Math.Abs(r.Item1 - gAt) > Mm))
                            refs.Add(Tuple.Create(gAt, new Reference(g)));
                    }

                    var len = piece.S1 - piece.S0;
                    var at = run.At((piece.S0 + piece.S1) / 2 + Math.Min(len * 0.25, 1500 * Mm), run.C, z);
                    made += Create(doc, view, at - run.N * 1500 * Mm, at + run.N * 1500 * Mm, refs, dimType, piece.Wall, warnings);
                }
                else
                {
                    Warn(warnings, $"{run.Label}: no wall side faces; thickness not dimensioned.");
                }
            }

            if (parts.Contains("length") && run.SMax - run.SMin >= minLength)
            {
                var start = EndReference(run, -1, allRuns, z);
                var end = EndReference(run, +1, allRuns, z);
                if (start != null && end != null && end.Item1 - start.Item1 > Mm)
                {
                    var refs = new List<Tuple<double, Reference>> { start, end };
                    var c = run.C - (run.Width / 2 + offset);
                    made += Create(doc, view, run.At(start.Item1 - 500 * Mm, c, z), run.At(end.Item1 + 500 * Mm, c, z), refs, dimType, owner, warnings);
                }
                else
                {
                    Warn(warnings, $"{run.Label}: {(start == null ? "start" : "end")} of the wall has no end face or perpendicular wall; length not dimensioned.");
                }
            }

            return made;
        }

        /// <summary>
        ///     Reference for one end of a run (sign -1 start, +1 end) with its position along the run:
        ///     a perpendicular wall through the end point gives its outer side face at a corner (L) or its
        ///     near side face at a T; otherwise the extreme piece's own end face.
        /// </summary>
        private static Tuple<double, Reference> EndReference(WallRun run, int sign, List<WallRun> allRuns, double z)
        {
            var sEnd = sign < 0 ? run.SMin : run.SMax;
            var p = run.At(sEnd, run.C, z);
            var half = run.Width / 2;

            WallRun best = null;
            var bestDist = double.MaxValue;
            foreach (var q in allRuns)
            {
                if (Math.Abs(q.U.DotProduct(run.U)) > 0.02) continue;
                var dist = Math.Abs(p.DotProduct(q.N) - q.C);
                if (dist > q.Width / 2 + Tol) continue;
                var t = p.DotProduct(q.U);
                if (t < q.SMin - half - Tol || t > q.SMax + half + Tol) continue;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = q;
                }
            }

            if (best != null)
            {
                var t = p.DotProduct(best.U);
                var isTee = t - best.SMin > half + Tol && best.SMax - t > half + Tol;
                // Side faces of the piece of the perpendicular run nearest the end point.
                var piece = best.Pieces.Where(w => w.Sides.Count >= 2)
                    .OrderBy(w => t < w.S0 ? w.S0 - t : t > w.S1 ? t - w.S1 : 0).FirstOrDefault();
                if (piece != null)
                {
                    var faces = piece.Sides.Select(s => Tuple.Create(s.Item1.DotProduct(run.U), s.Item2)).ToList();
                    return isTee
                        ? faces.OrderBy(f => sign * f.Item1).First()
                        : faces.OrderByDescending(f => sign * f.Item1).First();
                }
            }

            // Free end: the extreme piece's own end face near the end point.
            var extreme = sign < 0 ? run.Pieces.OrderBy(w => w.S0).First() : run.Pieces.OrderByDescending(w => w.S1).First();
            Tuple<double, Reference> found = null;
            foreach (var go in extreme.Wall.get_Geometry(new Options { ComputeReferences = true }))
                if (go is Solid s)
                    foreach (Face f in s.Faces)
                        if (f is PlanarFace pf && pf.Reference != null && Math.Abs(Math.Abs(pf.FaceNormal.DotProduct(run.U)) - 1) < 1e-3)
                        {
                            var pos = pf.Origin.DotProduct(run.U);
                            if (Math.Abs(pos - sEnd) > run.Width + Tol) continue;
                            if (found == null || sign * pos > sign * found.Item1) found = Tuple.Create(pos, pf.Reference);
                        }

            return found;
        }

        /// <summary>Perpendicular to d on the requested side of the element.</summary>
        private static XYZ Side(XYZ d, string side)
        {
            var perp = new XYZ(-d.Y, d.X, 0);
            var wantNegative = side == "bottomleft" || side == "bottom-left";
            var isNegative = perp.Y < -1e-6 || (Math.Abs(perp.Y) < 1e-6 && perp.X < 0);
            return wantNegative == isNegative ? perp : perp.Negate();
        }

        private static int Create(Document doc, View view, XYZ p0, XYZ p1, List<Tuple<double, Reference>> refs,
            DimensionType dimType, Element e, List<string> warnings)
        {
            var ra = new ReferenceArray();
            foreach (var r in refs.OrderBy(r => r.Item1)) ra.Append(r.Item2);
            try
            {
                var line = Line.CreateBound(p0, p1);
                var dim = dimType != null ? doc.Create.NewDimension(view, line, ra, dimType) : doc.Create.NewDimension(view, line, ra);
                return dim != null ? 1 : 0;
            }
            catch (Exception ex)
            {
                Warn(warnings, $"{e.Id.GetValue()}: {ex.Message}");
                return 0;
            }
        }
    }
}
