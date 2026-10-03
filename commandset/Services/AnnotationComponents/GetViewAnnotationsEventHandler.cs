using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Lists the view-specific 2D elements of a view (detail lines, text notes,
    ///     dimensions, filled regions, detail components, other annotations) with
    ///     their geometry in the same millimetre model coordinates the create
    ///     tools accept, so drawn content can be read back and edited. Read-only.
    /// </summary>
    public class GetViewAnnotationsEventHandler : JsonParameterEventHandler
    {
        private static readonly string[] Kinds =
            { "detailLines", "textNotes", "dimensions", "filledRegions", "detailComponents", "other" };

        public override string GetName() => "Get View Annotations";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var view = DetailGeometry.ResolveView(uiDoc, parameters);

            var kinds = parameters["kinds"]?.ToObject<List<string>>();
            if (kinds != null)
            {
                var unknown = kinds.Where(k => !Kinds.Contains(k)).ToList();
                if (unknown.Count > 0)
                    return Fail($"Unknown kinds: {string.Join(", ", unknown)}. Use {string.Join(", ", Kinds)}.");
            }

            var limit = Math.Max(1, Math.Min(5000, parameters.Value<int?>("limit") ?? 500));
            var offset = Math.Max(0, parameters.Value<int?>("offset") ?? 0);

            var all = new FilteredElementCollector(doc, view.Id)
                .WhereElementIsNotElementType()
                .Where(e => e.OwnerViewId == view.Id && e.Category != null && !(e is View))
                .Select(e => new { element = e, kind = KindOf(e) })
                .Where(x => kinds == null || kinds.Contains(x.kind))
                .OrderBy(x => x.element.Id.GetValue())
                .ToList();

            var page = all.Skip(offset).Take(limit).Select(x => Describe(doc, x.element, x.kind)).ToList();

            return Ok($"Found {all.Count} annotation elements in '{view.Name}'; returned {page.Count}.", new
            {
                viewId = view.Id.GetValue(),
                viewName = view.Name,
                viewType = view.ViewType.ToString(),
                scale = view.Scale,
                total = all.Count,
                offset,
                returned = page.Count,
                hasMore = offset + page.Count < all.Count,
                counts = all.GroupBy(x => x.kind).ToDictionary(g => g.Key, g => g.Count()),
                elements = page
            });
        }

        private static string KindOf(Element e)
        {
            switch (e)
            {
                case DetailCurve _:
                    return "detailLines";
                case TextNote _:
                    return "textNotes";
                case Dimension _:
                    return "dimensions";
                case FilledRegion _:
                    return "filledRegions";
                case FamilyInstance _:
                    return "detailComponents";
                default:
                    return "other";
            }
        }

        private static JObject Describe(Document doc, Element e, string kind)
        {
            var info = new JObject
            {
                ["id"] = e.Id.GetValue(),
                ["kind"] = kind,
                ["category"] = e.Category?.Name
            };

            // One element the API cannot describe must not fail the whole read.
            try
            {
                DescribeDetails(doc, e, info);
            }
            catch (Exception ex)
            {
                info["error"] = ex.Message;
            }

            return info;
        }

        private static void DescribeDetails(Document doc, Element e, JObject info)
        {
            switch (e)
            {
                case DetailCurve curve:
                    info["lineStyle"] = (curve.LineStyle as GraphicsStyle)?.Name;
                    DescribeCurve(info, curve.GeometryCurve);
                    break;

                case TextNote note:
                    info["text"] = note.Text?.TrimEnd('\r', '\n');
                    info["location"] = JToken.FromObject(DocumentationUtils.PointToMm(note.Coord));
                    info["typeName"] = doc.GetElement(note.GetTypeId())?.Name;
                    info["width"] = Math.Round(note.Width * DocumentationUtils.MmPerFoot, 2);
                    info["horizontalAlignment"] = note.HorizontalAlignment.ToString();
                    break;

                case Dimension dim:
                    info["typeName"] = doc.GetElement(dim.GetTypeId())?.Name;
                    if (dim.NumberOfSegments > 1)
                    {
                        info["segments"] = new JArray(dim.Segments.Cast<DimensionSegment>()
                            .Select(s => (object)(s.Value.HasValue ? DocumentationUtils.FeetToMm(s.Value.Value) : (double?)null)));
                        info["value"] = DocumentationUtils.FeetToMm(
                            dim.Segments.Cast<DimensionSegment>().Sum(s => s.Value ?? 0));
                    }
                    else
                    {
                        info["value"] = dim.Value.HasValue ? DocumentationUtils.FeetToMm(dim.Value.Value) : (double?)null;
                    }

                    info["referencedIds"] = new JArray(dim.References.Cast<Reference>()
                        .Select(r => r.ElementId.GetValue()).Distinct());
                    // Dimension lines are unbound and Dimension.Origin throws for
                    // multi-segment dimensions, so report per-segment origins.
                    if (dim.Curve is Line dimLine)
                        info["direction"] = JToken.FromObject(new
                        {
                            x = Math.Round(dimLine.Direction.X, 6),
                            y = Math.Round(dimLine.Direction.Y, 6),
                            z = Math.Round(dimLine.Direction.Z, 6)
                        });
                    info["origins"] = dim.NumberOfSegments > 1
                        ? new JArray(dim.Segments.Cast<DimensionSegment>()
                            .Select(s => JToken.FromObject(DocumentationUtils.PointToMm(s.Origin))))
                        : new JArray(JToken.FromObject(DocumentationUtils.PointToMm(dim.Origin)));
                    break;

                case FilledRegion region:
                    info["typeName"] = doc.GetElement(region.GetTypeId())?.Name;
                    info["isMasking"] = region.IsMasking;
                    info["boundaries"] = new JArray(region.GetBoundaries().Select(loop =>
                        new JArray(loop.Select(c => JToken.FromObject(DocumentationUtils.PointToMm(c.GetEndPoint(0)))))));
                    break;

                case FamilyInstance instance:
                    info["familyName"] = instance.Symbol?.FamilyName;
                    info["typeName"] = instance.Symbol?.Name;
                    if (instance.Location is LocationPoint lp)
                    {
                        info["location"] = JToken.FromObject(DocumentationUtils.PointToMm(lp.Point));
                        info["rotationDeg"] = Math.Round(lp.Rotation * 180 / Math.PI, 3);
                    }
                    else if (instance.Location is LocationCurve lc)
                    {
                        DescribeCurve(info, lc.Curve);
                    }
                    break;

                default:
                    var box = e.get_BoundingBox(doc.GetElement(e.OwnerViewId) as View);
                    if (box != null)
                    {
                        info["min"] = JToken.FromObject(DocumentationUtils.PointToMm(box.Min));
                        info["max"] = JToken.FromObject(DocumentationUtils.PointToMm(box.Max));
                    }
                    break;
            }
        }

        private static void DescribeCurve(JObject info, Curve curve)
        {
            if (curve == null)
                return;
            if (curve is Arc arc)
            {
                info["shape"] = "arc";
                info["center"] = JToken.FromObject(DocumentationUtils.PointToMm(arc.Center));
                info["radius"] = DocumentationUtils.FeetToMm(arc.Radius);
            }
            else
            {
                info["shape"] = curve is Line ? "line" : curve.GetType().Name.ToLowerInvariant();
            }

            if (curve.IsBound)
            {
                info["start"] = JToken.FromObject(DocumentationUtils.PointToMm(curve.GetEndPoint(0)));
                info["end"] = JToken.FromObject(DocumentationUtils.PointToMm(curve.GetEndPoint(1)));
                info["length"] = DocumentationUtils.FeetToMm(curve.Length);
            }
        }
    }
}
