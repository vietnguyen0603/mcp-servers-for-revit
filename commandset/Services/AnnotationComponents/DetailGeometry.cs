using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Geometry helpers for view-specific detailing: millimetre points are
    ///     projected onto the view plane (origin + view direction) and turned
    ///     into Revit curves and loops in feet.
    /// </summary>
    internal static class DetailGeometry
    {
        /// <summary>Throws when the view cannot host view-specific detail elements.</summary>
        public static void RequireDetailView(View view)
        {
            if (view == null)
                throw new ArgumentException("View not found.");
            if (view.IsTemplate)
                throw new ArgumentException($"'{view.Name}' is a view template.");
            if (view is View3D || view is ViewSchedule || view.ViewType == ViewType.Internal
                || view.ViewType == ViewType.ProjectBrowser || view.ViewType == ViewType.SystemBrowser)
                throw new ArgumentException($"View '{view.Name}' ({view.ViewType}) cannot host detail elements.");
        }

        public static View ResolveView(Autodesk.Revit.UI.UIDocument uiDoc, JToken parameters)
        {
            var doc = uiDoc.Document;
            var viewId = DocumentationUtils.ReadId(parameters, "viewId");
            var view = viewId != null
                ? DocumentationUtils.GetElement<View>(doc, viewId)
                  ?? throw new ArgumentException($"viewId {viewId} is not a view.")
                : uiDoc.ActiveView;
            RequireDetailView(view);
            return view;
        }

        /// <summary>Projects a point onto the plane through the view origin, normal to the view direction.</summary>
        public static XYZ Project(View view, XYZ point)
        {
            var normal = view.ViewDirection.Normalize();
            var origin = view.Origin ?? XYZ.Zero;
            return point - normal.Multiply(normal.DotProduct(point - origin));
        }

        public static XYZ ReadPoint(View view, JToken token, string what)
        {
            var point = DocumentationUtils.ReadPointMm(token)
                        ?? throw new ArgumentException($"'{what}' must be a point {{x, y, z?}} in mm.");
            return Project(view, point);
        }

        public static List<XYZ> ReadPoints(View view, JToken token, string what, int minimum)
        {
            if (!(token is JArray array))
                throw new ArgumentException($"'{what}' must be an array of points (mm).");
            var points = array.Select((p, i) => ReadPoint(view, p, $"{what}[{i}]")).ToList();
            if (points.Count < minimum)
                throw new ArgumentException($"'{what}' needs at least {minimum} points.");
            return points;
        }

        /// <summary>Line segments through the points; closes the polygon when requested. Skips zero-length segments.</summary>
        public static List<Curve> Polyline(IList<XYZ> points, bool closed)
        {
            var curves = new List<Curve>();
            var count = points.Count;
            var segments = closed ? count : count - 1;
            for (var i = 0; i < segments; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % count];
                if (a.IsAlmostEqualTo(b))
                    continue;
                curves.Add(Line.CreateBound(a, b));
            }

            if (curves.Count == 0)
                throw new ArgumentException("Points do not form any segment.");
            return curves;
        }

        /// <summary>
        ///     Polyline whose corners are replaced by tangent arcs. <paramref name="radii" /> holds one
        ///     radius (feet) per input point; end points of open polylines are never filleted. A radius
        ///     that does not fit the adjacent segments is reduced to the largest that fits (a segment
        ///     shared by two oversized fillets gives each half its length) and reported in <paramref name="warnings" />.
        ///     Collinear corners need no fillet and reversals cannot be filleted (warned).
        /// </summary>
        public static List<Curve> FilletedPolyline(IList<XYZ> points, IList<double> radii, bool closed,
            double shortCurveTolerance, List<string> warnings)
        {
            // Drop consecutive duplicates (keeping the first radius) and a repeated closing point.
            var pts = new List<XYZ>();
            var rad = new List<double>();
            var source = new List<int>();
            for (var i = 0; i < points.Count; i++)
            {
                if (pts.Count > 0 && pts[pts.Count - 1].IsAlmostEqualTo(points[i]))
                    continue;
                pts.Add(points[i]);
                rad.Add(Math.Max(0, radii[i]));
                source.Add(i);
            }

            if (closed && pts.Count > 1 && pts[0].IsAlmostEqualTo(pts[pts.Count - 1]))
            {
                pts.RemoveAt(pts.Count - 1);
                rad.RemoveAt(rad.Count - 1);
                source.RemoveAt(source.Count - 1);
            }

            var n = pts.Count;
            if (n < 2 || (closed && n < 3))
                return Polyline(pts, closed && n >= 3);

            var segments = closed ? n : n - 1;
            var dir = new XYZ[segments];
            var len = new double[segments];
            for (var s = 0; s < segments; s++)
            {
                var v = pts[(s + 1) % n] - pts[s];
                len[s] = v.GetLength();
                dir[s] = v.Normalize();
            }

            // Desired fillet per vertex: half angle and requested radius; tangent length t = r / tan(half).
            var tangent = new double[n];
            var radius = new double[n];
            var half = new double[n];
            var wanted = new bool[n];
            for (var i = 0; i < n; i++)
            {
                if (rad[i] <= 0 || (!closed && (i == 0 || i == n - 1)))
                    continue;
                var dIn = dir[(i - 1 + segments) % segments];
                var dOut = dir[i % segments];
                var interior = (-dIn).AngleTo(dOut); // 0 = reversal, PI = straight
                if (interior > Math.PI - 1e-6)
                    continue;
                if (interior < 1e-6)
                {
                    warnings.Add($"Vertex {source[i]}: segments reverse direction; corner not filleted.");
                    continue;
                }

                wanted[i] = true;
                half[i] = interior / 2;
                radius[i] = rad[i];
                tangent[i] = rad[i] / Math.Tan(half[i]);
            }

            for (var i = 0; i < n; i++)
            {
                if (!wanted[i])
                    continue;
                var sIn = (i - 1 + segments) % segments;
                var sOut = i % segments;
                var prev = (i - 1 + n) % n;
                var next = (i + 1) % n;
                // A neighbouring fillet keeps its own tangent length, but at most half the shared segment.
                var availIn = len[sIn] - (wanted[prev] ? Math.Min(tangent[prev], len[sIn] / 2) : 0);
                var availOut = len[sOut] - (wanted[next] ? Math.Min(tangent[next], len[sOut] / 2) : 0);
                var max = Math.Min(availIn, availOut);
                if (tangent[i] <= max + 1e-9)
                    continue;

                var clamped = max * Math.Tan(half[i]);
                if (clamped < shortCurveTolerance)
                {
                    warnings.Add($"Vertex {source[i]}: radius {DocumentationUtils.FeetToMm(radius[i])} mm does not fit; corner not filleted.");
                    wanted[i] = false;
                    tangent[i] = 0;
                    continue;
                }

                warnings.Add($"Vertex {source[i]}: radius {DocumentationUtils.FeetToMm(radius[i])} mm does not fit; " +
                             $"reduced to {DocumentationUtils.FeetToMm(clamped)} mm.");
                radius[i] = clamped;
                tangent[i] = max;
            }

            var curves = new List<Curve>();
            for (var s = 0; s < segments; s++)
            {
                var a = s;
                var b = (s + 1) % n;
                var start = pts[a] + dir[s] * (wanted[a] ? tangent[a] : 0);
                var end = pts[b] - dir[s] * (wanted[b] ? tangent[b] : 0);
                if (start.DistanceTo(end) >= shortCurveTolerance && (end - start).DotProduct(dir[s]) > 0)
                    curves.Add(Line.CreateBound(start, end));

                if (!wanted[b])
                    continue;
                var dIn = dir[s];
                var dOut = dir[b % segments];
                var t1 = pts[b] - dIn * tangent[b];
                var t2 = pts[b] + dOut * tangent[b];
                var bisector = (dOut - dIn).Normalize();
                var mid = pts[b] + bisector * (radius[b] / Math.Sin(half[b]) - radius[b]);
                curves.Add(Arc.Create(t1, t2, mid));
            }

            if (curves.Count == 0)
                throw new ArgumentException("Points do not form any segment.");
            return curves;
        }

        /// <summary>Closed loop from a polygon; a repeated closing point is tolerated.</summary>
        public static CurveLoop Loop(IList<XYZ> points)
        {
            var list = points.ToList();
            if (list.Count > 1 && list[0].IsAlmostEqualTo(list[list.Count - 1]))
                list.RemoveAt(list.Count - 1);
            if (list.Count < 3)
                throw new ArgumentException("A closed loop needs at least 3 distinct points.");
            return CurveLoop.Create(Polyline(list, true));
        }

        /// <summary>Rectangle aligned with the view's right/up directions, spanning two opposite corners.</summary>
        public static List<XYZ> Rectangle(View view, XYZ min, XYZ max)
        {
            var right = view.RightDirection;
            var up = view.UpDirection;
            var diagonal = max - min;
            var width = right.Multiply(diagonal.DotProduct(right));
            var height = up.Multiply(diagonal.DotProduct(up));
            if (width.GetLength() < 1e-6 || height.GetLength() < 1e-6)
                throw new ArgumentException("Rectangle has zero width or height in this view.");
            return new List<XYZ> { min, min + width, min + width + height, min + height };
        }

        /// <summary>Line style (GraphicsStyle) by name among the subcategories of Lines.</summary>
        public static GraphicsStyle ResolveLineStyle(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            var lines = Category.GetCategory(doc, BuiltInCategory.OST_Lines);
            foreach (Category sub in lines.SubCategories)
            {
                if (string.Equals(sub.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                    return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
            }

            throw new ArgumentException(
                $"Line style '{name}' not found. Available: {string.Join(", ", LineStyleNames(doc))}.");
        }

        public static List<string> LineStyleNames(Document doc)
        {
            var lines = Category.GetCategory(doc, BuiltInCategory.OST_Lines);
            return lines.SubCategories.Cast<Category>().Select(c => c.Name).OrderBy(n => n).ToList();
        }
    }
}
