using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.AnnotationComponents;
using RevitMCPCommandSet.Services.Library;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Viewport helpers shared by place_viewport and update_viewports:
    ///     detail numbers with collision checks, anchoring a view point to a
    ///     sheet point, and viewport type resolution. Sheet values are feet
    ///     internally and millimetres on the wire.
    /// </summary>
    internal static class ViewportUtils
    {
        public static string DetailNumber(Viewport viewport) => DetailReferenceUtils.ViewportDetailNumber(viewport);

        /// <summary>Writes the Detail Number parameter or throws.</summary>
        public static void WriteDetailNumber(Viewport viewport, string number)
        {
            var parameter = viewport.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER);
            if (parameter == null || parameter.IsReadOnly)
                throw new InvalidOperationException("The viewport's Detail Number cannot be set.");
            if (!parameter.Set(number))
                throw new InvalidOperationException($"Detail Number '{number}' could not be set.");
        }

        /// <summary>
        ///     The viewport on <paramref name="sheetId" /> (other than the excluded
        ///     ids) whose detail number equals <paramref name="number" />, or null.
        /// </summary>
        public static Viewport FindHolder(Document doc, ElementId sheetId, string number, ISet<long> exclude)
        {
            if (!(doc.GetElement(sheetId) is ViewSheet sheet))
                return null;
            var wanted = DetailReferenceUtils.Normalize(number);
            return sheet.GetAllViewports()
                .Where(id => !exclude.Contains(id.GetValue()))
                .Select(id => doc.GetElement(id) as Viewport)
                .FirstOrDefault(vp => vp != null && DetailReferenceUtils.Normalize(DetailNumber(vp)) == wanted);
        }

        /// <summary>Sets one viewport's detail number, failing when another viewport on its sheet holds it.</summary>
        public static void AssignDetailNumber(Document doc, Viewport viewport, string number)
        {
            var holder = FindHolder(doc, viewport.SheetId, number, new HashSet<long> { viewport.Id.GetValue() });
            if (holder != null)
                throw new InvalidOperationException(
                    $"Detail number '{number}' is already used on this sheet by viewport {holder.Id.GetValue()} " +
                    $"('{DocumentationUtils.ViewName(viewport.Document, holder.ViewId)}').");
            WriteDetailNumber(viewport, number);
        }

        /// <summary>Resolves a viewport type valid for the viewport by id (wins) or name.</summary>
        public static ElementId ResolveType(Document doc, Viewport viewport, long? typeId, string typeName)
        {
            var valid = viewport.GetValidTypes();
            if (typeId != null)
            {
                var id = typeId.Value.ToRevitElementId();
                if (!valid.Contains(id))
                    throw new ArgumentException($"viewportTypeId {typeId} is not a valid viewport type.");
                return id;
            }

            if (string.IsNullOrWhiteSpace(typeName))
                return null;
            return TypeNameResolver.Resolve(valid.Select(id => doc.GetElement(id)), typeName, "Viewport type").Id;
        }

        /// <summary>
        ///     Moves the viewport so the view point (feet, view model coordinates)
        ///     lands on the sheet point (feet). Returns the method used and the
        ///     achieved sheet position in mm.
        /// </summary>
        public static JObject Anchor(Document doc, Viewport viewport, XYZ viewPoint, XYZ sheetPoint)
        {
            var view = doc.GetElement(viewport.ViewId) as View
                       ?? throw new InvalidOperationException("The viewport's view was not found.");
            var point = DetailGeometry.Project(view, viewPoint);

            var before = viewport.GetBoxCenter();
            var current = ViewPointToSheet(viewport, view, point, out _);
            viewport.SetBoxCenter(before + new XYZ(sheetPoint.X - current.X, sheetPoint.Y - current.Y, 0));
            doc.Regenerate();

            var achieved = ViewPointToSheet(viewport, view, point, out var method);
            var after = viewport.GetBoxCenter();
            return new JObject
            {
                ["method"] = method,
                ["achievedSheetPoint"] = new JObject
                {
                    ["x"] = DocumentationUtils.FeetToMm(achieved.X),
                    ["y"] = DocumentationUtils.FeetToMm(achieved.Y)
                },
                ["errorMm"] = DocumentationUtils.FeetToMm(
                    Math.Sqrt(Math.Pow(achieved.X - sheetPoint.X, 2) + Math.Pow(achieved.Y - sheetPoint.Y, 2))),
                ["movedMm"] = new JObject
                {
                    ["x"] = DocumentationUtils.FeetToMm(after.X - before.X),
                    ["y"] = DocumentationUtils.FeetToMm(after.Y - before.Y)
                }
            };
        }

        /// <summary>
        ///     Sheet position (feet) of a view point. Uses the model-to-projection
        ///     and projection-to-sheet transforms when Revit provides them (2023+;
        ///     drafting viewports throw), otherwise box centre - centre of the
        ///     view outline (paper feet) + point / scale along the view axes.
        /// </summary>
        public static XYZ ViewPointToSheet(Viewport viewport, View view, XYZ point, out string method)
        {
#if REVIT2023_OR_GREATER
            try
            {
                var transforms = view.GetModelToProjectionTransforms();
                if (transforms != null && transforms.Count > 0)
                {
                    var projected = transforms[0].GetModelToProjectionTransform().OfPoint(point);
                    var onSheet = viewport.GetProjectionToSheetTransform().OfPoint(projected);
                    method = "transform";
                    return new XYZ(onSheet.X, onSheet.Y, 0);
                }
            }
            catch (Exception)
            {
                // Drafting views and some other view types expose no transforms; use the outline.
            }
#endif
            if (viewport.Rotation != ViewportRotation.None)
                throw new InvalidOperationException(
                    "Cannot anchor a rotated viewport without Revit's view transforms (Revit 2023+, non-drafting views).");

            var outline = view.Outline;
            var scale = view.Scale > 0 ? view.Scale : 1;
            var box = viewport.GetBoxCenter();
            var u = point.DotProduct(view.RightDirection) / scale;
            var v = point.DotProduct(view.UpDirection) / scale;
            method = "outline";
            return new XYZ(
                box.X - (outline.Min.U + outline.Max.U) / 2 + u,
                box.Y - (outline.Min.V + outline.Max.V) / 2 + v,
                0);
        }

        /// <summary>Reads an {x, y} millimetre point as feet with z = 0, or null when absent.</summary>
        public static XYZ ReadPoint2(JToken token, string what)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;
            var point = DocumentationUtils.ReadPointMm(token)
                        ?? throw new ArgumentException($"'{what}' must be a point {{x, y}} in mm.");
            return new XYZ(point.X, point.Y, 0);
        }

        /// <summary>Reads anchor {viewPoint, sheetPoint} or returns false when absent.</summary>
        public static bool TryReadAnchor(JToken item, out XYZ viewPoint, out XYZ sheetPoint)
        {
            viewPoint = sheetPoint = null;
            var anchor = item?["anchor"];
            if (anchor == null || anchor.Type == JTokenType.Null)
                return false;
            viewPoint = ReadPoint2(anchor["viewPoint"], "anchor.viewPoint")
                        ?? throw new ArgumentException("'anchor.viewPoint' is required (view model mm).");
            sheetPoint = ReadPoint2(anchor["sheetPoint"], "anchor.sheetPoint")
                         ?? throw new ArgumentException("'anchor.sheetPoint' is required (sheet mm).");
            return true;
        }
    }
}
