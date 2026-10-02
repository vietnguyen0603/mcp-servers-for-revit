using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Creates detail lines, arcs and polylines in a view. Each input item
    ///     may produce several detail curves (polylines); ids are reported per item.
    /// </summary>
    public class CreateDetailLinesEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Create Detail Lines";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var view = DetailGeometry.ResolveView(uiDoc, parameters);
            var items = DocumentationUtils.RequireArray(parameters, "lines");
            var defaultStyle = DetailGeometry.ResolveLineStyle(doc, parameters.Value<string>("lineStyle"));

            var results = DocumentationUtils.RunBatch(doc, "MCP: Create Detail Lines", items, item =>
            {
                var style = item["lineStyle"] != null
                    ? DetailGeometry.ResolveLineStyle(doc, item.Value<string>("lineStyle"))
                    : defaultStyle;

                var ids = new List<long>();
                foreach (var curve in BuildCurves(view, (JObject)item))
                {
                    var detailCurve = doc.Create.NewDetailCurve(view, curve);
                    if (style != null)
                        detailCurve.LineStyle = style;
                    ids.Add(detailCurve.Id.GetValue());
                }

                return new { detailCurveIds = ids };
            });

            return Ok($"Created detail lines for {results.Count(r => r.Value<bool>("success"))} of {results.Count} items in '{view.Name}'.",
                DocumentationUtils.Summarize(results));
        }

        private static List<Curve> BuildCurves(View view, JObject item)
        {
            if (item["points"] != null)
            {
                var points = DetailGeometry.ReadPoints(view, item["points"], "points", 2);
                return DetailGeometry.Polyline(points, item.Value<bool?>("closed") ?? false);
            }

            if (item["center"] != null)
            {
                var center = DetailGeometry.ReadPoint(view, item["center"], "center");
                var radius = item.Value<double?>("radius") ?? 0;
                if (radius <= 0)
                    throw new ArgumentException("Arc 'radius' must be positive (mm).");
                var start = (item.Value<double?>("startAngleDeg") ?? 0) * Math.PI / 180;
                var end = (item.Value<double?>("endAngleDeg") ?? 360) * Math.PI / 180;
                if (end <= start)
                    throw new ArgumentException("'endAngleDeg' must be greater than 'startAngleDeg'.");
                if (end - start >= 2 * Math.PI - 1e-9)
                {
                    // A detail curve cannot be a closed circle; split it into two half arcs.
                    var mid = start + Math.PI;
                    return new List<Curve>
                    {
                        Arc.Create(center, DocumentationUtils.MmToFeet(radius), start, mid, view.RightDirection, view.UpDirection),
                        Arc.Create(center, DocumentationUtils.MmToFeet(radius), mid, start + 2 * Math.PI, view.RightDirection, view.UpDirection)
                    };
                }

                return new List<Curve>
                {
                    Arc.Create(center, DocumentationUtils.MmToFeet(radius), start, end, view.RightDirection, view.UpDirection)
                };
            }

            var a = DetailGeometry.ReadPoint(view, item["start"], "start");
            var b = DetailGeometry.ReadPoint(view, item["end"], "end");
            return new List<Curve> { Line.CreateBound(a, b) };
        }
    }
}
