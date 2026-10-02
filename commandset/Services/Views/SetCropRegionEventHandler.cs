using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Sets a view's rectangular crop region, from an explicit rectangle or
    ///     by fitting it to elements, and toggles crop and annotation-crop
    ///     settings. Rectangles are model XY (mm) for plans and crop-box local
    ///     coordinates (mm; x along the view's right direction, y up) for all
    ///     other views. The crop depth (near/far clip) is preserved.
    /// </summary>
    public class SetCropRegionEventHandler : JsonParameterEventHandler
    {
        private const double DefaultMarginMm = 500;

        public override string GetName() => "Set Crop Region";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "views");
            var results = DocumentationUtils.RunBatch(doc, "MCP: Set Crop Regions", items, item => SetCrop(doc, (JObject)item));
            return Ok($"Updated {results.Count(r => r.Value<bool>("success"))} of {results.Count} views.",
                DocumentationUtils.Summarize(results));
        }

        private static object SetCrop(Document doc, JObject item)
        {
            var view = DocumentationUtils.GetElement<View>(doc, DocumentationUtils.ReadId(item, "viewId"))
                       ?? throw new ArgumentException("'viewId' does not refer to a view.");
            if (view.IsTemplate || view is ViewSheet || view is ViewSchedule || view.ViewType == ViewType.DraftingView
                || view.ViewType == ViewType.Legend)
                throw new ArgumentException($"View '{view.Name}' ({view.ViewType}) has no model crop region.");

            var rectangle = item["rectangle"] as JObject;
            var fitIds = item["fitToElementIds"] as JArray;
            if (rectangle != null && fitIds != null && fitIds.Count > 0)
                throw new ArgumentException("Give either 'rectangle' or 'fitToElementIds', not both.");

            // Turning the crop on first lets the new extents take effect immediately.
            var cropActive = item.Value<bool?>("cropActive");
            if (cropActive != null)
                view.CropBoxActive = cropActive.Value;

            var current = view.CropBox;
            var transform = current.Transform;
            double? minX = null, minY = null, maxX = null, maxY = null;

            if (rectangle != null)
            {
                var a = DocumentationUtils.ReadPointMm(rectangle["min"])
                        ?? throw new ArgumentException("'rectangle.min' is required (mm).");
                var b = DocumentationUtils.ReadPointMm(rectangle["max"])
                        ?? throw new ArgumentException("'rectangle.max' is required (mm).");
                if (view is ViewPlan)
                {
                    // Model XY corners: project all four into crop-box local space.
                    var inverse = transform.Inverse;
                    var corners = new[]
                    {
                        new XYZ(a.X, a.Y, 0), new XYZ(b.X, a.Y, 0), new XYZ(a.X, b.Y, 0), new XYZ(b.X, b.Y, 0)
                    }.Select(p => inverse.OfPoint(p)).ToList();
                    (minX, maxX) = (corners.Min(p => p.X), corners.Max(p => p.X));
                    (minY, maxY) = (corners.Min(p => p.Y), corners.Max(p => p.Y));
                }
                else
                {
                    (minX, maxX) = (Math.Min(a.X, b.X), Math.Max(a.X, b.X));
                    (minY, maxY) = (Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y));
                }
            }
            else if (fitIds != null && fitIds.Count > 0)
            {
                var margin = DocumentationUtils.MmToFeet(item.Value<double?>("marginMm") ?? DefaultMarginMm);
                var inverse = transform.Inverse;
                var points = new List<XYZ>();
                var missing = new List<long>();
                foreach (var id in fitIds.Select(t => t.Value<long>()))
                {
                    var box = doc.GetElement(id.ToRevitElementId())?.get_BoundingBox(null);
                    if (box == null)
                    {
                        missing.Add(id);
                        continue;
                    }

                    foreach (var corner in Corners(box))
                        points.Add(inverse.OfPoint(corner));
                }

                if (points.Count == 0)
                    throw new ArgumentException("None of 'fitToElementIds' has a model bounding box.");
                if (missing.Count > 0)
                    throw new ArgumentException($"Elements without a bounding box: {string.Join(", ", missing)}.");

                (minX, maxX) = (points.Min(p => p.X) - margin, points.Max(p => p.X) + margin);
                (minY, maxY) = (points.Min(p => p.Y) - margin, points.Max(p => p.Y) + margin);
            }

            if (minX != null)
            {
                if (maxX - minX < 1e-3 || maxY - minY < 1e-3)
                    throw new ArgumentException("Crop rectangle must have a positive width and height.");

                var shapeManager = view.GetCropRegionShapeManager();
                if (shapeManager != null && shapeManager.ShapeSet)
                    shapeManager.RemoveCropRegionShape();

                view.CropBox = new BoundingBoxXYZ
                {
                    Transform = transform,
                    Min = new XYZ(minX.Value, minY.Value, current.Min.Z),
                    Max = new XYZ(maxX.Value, maxY.Value, current.Max.Z)
                };
            }

            var cropVisible = item.Value<bool?>("cropVisible");
            if (cropVisible != null)
                view.CropBoxVisible = cropVisible.Value;

            var annotationCrop = item.Value<bool?>("annotationCrop");
            if (annotationCrop != null)
            {
                var parameter = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE)
                                ?? throw new InvalidOperationException($"View '{view.Name}' has no annotation crop.");
                parameter.Set(annotationCrop.Value ? 1 : 0);
            }

            if (item["annotationOffsetsMm"] is JObject offsets)
            {
                var manager = view.GetCropRegionShapeManager()
                              ?? throw new InvalidOperationException($"View '{view.Name}' has no annotation crop.");
                if (offsets.Value<double?>("left") is double left)
                    manager.LeftAnnotationCropOffset = DocumentationUtils.MmToFeet(left);
                if (offsets.Value<double?>("right") is double right)
                    manager.RightAnnotationCropOffset = DocumentationUtils.MmToFeet(right);
                if (offsets.Value<double?>("top") is double top)
                    manager.TopAnnotationCropOffset = DocumentationUtils.MmToFeet(top);
                if (offsets.Value<double?>("bottom") is double bottom)
                    manager.BottomAnnotationCropOffset = DocumentationUtils.MmToFeet(bottom);
            }

            var result = view.CropBox;
            return new
            {
                viewId = view.Id.GetValue(),
                name = view.Name,
                cropActive = view.CropBoxActive,
                cropVisible = view.CropBoxVisible,
                localMin = new { x = DocumentationUtils.FeetToMm(result.Min.X), y = DocumentationUtils.FeetToMm(result.Min.Y) },
                localMax = new { x = DocumentationUtils.FeetToMm(result.Max.X), y = DocumentationUtils.FeetToMm(result.Max.Y) },
                widthMm = DocumentationUtils.FeetToMm(result.Max.X - result.Min.X),
                heightMm = DocumentationUtils.FeetToMm(result.Max.Y - result.Min.Y)
            };
        }

        private static IEnumerable<XYZ> Corners(BoundingBoxXYZ box)
        {
            var transform = box.Transform ?? Transform.Identity;
            foreach (var x in new[] { box.Min.X, box.Max.X })
            foreach (var y in new[] { box.Min.Y, box.Max.Y })
            foreach (var z in new[] { box.Min.Z, box.Max.Z })
                yield return transform.OfPoint(new XYZ(x, y, z));
        }
    }
}
