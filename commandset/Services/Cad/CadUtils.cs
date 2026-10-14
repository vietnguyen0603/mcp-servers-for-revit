using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Cad
{
    /// <summary>One curve-like item of a CAD import, in model coordinates (feet).</summary>
    internal sealed class CadItem
    {
        /// <summary>line, arc, circle, curve (ellipse/spline), polyline, solid, mesh, point.</summary>
        public string Kind;
        public string Layer;
        public ElementId StyleId;
        /// <summary>Model-coordinate curve for line/arc/circle/curve items.</summary>
        public Curve Curve;
        /// <summary>Model-coordinate vertices for polyline and tessellated curve items.</summary>
        public IList<XYZ> Points;
        public double MinX = double.MaxValue, MinY = double.MaxValue, MinZ = double.MaxValue;
        public double MaxX = double.MinValue, MaxY = double.MinValue, MaxZ = double.MinValue;

        public bool HasExtent => MinX <= MaxX;

        public void Grow(XYZ p)
        {
            MinX = Math.Min(MinX, p.X);
            MinY = Math.Min(MinY, p.Y);
            MinZ = Math.Min(MinZ, p.Z);
            MaxX = Math.Max(MaxX, p.X);
            MaxY = Math.Max(MaxY, p.Y);
            MaxZ = Math.Max(MaxZ, p.Z);
        }
    }

    /// <summary>Plan-region filter in feet (model X/Y).</summary>
    internal sealed class CadRegion
    {
        public double MinX, MinY, MaxX, MaxY;
        public bool Inside;

        public static CadRegion Read(JToken token, string mode)
        {
            if (token == null || token.Type != JTokenType.Object)
                return null;
            var region = new CadRegion
            {
                MinX = DocumentationUtils.MmToFeet(token.Value<double>("minX")),
                MinY = DocumentationUtils.MmToFeet(token.Value<double>("minY")),
                MaxX = DocumentationUtils.MmToFeet(token.Value<double>("maxX")),
                MaxY = DocumentationUtils.MmToFeet(token.Value<double>("maxY")),
                Inside = string.Equals(mode, "inside", StringComparison.OrdinalIgnoreCase)
            };
            if (region.MaxX <= region.MinX || region.MaxY <= region.MinY)
                throw new ArgumentException("'region' needs maxX > minX and maxY > minY (mm).");
            return region;
        }

        public bool Accepts(CadItem item)
        {
            if (!item.HasExtent)
                return false;
            if (Inside)
                return item.MinX >= MinX && item.MaxX <= MaxX && item.MinY >= MinY && item.MaxY <= MaxY;
            return item.MaxX >= MinX && item.MinX <= MaxX && item.MaxY >= MinY && item.MinY <= MaxY;
        }
    }

    /// <summary>
    ///     Shared helpers for the CAD tools: import lookup, layer names, a
    ///     geometry walker that resolves nested block transforms into model
    ///     coordinates, and wildcard layer filters.
    /// </summary>
    internal static class CadUtils
    {
        public const string TextNote =
            "CAD texts (TEXT/MTEXT/attributes) are not exposed by the Revit API; only curves, polylines and hatch/solid geometry can be read.";

        private const int MaxDepth = 24;

        public static ImportInstance RequireImport(Document doc, JObject parameters, string name = "importId")
        {
            var id = DocumentationUtils.ReadId(parameters, name)
                     ?? throw new ArgumentException($"'{name}' is required.");
            return DocumentationUtils.GetElement<ImportInstance>(doc, id)
                   ?? throw new ArgumentException($"Element {id} is not a CAD import/link instance (ImportInstance).");
        }

        public static string ImportName(ImportInstance instance)
        {
            return instance.Category?.Name ?? instance.Name;
        }

        public static string LinkPath(Document doc, ElementId typeId)
        {
            try
            {
                var type = doc.GetElement(typeId);
                if (type == null || !type.IsExternalFileReference())
                    return null;
                var path = type.GetExternalFileReference().GetAbsolutePath();
                return path == null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(path);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Layer names = subcategories of the import's own category, sorted.</summary>
        public static List<string> Layers(ImportInstance instance)
        {
            var category = instance.Category;
            if (category == null)
                return new List<string>();
            return category.SubCategories.Cast<Category>().Select(c => c.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static Category LayerCategory(ImportInstance instance, string layer)
        {
            var category = instance.Category;
            if (category == null)
                return null;
            return category.SubCategories.Cast<Category>()
                .FirstOrDefault(c => string.Equals(c.Name, layer, StringComparison.OrdinalIgnoreCase));
        }

        public static object TransformInfo(Transform t)
        {
            return new
            {
                origin = new { x = Mm(t.Origin.X), y = Mm(t.Origin.Y), z = Mm(t.Origin.Z) },
                rotationDeg = Math.Round(Math.Atan2(t.BasisX.Y, t.BasisX.X) * 180.0 / Math.PI, 4),
                basisX = Vector(t.BasisX),
                basisY = Vector(t.BasisY),
                scale = Math.Round(t.Scale, 6)
            };
        }

        public static object Describe(Document doc, ImportInstance instance)
        {
            var typeId = instance.GetTypeId();
            var ownerView = instance.ViewSpecific ? doc.GetElement(instance.OwnerViewId) as View : null;
            return new
            {
                id = instance.Id.GetValue(),
                name = ImportName(instance),
                typeId = typeId.GetValue(),
                linked = instance.IsLinked,
                path = LinkPath(doc, typeId),
                viewSpecific = instance.ViewSpecific,
                ownerViewId = ownerView?.Id.GetValue(),
                ownerViewName = ownerView?.Name,
                pinned = instance.Pinned
            };
        }

        /// <summary>Geometry options: the owner view for view-specific imports, else the given view or none.</summary>
        public static Options GeometryOptions(Document doc, ImportInstance instance, View view)
        {
            var options = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false };
            if (view != null)
                options.View = view;
            else if (instance.ViewSpecific && doc.GetElement(instance.OwnerViewId) is View owner)
                options.View = owner;
            return options;
        }

        /// <summary>
        ///     Walks the import geometry, applying each nested block transform so
        ///     every item comes out in model coordinates. Items with no own layer
        ///     inherit their block's layer.
        /// </summary>
        public static List<CadItem> Collect(Document doc, ImportInstance instance, View view)
        {
            var geometry = instance.get_Geometry(GeometryOptions(doc, instance, view));
            if (geometry == null)
                throw new InvalidOperationException(
                    $"Import {instance.Id.GetValue()} has no readable geometry (unloaded link, or not visible in the given view).");
            var items = new List<CadItem>();
            var cache = new Dictionary<long, string>();
            Walk(doc, geometry, Transform.Identity, null, ElementId.InvalidElementId, cache, items, 0);
            return items;
        }

        private static void Walk(Document doc, GeometryElement geometry, Transform transform, string parentLayer,
            ElementId parentStyle, Dictionary<long, string> cache, List<CadItem> items, int depth)
        {
            foreach (GeometryObject obj in geometry)
            {
                var styleId = obj.GraphicsStyleId;
                var layer = LayerName(doc, styleId, cache);
                if (layer == null)
                {
                    layer = parentLayer;
                    styleId = parentStyle;
                }

                switch (obj)
                {
                    case GeometryInstance instance:
                        if (depth < MaxDepth)
                            Walk(doc, instance.GetSymbolGeometry(), transform.Multiply(instance.Transform), layer, styleId,
                                cache, items, depth + 1);
                        break;
                    case Curve curve:
                        var item = FromCurve(transform.IsIdentity ? curve : curve.CreateTransformed(transform));
                        if (item != null)
                        {
                            item.Layer = layer ?? "0";
                            item.StyleId = styleId;
                            items.Add(item);
                        }

                        break;
                    case PolyLine polyLine:
                        var points = polyLine.GetCoordinates().Select(transform.OfPoint).ToList();
                        if (points.Count >= 2)
                        {
                            var poly = new CadItem { Kind = "polyline", Points = points, Layer = layer ?? "0", StyleId = styleId };
                            foreach (var p in points)
                                poly.Grow(p);
                            items.Add(poly);
                        }

                        break;
                    case Solid solid:
                        if (solid.Faces.Size > 0)
                            items.Add(Bounded(new CadItem { Kind = "solid", Layer = layer ?? "0", StyleId = styleId },
                                SolidBox(solid), transform));
                        break;
                    case Mesh mesh:
                        var meshItem = new CadItem { Kind = "mesh", Layer = layer ?? "0", StyleId = styleId };
                        foreach (var v in mesh.Vertices)
                            meshItem.Grow(transform.OfPoint(v));
                        items.Add(meshItem);
                        break;
                    case Point point:
                        var pointItem = new CadItem { Kind = "point", Layer = layer ?? "0", StyleId = styleId };
                        pointItem.Grow(transform.OfPoint(point.Coord));
                        items.Add(pointItem);
                        break;
                }
            }
        }

        private static BoundingBoxXYZ SolidBox(Solid solid)
        {
            try
            {
                return solid.GetBoundingBox();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static CadItem Bounded(CadItem item, BoundingBoxXYZ box, Transform transform)
        {
            if (box == null)
                return item;
            var t = transform.Multiply(box.Transform);
            item.Grow(t.OfPoint(box.Min));
            item.Grow(t.OfPoint(box.Max));
            item.Grow(t.OfPoint(new XYZ(box.Min.X, box.Max.Y, box.Min.Z)));
            item.Grow(t.OfPoint(new XYZ(box.Max.X, box.Min.Y, box.Max.Z)));
            return item;
        }

        private static CadItem FromCurve(Curve curve)
        {
            switch (curve)
            {
                case Line line:
                    if (!line.IsBound)
                        return null;
                    var lineItem = new CadItem { Kind = "line", Curve = line };
                    lineItem.Grow(line.GetEndPoint(0));
                    lineItem.Grow(line.GetEndPoint(1));
                    return lineItem;
                case Arc arc:
                    var full = !arc.IsBound || Math.Abs(arc.Length - 2 * Math.PI * arc.Radius) < 1e-6;
                    var arcItem = new CadItem { Kind = full ? "circle" : "arc", Curve = arc };
                    if (full)
                    {
                        // Planar box of the circle around its centre (exact for plan circles)
                        var r = arc.Radius;
                        arcItem.Grow(arc.Center - new XYZ(r, r, 0));
                        arcItem.Grow(arc.Center + new XYZ(r, r, 0));
                    }
                    else
                    {
                        foreach (var p in arc.Tessellate())
                            arcItem.Grow(p);
                    }

                    return arcItem;
                default:
                    IList<XYZ> points;
                    try
                    {
                        points = curve.Tessellate();
                    }
                    catch (Exception)
                    {
                        return null;
                    }

                    if (points == null || points.Count < 2)
                        return null;
                    var curveItem = new CadItem { Kind = "curve", Curve = curve, Points = points };
                    foreach (var p in points)
                        curveItem.Grow(p);
                    return curveItem;
            }
        }

        public static string LayerName(Document doc, ElementId styleId, Dictionary<long, string> cache)
        {
            if (styleId == null || styleId == ElementId.InvalidElementId)
                return null;
            var key = styleId.GetValue();
            if (cache.TryGetValue(key, out var cached))
                return cached;
            var name = (doc.GetElement(styleId) as GraphicsStyle)?.GraphicsStyleCategory?.Name;
            cache[key] = name;
            return name;
        }

        public static List<string> ReadStrings(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return new List<string>();
            if (token.Type == JTokenType.String)
                return new List<string> { token.Value<string>() };
            return token.Values<string>().Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        }

        /// <summary>Case-insensitive wildcard matcher (* and ?); null when no patterns are given.</summary>
        public static Func<string, bool> Matcher(IEnumerable<string> patterns)
        {
            var regexes = patterns.Select(WildcardRegex).ToList();
            if (regexes.Count == 0)
                return null;
            return name => name != null && regexes.Any(r => r.IsMatch(name));
        }

        public static Regex WildcardRegex(string pattern)
        {
            var body = Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".");
            return new Regex("^" + body + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        public static double Mm(double feet) => Math.Round(feet * DocumentationUtils.MmPerFoot, 1);

        private static object Vector(XYZ v) => new[] { Math.Round(v.X, 6), Math.Round(v.Y, 6), Math.Round(v.Z, 6) };
    }
}
