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

        /// <summary>
        ///     Line style (GraphicsStyle) by name among the subcategories of Lines; throws when missing.
        ///     Matching: exact, then case-insensitive, then ignoring angle brackets ("Medium Lines" = "&lt;Medium Lines&gt;").
        /// </summary>
        public static GraphicsStyle ResolveLineStyle(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            return FindLineStyle(doc, name)
                   ?? throw new ArgumentException(
                       $"Line style '{name}' not found. Available: {string.Join(", ", LineStyleNames(doc))}.");
        }

        /// <summary>As <see cref="ResolveLineStyle" /> but returns null when no style matches.</summary>
        public static GraphicsStyle FindLineStyle(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            var lines = Category.GetCategory(doc, BuiltInCategory.OST_Lines);
            var subs = lines.SubCategories.Cast<Category>().ToList();
            var actual = MatchLineStyleName(subs.Select(c => c.Name), name);
            return actual == null
                ? null
                : subs.First(c => c.Name == actual).GetGraphicsStyle(GraphicsStyleType.Projection);
        }

        /// <summary>
        ///     The entry of <paramref name="names" /> matching <paramref name="name" />: exact, then
        ///     case-insensitive, then case-insensitive ignoring surrounding angle brackets; null if none.
        /// </summary>
        public static string MatchLineStyleName(IEnumerable<string> names, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            var list = names.Where(n => n != null).ToList();
            var wanted = name.Trim();
            var bare = StripBrackets(wanted);
            return list.FirstOrDefault(n => string.Equals(n, wanted, StringComparison.Ordinal))
                   ?? list.FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase))
                   ?? list.FirstOrDefault(n => string.Equals(StripBrackets(n), bare, StringComparison.OrdinalIgnoreCase));
        }

        private static string StripBrackets(string name)
        {
            var s = name.Trim();
            if (s.StartsWith("<") && s.EndsWith(">") && s.Length >= 2)
                s = s.Substring(1, s.Length - 2).Trim();
            return s;
        }

        public static List<string> LineStyleNames(Document doc)
        {
            var lines = Category.GetCategory(doc, BuiltInCategory.OST_Lines);
            return lines.SubCategories.Cast<Category>().Select(c => c.Name).OrderBy(n => n).ToList();
        }

        /// <summary>Projects a point onto the plane through <paramref name="origin" /> with <paramref name="normal" />.</summary>
        public static XYZ ProjectOnto(XYZ origin, XYZ normal, XYZ point)
        {
            var n = normal.Normalize();
            return point - n.Multiply(n.DotProduct(point - origin));
        }

        /// <summary>
        ///     Line (start/end) or arc (start/end/mid) from mm points in <paramref name="spec" />,
        ///     projected onto the given plane (the element's own sketch plane, so its elevation is kept).
        /// </summary>
        public static Curve ReadCurve(XYZ origin, XYZ normal, JToken spec, string what)
        {
            if (spec == null)
                throw new ArgumentException($"'{what}' {{start, end, mid?}} is required.");
            XYZ Read(string name)
            {
                var p = DocumentationUtils.ReadPointMm(spec[name])
                        ?? throw new ArgumentException($"'{what}.{name}' must be a point {{x, y, z?}} in mm.");
                return ProjectOnto(origin, normal, p);
            }

            var start = Read("start");
            var end = Read("end");
            if (start.IsAlmostEqualTo(end))
                throw new ArgumentException($"'{what}' start and end coincide.");
            if (spec["mid"] == null || spec["mid"].Type == JTokenType.Null)
                return Line.CreateBound(start, end);
            var mid = Read("mid");
            if (mid.IsAlmostEqualTo(start) || mid.IsAlmostEqualTo(end))
                throw new ArgumentException($"'{what}.mid' coincides with an end point.");
            var chord = (end - start).Normalize();
            if ((mid - start).Normalize().IsAlmostEqualTo(chord))
                return Line.CreateBound(start, end); // collinear mid: a straight line
            return Arc.Create(start, end, mid);
        }

        /// <summary>
        ///     Replaces the location curve of a detail line (DetailCurve.SetGeometryCurve) or a
        ///     line-based detail/annotation family instance (LocationCurve.Curve). Points are projected
        ///     onto the element's current plane (sketch plane, else the plane through the existing curve
        ///     normal to the view direction). Returns the new start/end in mm.
        /// </summary>
        public static object ApplyCurve(Document doc, Element element, JToken spec, string what)
        {
            return ApplyCurve(doc, element, spec, what, out _);
        }

        /// <summary>
        ///     As <see cref="ApplyCurve(Document, Element, JToken, string)" />. A detail/model line whose
        ///     curve kind changes (line to arc or back) cannot take SetGeometryCurve, so a new curve element
        ///     is created with the same line style in the same view/sketch plane and the old one deleted;
        ///     <paramref name="result" /> is the element now carrying the curve (the replacement or the input).
        /// </summary>
        public static object ApplyCurve(Document doc, Element element, JToken spec, string what, out Element result)
        {
            result = element;
            var view = doc.GetElement(element.OwnerViewId) as View;
            if (element is CurveElement curveElement)
            {
                XYZ origin;
                XYZ normal;
                var plane = curveElement.SketchPlane?.GetPlane();
                if (plane != null)
                {
                    origin = plane.Origin;
                    normal = plane.Normal;
                }
                else
                {
                    origin = curveElement.GeometryCurve.GetEndPoint(0);
                    normal = view?.ViewDirection ?? XYZ.BasisZ;
                }

                var curve = ReadCurve(origin, normal, spec, what);
                if (SameCurveKind(curveElement.GeometryCurve, curve))
                {
                    curveElement.SetGeometryCurve(curve, true);
                    return Describe(element, curve);
                }

                var replacement = CreateCurveLike(doc, curveElement, curve);
                var oldId = element.Id.GetValue();
                doc.Delete(element.Id);
                result = replacement;
                return new
                {
                    elementId = replacement.Id.GetValue(),
                    replacedId = oldId,
                    start = DocumentationUtils.PointToMm(curve.GetEndPoint(0)),
                    end = DocumentationUtils.PointToMm(curve.GetEndPoint(1)),
                    curveType = curve is Arc ? "arc" : "line",
                    length = DocumentationUtils.FeetToMm(curve.Length)
                };
            }

            if (element.Location is LocationCurve location)
            {
                var existing = location.Curve;
                var normal = view?.ViewDirection ?? XYZ.BasisZ;
                var curve = ReadCurve(existing.GetEndPoint(0), normal, spec, what);
                location.Curve = curve;
                return Describe(element, curve);
            }

            throw new ArgumentException(
                $"Element {element.Id.GetValue()} ({element.Category?.Name ?? element.GetType().Name}) is not a detail line or line-based item.");
        }

        private static bool SameCurveKind(Curve a, Curve b)
        {
            return a != null && b != null && a.GetType() == b.GetType();
        }

        /// <summary>
        ///     New detail curve (same view) or model curve (same sketch plane) with the source's line style
        ///     and comments, for curve-kind changes SetGeometryCurve rejects.
        /// </summary>
        private static CurveElement CreateCurveLike(Document doc, CurveElement source, Curve curve)
        {
            CurveElement created;
            if (source is DetailCurve)
            {
                var view = doc.GetElement(source.OwnerViewId) as View
                           ?? throw new ArgumentException($"Detail line {source.Id.GetValue()} has no owner view.");
                created = doc.Create.NewDetailCurve(view, curve);
            }
            else if (source is ModelCurve
                     && source.Category?.Id.GetValue() == (long)BuiltInCategory.OST_Lines)
            {
                var sketchPlane = source.SketchPlane
                                  ?? throw new ArgumentException($"Model line {source.Id.GetValue()} has no sketch plane.");
                created = doc.Create.NewModelCurve(curve, sketchPlane);
            }
            else
            {
                throw new ArgumentException(
                    $"Element {source.Id.GetValue()} ({source.GetType().Name}) cannot change curve type " +
                    $"({source.GeometryCurve?.GetType().Name} to {curve.GetType().Name}).");
            }

            var style = source.LineStyle as GraphicsStyle;
            if (style != null)
            {
                try
                {
                    created.LineStyle = style;
                }
                catch (Exception)
                {
                    // Style not valid for the new curve: keep Revit's default.
                }
            }

            var comments = source.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
            var target = created.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (!string.IsNullOrEmpty(comments) && target != null && !target.IsReadOnly)
                target.Set(comments);

            return created;
        }

        private static object Describe(Element element, Curve curve)
        {
            return new
            {
                elementId = element.Id.GetValue(),
                start = DocumentationUtils.PointToMm(curve.GetEndPoint(0)),
                end = DocumentationUtils.PointToMm(curve.GetEndPoint(1)),
                curveType = curve is Arc ? "arc" : "line",
                length = DocumentationUtils.FeetToMm(curve.Length)
            };
        }
    }
}
