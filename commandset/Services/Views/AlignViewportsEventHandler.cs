using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Aligns viewports to a reference viewport, possibly on other sheets.
    ///     ViewOrigin mode moves each viewport so a shared model point (the
    ///     centre of the reference view's crop box) lands at the same sheet
    ///     position, which requires matching scale and view direction and
    ///     Revit 2023 or later. Center mode copies the reference box centre.
    /// </summary>
    public class AlignViewportsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Align Viewports";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var reference = DocumentationUtils.GetElement<Viewport>(doc, DocumentationUtils.ReadId(parameters, "referenceViewportId"))
                            ?? throw new ArgumentException("'referenceViewportId' does not refer to a viewport.");
            var items = DocumentationUtils.RequireArray(parameters, "viewportIds");
            var byOrigin = !string.Equals(parameters.Value<string>("align") ?? "ViewOrigin", "Center",
                StringComparison.OrdinalIgnoreCase);
            if (byOrigin && !string.Equals(parameters.Value<string>("align") ?? "ViewOrigin", "ViewOrigin",
                    StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("'align' must be Center or ViewOrigin.");

#if REVIT2023_OR_GREATER
            var referenceView = (View)doc.GetElement(reference.ViewId);
            var anchor = byOrigin ? AnchorPoint(referenceView) : null;
            var referenceSheetPoint = byOrigin ? ModelToSheet(reference, referenceView, anchor) : null;
#else
            if (byOrigin)
                return Fail("align=ViewOrigin requires Revit 2023 or later; use align=Center.");
#endif

            var results = DocumentationUtils.RunBatch(doc, "MCP: Align Viewports", items, token =>
            {
                var viewport = doc.GetElement(token.Value<long>().ToRevitElementId()) as Viewport
                               ?? throw new ArgumentException($"{token} is not a viewport.");
                if (viewport.Id == reference.Id)
                    throw new ArgumentException("Viewport is the reference viewport.");

                var before = viewport.GetBoxCenter();
                if (!byOrigin)
                {
                    viewport.SetBoxCenter(reference.GetBoxCenter());
                }
                else
                {
#if REVIT2023_OR_GREATER
                    var view = (View)doc.GetElement(viewport.ViewId);
                    if (view.Scale != referenceView.Scale)
                        throw new InvalidOperationException(
                            $"Scale 1:{view.Scale} differs from the reference 1:{referenceView.Scale}.");
                    if (!view.ViewDirection.IsAlmostEqualTo(referenceView.ViewDirection))
                        throw new InvalidOperationException("View direction differs from the reference view.");

                    var delta = referenceSheetPoint - ModelToSheet(viewport, view, anchor);
                    viewport.SetBoxCenter(before + new XYZ(delta.X, delta.Y, 0));
#endif
                }

                var after = viewport.GetBoxCenter();
                return new
                {
                    viewportId = viewport.Id.GetValue(),
                    sheetId = viewport.SheetId.GetValue(),
                    movedMm = new
                    {
                        x = DocumentationUtils.FeetToMm(after.X - before.X),
                        y = DocumentationUtils.FeetToMm(after.Y - before.Y)
                    },
                    center = DocumentationUtils.PointToMm(after)
                };
            });

            return Ok($"Aligned {results.Count(r => r.Value<bool>("success"))} of {results.Count} viewports.",
                DocumentationUtils.Summarize(results));
        }

#if REVIT2023_OR_GREATER
        /// <summary>Model point at the centre of the view's crop box.</summary>
        private static XYZ AnchorPoint(View view)
        {
            var box = view.CropBox;
            return box.Transform.OfPoint((box.Min + box.Max) / 2);
        }

        private static XYZ ModelToSheet(Viewport viewport, View view, XYZ modelPoint)
        {
            var transforms = view.GetModelToProjectionTransforms();
            if (transforms == null || transforms.Count == 0)
                throw new InvalidOperationException($"View '{view.Name}' has no model-to-projection transform.");
            var projected = transforms[0].GetModelToProjectionTransform().OfPoint(modelPoint);
            return viewport.GetProjectionToSheetTransform().OfPoint(projected);
        }
#endif
    }
}
