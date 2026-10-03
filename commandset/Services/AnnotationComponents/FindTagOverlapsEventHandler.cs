using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Reports annotations whose view-plane extents overlap, optionally
    ///     also against model elements. Extents are the elements' bounding
    ///     boxes projected onto the view plane; overlap areas are reported in
    ///     paper square millimetres. Read-only: no transaction is opened.
    /// </summary>
    public class FindTagOverlapsEventHandler : JsonParameterEventHandler
    {
        private const int DefaultMaxPairs = 500;

        public override string GetName() => "Find Tag Overlaps";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var view = DocumentationUtils.GetElement<View>(doc, DocumentationUtils.ReadId(parameters, "viewId"))
                       ?? uiDoc.ActiveView;
            if (view.IsTemplate || view is ViewSchedule || view is View3D && !((View3D)view).IsLocked)
                return Fail($"View '{view.Name}' does not support annotation overlap checks.");

            var scale = Math.Max(1, view.Scale);
            var paperMmPerFoot = DocumentationUtils.MmPerFoot / scale;
            var toleranceFeet = Math.Max(0, parameters.Value<double?>("toleranceMm") ?? 0) / paperMmPerFoot;
            var maxPairs = Math.Max(1, Math.Min(parameters.Value<int?>("maxPairs") ?? DefaultMaxPairs, 10000));

            var annotations = CollectAnnotations(doc, view, parameters["categories"]?.ToObject<List<string>>())
                .Select(e => Extent.Of(e, view, toleranceFeet / 2))
                .Where(x => x != null)
                .ToList();

            var models = new List<Extent>();
            if (parameters.Value<bool?>("includeModelElements") == true)
            {
                var modelCategories = parameters["modelCategories"]?.ToObject<List<string>>() ?? new List<string>();
                if (modelCategories.Count == 0)
                    return Fail("'modelCategories' is required when includeModelElements is true.");
                foreach (var name in modelCategories)
                {
                    var category = DocumentationUtils.ResolveCategory(doc, name)
                                   ?? throw new ArgumentException($"Category '{name}' not found.");
                    models.AddRange(new FilteredElementCollector(doc, view.Id)
                        .OfCategoryId(category.Id)
                        .WhereElementIsNotElementType()
                        .ToElements()
                        .Select(e => Extent.Of(e, view, toleranceFeet / 2))
                        .Where(x => x != null));
                }
            }

            var pairs = new List<(Extent a, Extent b, double area)>();
            Sweep(annotations, annotations, true, pairs);
            if (models.Count > 0)
                Sweep(annotations, models, false, pairs);

            var ordered = pairs
                .OrderByDescending(p => p.area)
                .ThenBy(p => p.a.Id)
                .ThenBy(p => p.b.Id)
                .ToList();

            return Ok($"Found {ordered.Count} overlapping pairs among {annotations.Count} annotations in '{view.Name}'.", new
            {
                viewId = view.Id.GetValue(),
                viewScale = scale,
                annotationCount = annotations.Count,
                modelElementCount = models.Count,
                totalPairs = ordered.Count,
                truncated = ordered.Count > maxPairs,
                pairs = ordered.Take(maxPairs).Select(p => new
                {
                    aId = p.a.Id,
                    bId = p.b.Id,
                    aCategory = p.a.Category,
                    bCategory = p.b.Category,
                    overlapAreaPaperMm2 = Math.Round(p.area * paperMmPerFoot * paperMmPerFoot, 2)
                }).ToList()
            });
        }

        /// <summary>
        ///     Sweep-and-prune over extents sorted by minimum U. When
        ///     <paramref name="same" /> is true the two lists are the same and
        ///     each unordered pair is reported once.
        /// </summary>
        private static void Sweep(List<Extent> left, List<Extent> right, bool same,
            List<(Extent a, Extent b, double area)> pairs)
        {
            var sortedRight = right.OrderBy(x => x.MinU).ToList();
            var sortedLeft = same ? sortedRight : left.OrderBy(x => x.MinU).ToList();

            for (var i = 0; i < sortedLeft.Count; i++)
            {
                var a = sortedLeft[i];
                for (var j = same ? i + 1 : 0; j < sortedRight.Count; j++)
                {
                    var b = sortedRight[j];
                    // Right is sorted by MinU, so no later extent can overlap a in U.
                    if (b.MinU > a.MaxU)
                        break;

                    if (a.Id == b.Id)
                        continue;

                    var width = Math.Min(a.MaxU, b.MaxU) - Math.Max(a.MinU, b.MinU);
                    var height = Math.Min(a.MaxV, b.MaxV) - Math.Max(a.MinV, b.MinV);
                    if (width > 0 && height > 0)
                        pairs.Add(same && b.Id < a.Id ? (b, a, width * height) : (a, b, width * height));
                }
            }
        }

        private static List<Element> CollectAnnotations(Document doc, View view, List<string> categories)
        {
            var elements = new List<Element>();
            if (categories != null && categories.Count > 0)
            {
                foreach (var name in categories)
                {
                    var category = DocumentationUtils.ResolveCategory(doc, name)
                                   ?? throw new ArgumentException($"Category '{name}' not found.");
                    elements.AddRange(new FilteredElementCollector(doc, view.Id)
                        .OfCategoryId(category.Id)
                        .WhereElementIsNotElementType()
                        .ToElements());
                }
            }
            else
            {
                elements.AddRange(new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).ToElements());
                elements.AddRange(new FilteredElementCollector(doc, view.Id).OfClass(typeof(SpatialElementTag)).ToElements());
                elements.AddRange(new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)).ToElements());
            }

            return elements
                .Where(e => e.OwnerViewId == view.Id || categories != null && categories.Count > 0)
                .GroupBy(e => e.Id.GetValue())
                .Select(g => g.First())
                .ToList();
        }

        private class Extent
        {
            public long Id { get; private set; }
            public string Category { get; private set; }
            public double MinU { get; private set; }
            public double MaxU { get; private set; }
            public double MinV { get; private set; }
            public double MaxV { get; private set; }

            /// <summary>Projects the element's view bounding box onto the view plane, inflated by <paramref name="pad" /> feet.</summary>
            public static Extent Of(Element element, View view, double pad)
            {
                var box = element.get_BoundingBox(view);
                if (box == null || !box.Enabled)
                    return null;

                var transform = box.Transform ?? Transform.Identity;
                var right = view.RightDirection;
                var up = view.UpDirection;
                double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;

                foreach (var x in new[] { box.Min.X, box.Max.X })
                foreach (var y in new[] { box.Min.Y, box.Max.Y })
                foreach (var z in new[] { box.Min.Z, box.Max.Z })
                {
                    var p = transform.OfPoint(new XYZ(x, y, z));
                    var u = p.DotProduct(right);
                    var v = p.DotProduct(up);
                    minU = Math.Min(minU, u);
                    maxU = Math.Max(maxU, u);
                    minV = Math.Min(minV, v);
                    maxV = Math.Max(maxV, v);
                }

                return new Extent
                {
                    Id = element.Id.GetValue(),
                    Category = element.Category?.Name,
                    MinU = minU - pad,
                    MaxU = maxU + pad,
                    MinV = minV - pad,
                    MaxV = maxV + pad
                };
            }
        }
    }
}
