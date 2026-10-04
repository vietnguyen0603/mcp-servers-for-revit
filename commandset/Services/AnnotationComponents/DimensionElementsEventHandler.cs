using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     dimension_elements: drafts the dimensions of a column / shear wall layout plan.
    ///     Columns and footings: per local axis, a chain from face to face through the nearest
    ///     parallel grid (e.g. 550 | 550 from grid, or the size alone). Walls: thickness across
    ///     each wall with the nearest parallel grid, and the length of each straight wall.
    ///     Dimension lines sit a paper distance outside the element on the chosen side.
    /// </summary>
    public class DimensionElementsEventHandler : JsonParameterEventHandler
    {
        private const double Mm = 1 / 304.8;

        public override string GetName() => "Dimension Elements";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject p)
        {
            var doc = uiDoc.Document;
            var viewId = p.Value<long?>("viewId");
            var view = viewId.HasValue ? doc.GetElement(new ElementId(viewId.Value)) as View : uiDoc.ActiveView;
            if (!(view is ViewPlan plan) || view.IsTemplate)
                throw new ArgumentException("dimension_elements needs a plan view (viewId).");

            var gridDistance = (p.Value<double?>("maxGridDistanceMm") ?? 3000) * Mm;
            var offset = (p.Value<double?>("offsetPaperMm") ?? 6) * view.Scale * Mm;
            var wallLengthMin = (p.Value<double?>("minWallLengthMm") ?? 1500) * Mm;
            var toGrids = p.Value<bool?>("toGrids") ?? true;
            var side = (p.Value<string>("side") ?? "bottomLeft").ToLowerInvariant();
            var replace = p.Value<bool?>("replaceExisting") ?? false;
            var cats = (p["categories"] as JArray)?.Select(t => t.Value<string>()).ToList()
                       ?? new List<string> { "StructuralColumns", "Walls" };
            var wallParts = (p["wallDimensions"] as JArray)?.Select(t => t.Value<string>().ToLowerInvariant()).ToList()
                            ?? new List<string> { "thickness", "length" };

            DimensionType dimType = null;
            var typeName = p.Value<string>("dimensionType");
            if (!string.IsNullOrWhiteSpace(typeName))
                dimType = new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                              .FirstOrDefault(d => string.Equals(d.Name, typeName, StringComparison.OrdinalIgnoreCase))
                          ?? throw new ArgumentException($"Dimension type '{typeName}' not found.");

            var z = plan.GenLevel?.Elevation ?? 0;
            var grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().Where(g => g.Curve is Line).ToList();
            var ids = (p["elementIds"] as JArray)?.Select(t => t.Value<long>()).ToHashSet();

            var elements = new List<Element>();
            foreach (var c in cats)
            {
                var bic = ParseCategory(c);
                var collector = new FilteredElementCollector(doc, view.Id).OfCategory(bic).WhereElementIsNotElementType();
                foreach (var e in collector)
                    if (ids == null || ids.Contains(e.Id.GetValue()))
                        elements.Add(e);
            }

            var warnings = new List<string>();
            int columns = 0, walls = 0, created = 0, removed = 0;
            using (var t = new Transaction(doc, "MCP: Dimension Elements"))
            {
                t.Start();
                if (replace)
                {
                    var targets = new HashSet<long>(elements.Select(e => e.Id.GetValue()));
                    var old = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Dimension)).Cast<Dimension>()
                        .Where(d => d.References.Cast<Reference>().Any(r => targets.Contains(r.ElementId.GetValue())))
                        .Select(d => d.Id).ToList();
                    if (old.Count > 0) removed = doc.Delete(old).Count;
                }

                foreach (var e in elements)
                {
                    try
                    {
                        if (e is Wall w)
                        {
                            created += DimensionWall(doc, view, w, z, grids, toGrids, gridDistance, offset, wallLengthMin, wallParts, dimType, warnings);
                            walls++;
                        }
                        else if (e is FamilyInstance fi && fi.Location is LocationPoint)
                        {
                            created += DimensionPointElement(doc, view, fi, z, grids, toGrids, gridDistance, offset, side, dimType, warnings);
                            columns++;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (warnings.Count < 50) warnings.Add($"{e.Id.GetValue()}: {ex.Message}");
                    }
                }

                t.Commit();
            }

            var result = new JObject
            {
                ["viewId"] = view.Id.GetValue(),
                ["pointElements"] = columns,
                ["walls"] = walls,
                ["dimensionsCreated"] = created,
                ["dimensionsReplaced"] = removed
            };
            if (warnings.Count > 0) result["warnings"] = new JArray(warnings);
            return Ok($"Created {created} dimensions for {columns} columns/footings and {walls} walls.", result);
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
                warnings.Add($"{fi.Id.GetValue()}: no face references in this view.");
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

        private static int DimensionWall(Document doc, View view, Wall w, double z, List<Grid> grids, bool toGrids,
            double reach, double offset, double minLength, List<string> parts, DimensionType dimType, List<string> warnings)
        {
            if (!(((LocationCurve)w.Location).Curve is Line lc)) return 0;
            var u = new XYZ(lc.Direction.X, lc.Direction.Y, 0).Normalize();
            var n = new XYZ(-u.Y, u.X, 0);
            var a = new XYZ(lc.GetEndPoint(0).X, lc.GetEndPoint(0).Y, z);
            var b = new XYZ(lc.GetEndPoint(1).X, lc.GetEndPoint(1).Y, z);
            var mid = (a + b) / 2;
            var length = a.DistanceTo(b);
            var made = 0;

            if (parts.Contains("thickness"))
            {
                // Side faces come from HostObjectUtils: view-specific wall geometry has no face references.
                var sides = new List<Tuple<double, Reference>>();
                foreach (var shell in new[] { ShellLayerType.Exterior, ShellLayerType.Interior })
                foreach (var r in HostObjectUtils.GetSideFaces(w, shell))
                    if (w.GetGeometryObjectFromReference(r) is PlanarFace pf)
                        sides.Add(Tuple.Create(pf.Origin.DotProduct(n), r));
                if (sides.Count >= 2)
                {
                    var ordered = sides.OrderBy(s => s.Item1).ToList();
                    var refs = new List<Tuple<double, Reference>> { ordered.First(), ordered.Last() };
                    if (toGrids)
                    {
                        var g = NearestGrid(grids, n, mid.DotProduct(n), reach, out var gAt);
                        if (g != null && refs.All(r => Math.Abs(r.Item1 - gAt) > Mm))
                            refs.Add(Tuple.Create(gAt, new Reference(g)));
                    }

                    var at = mid + u * Math.Min(length * 0.25, 1500 * Mm);
                    made += Create(doc, view, at - n * 1500 * Mm, at + n * 1500 * Mm, refs, dimType, w, warnings);
                }
            }

            if (parts.Contains("length") && length >= minLength)
            {
                var ends = new List<PlanarFace>();
                foreach (var go in w.get_Geometry(new Options { ComputeReferences = true }))
                    if (go is Solid s)
                        foreach (Face f in s.Faces)
                            if (f is PlanarFace pf && pf.Reference != null && Math.Abs(Math.Abs(pf.FaceNormal.DotProduct(u)) - 1) < 1e-3)
                                ends.Add(pf);
                ends = ends.OrderBy(f => f.Origin.DotProduct(u)).ToList();
                if (ends.Count >= 2)
                {
                    var refs = new List<Tuple<double, Reference>>
                    {
                        Tuple.Create(ends.First().Origin.DotProduct(u), ends.First().Reference),
                        Tuple.Create(ends.Last().Origin.DotProduct(u), ends.Last().Reference)
                    };
                    var off = w.Width / 2 + offset;
                    made += Create(doc, view, a - n * off - u * 500 * Mm, b - n * off + u * 500 * Mm, refs, dimType, w, warnings);
                }
                else if (warnings.Count < 50)
                {
                    warnings.Add($"{w.Id.GetValue()}: wall end faces are joined to other walls; length not dimensioned.");
                }
            }

            return made;
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
                if (warnings.Count < 50) warnings.Add($"{e.Id.GetValue()}: {ex.Message}");
                return 0;
            }
        }
    }
}
