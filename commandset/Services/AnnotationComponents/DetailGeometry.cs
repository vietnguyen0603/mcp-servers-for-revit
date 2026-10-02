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
