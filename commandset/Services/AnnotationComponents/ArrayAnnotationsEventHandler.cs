using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Copies view-specific elements (detail items, lines, groups, text...) as plain,
    ///     unassociated copies: linearly at a spacing / count / fill along a direction, or
    ///     to explicit points. Each array request is one batch item; all run in one undo step.
    /// </summary>
    public class ArrayAnnotationsEventHandler : JsonParameterEventHandler
    {
        private const int MaxSteps = 1000;

        public override string GetName() => "Array Annotations";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var arrays = DocumentationUtils.RequireArray(parameters, "arrays");
            var results = DocumentationUtils.RunBatch(doc, "MCP: Array Annotations", arrays,
                item => Apply(doc, (JObject)item));

            return Ok($"Arrayed {results.Count(r => r.Value<bool>("success"))} of {results.Count} items.",
                DocumentationUtils.Summarize(results));
        }

        private static object Apply(Document doc, JObject item)
        {
            var elements = ReadSources(doc, item);
            var ids = elements.Select(e => e.Id).ToList();
            var view = (View)doc.GetElement(elements[0].OwnerViewId);
            var mode = item.Value<string>("mode") ?? "linear";

            List<XYZ> offsets;
            string fit = null;
            double? spacingMm = null;
            switch (mode)
            {
                case "linear":
                    offsets = LinearOffsets(view, item, out fit, out spacingMm);
                    break;
                case "points":
                    offsets = PointOffsets(view, elements, item);
                    break;
                default:
                    throw new ArgumentException($"Unknown mode '{mode}'. Use linear or points.");
            }

            if (offsets.Count > MaxSteps)
                throw new ArgumentException($"{offsets.Count} copies requested; at most {MaxSteps} per item.");

            var steps = new List<object>();
            var created = 0;
            for (var i = 0; i < offsets.Count; i++)
            {
                var offset = offsets[i];
                if (offset.IsZeroLength())
                {
                    steps.Add(new { step = i + 1, offset = DocumentationUtils.PointToMm(offset), newIds = new long[0], original = true });
                    continue;
                }

                var copies = ElementTransformUtils.CopyElements(doc, ids, offset).Select(id => id.GetValue()).ToList();
                created += copies.Count;
                steps.Add(new { step = i + 1, offset = DocumentationUtils.PointToMm(offset), newIds = copies });
            }

            return new
            {
                mode,
                fit,
                spacing = spacingMm,
                sourceIds = ids.Select(id => id.GetValue()),
                viewId = view.Id.GetValue(),
                createdCount = created,
                steps
            };
        }

        private static List<Element> ReadSources(Document doc, JObject item)
        {
            var raw = item["elementIds"]?.ToObject<List<long>>() ?? new List<long>();
            if (raw.Count == 0)
                throw new ArgumentException("'elementIds' must list at least one element.");

            var elements = new List<Element>();
            foreach (var id in raw.Distinct())
            {
                var element = doc.GetElement(id.ToRevitElementId());
                if (element == null || element is ElementType)
                    throw new ArgumentException($"Element {id} not found.");
                if (element.OwnerViewId == ElementId.InvalidElementId)
                    throw new ArgumentException($"Element {id} is not view-specific.");
                if (elements.Count > 0 && element.OwnerViewId != elements[0].OwnerViewId)
                    throw new ArgumentException("All elements of one array must belong to the same view.");
                elements.Add(element);
            }

            return elements;
        }

        /// <summary>
        ///     Offsets from the source position. With includeOriginal (default) the source is member 1
        ///     and count is the total; otherwise count is the number of copies. fit:
        ///     spacing = count steps of spacing; count = count members spread over the along length;
        ///     fill = as many steps of spacing as fit in the along length.
        /// </summary>
        private static List<XYZ> LinearOffsets(View view, JObject item, out string fit, out double? spacingMm)
        {
            XYZ vector;
            double? length = null;
            if (item["along"] is JObject along)
            {
                vector = DetailGeometry.ReadPoint(view, along["end"], "along.end")
                         - DetailGeometry.ReadPoint(view, along["start"], "along.start");
                length = vector.GetLength();
            }
            else if (item["direction"] != null)
            {
                var d = DocumentationUtils.ReadPointMm(item["direction"]);
                var normal = view.ViewDirection.Normalize();
                vector = d - normal.Multiply(normal.DotProduct(d));
            }
            else
            {
                throw new ArgumentException("linear mode needs 'along' {start, end} or 'direction' {x, y}.");
            }

            if (vector.GetLength() < 1e-9)
                throw new ArgumentException("The array direction is zero in this view.");
            var unit = vector.Normalize();

            var count = item.Value<int?>("count");
            spacingMm = item.Value<double?>("spacing");
            var includeOriginal = item.Value<bool?>("includeOriginal") ?? true;
            fit = item.Value<string>("fit")
                  ?? (length != null && spacingMm != null && count == null ? "fill"
                      : length != null && count != null && spacingMm == null ? "count"
                      : "spacing");

            if (spacingMm != null && spacingMm <= 0)
                throw new ArgumentException("'spacing' must be positive (mm).");
            if (count != null && count < 1)
                throw new ArgumentException("'count' must be at least 1.");

            double spacing;
            int copies;
            switch (fit)
            {
                case "spacing":
                    if (spacingMm == null || count == null)
                        throw new ArgumentException("fit 'spacing' needs 'spacing' and 'count'.");
                    spacing = DocumentationUtils.MmToFeet(spacingMm.Value);
                    copies = includeOriginal ? count.Value - 1 : count.Value;
                    break;
                case "count":
                    if (count == null || length == null)
                        throw new ArgumentException("fit 'count' needs 'count' and 'along'.");
                    var intervals = includeOriginal ? count.Value - 1 : count.Value;
                    if (intervals < 1)
                        throw new ArgumentException("fit 'count' needs at least 2 members.");
                    spacing = length.Value / intervals;
                    spacingMm = DocumentationUtils.FeetToMm(spacing);
                    copies = intervals;
                    break;
                case "fill":
                    if (spacingMm == null || length == null)
                        throw new ArgumentException("fit 'fill' needs 'spacing' and 'along'.");
                    spacing = DocumentationUtils.MmToFeet(spacingMm.Value);
                    copies = (int)Math.Floor(length.Value / spacing + 1e-6);
                    break;
                default:
                    throw new ArgumentException($"Unknown fit '{fit}'. Use spacing, count or fill.");
            }

            if (copies > MaxSteps)
                throw new ArgumentException($"{copies} copies requested; at most {MaxSteps} per item.");
            return Enumerable.Range(1, Math.Max(0, copies)).Select(k => unit * (spacing * k)).ToList();
        }

        /// <summary>Offsets that bring the reference point (explicit, first location point, or bbox centre) to each point.</summary>
        private static List<XYZ> PointOffsets(View view, List<Element> elements, JObject item)
        {
            var points = DetailGeometry.ReadPoints(view, item["points"], "points", 1);
            var reference = item["reference"] != null
                ? DetailGeometry.ReadPoint(view, item["reference"], "reference")
                : DetailGeometry.Project(view, ReferencePoint(view, elements[0]));
            return points.Select(p => p - reference).ToList();
        }

        private static XYZ ReferencePoint(View view, Element element)
        {
            if (element.Location is LocationPoint point)
                return point.Point;
            var box = element.get_BoundingBox(view) ?? element.get_BoundingBox(null)
                      ?? throw new ArgumentException($"Element {element.Id.GetValue()} has no location; pass 'reference'.");
            return (box.Min + box.Max) / 2;
        }
    }
}
