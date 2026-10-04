using System.IO;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Activates a view (default: the active view), optionally zooms it to fit,
    ///     refreshes it and exports the visible region to a PNG so an agent can see
    ///     the result of its work. Changes only UI state; no transaction.
    /// </summary>
    public class CaptureViewEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Capture View";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var previous = uiDoc.ActiveView;
            var restoreActiveView = parameters.Value<bool?>("restoreActiveView") ?? false;
            var openBefore = new HashSet<long>(uiDoc.GetOpenUIViews().Select(v => v.ViewId.GetValue()));

            var viewId = parameters.Value<long?>("viewId");
            if (viewId.HasValue && viewId.Value != previous.Id.GetValue())
            {
                if (!(doc.GetElement(viewId.Value.ToRevitElementId()) is View view) || view.IsTemplate)
                    return Fail($"View {viewId.Value} was not found or is a view template.");
                uiDoc.ActiveView = view;
            }

            try
            {
                return Capture(uiDoc, parameters, restoreActiveView && uiDoc.ActiveView.Id != previous.Id);
            }
            finally
            {
                if (restoreActiveView)
                    RestoreActiveView(uiDoc, previous, openBefore);
            }
        }

        /// <summary>
        ///     Switches back to the view that was active before the capture and
        ///     closes the captured view's window if the capture opened it, so the
        ///     captured view can be deleted afterwards (Revit cannot delete the
        ///     active view).
        /// </summary>
        private static void RestoreActiveView(UIDocument uiDoc, View previous, HashSet<long> openBefore)
        {
            var captured = uiDoc.ActiveView;
            if (captured.Id == previous.Id)
                return;
            uiDoc.ActiveView = previous;
            if (openBefore.Contains(captured.Id.GetValue()))
                return;
            try
            {
                uiDoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == captured.Id)?.Close();
            }
            catch (Exception)
            {
                // Leaving the window open is harmless; the view is no longer active.
            }
        }

        private AIResult<object> Capture(UIDocument uiDoc, JObject parameters, bool willRestore)
        {
            var doc = uiDoc.Document;
            var active = uiDoc.ActiveView;
            var zoomToFit = parameters.Value<bool?>("zoomToFit") ?? true;
            var zoomed = false;
            if (zoomToFit)
            {
                var uiView = uiDoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == active.Id);
                if (uiView != null)
                {
                    uiView.ZoomToFit();
                    zoomed = true;
                }
            }

            uiDoc.RefreshActiveView();

            var requestedFolder = parameters.Value<string>("folder");
            var folder = ExportFileUtils.PrepareFolder(string.IsNullOrWhiteSpace(requestedFolder)
                ? Path.Combine(Path.GetTempPath(), "revit-mcp-captures")
                : requestedFolder);
            var prefix = "capture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");

            // A view activated in this same call is not drawn yet, so its visible
            // region exports blank; zoom-to-fit renders the full extents instead,
            // which needs no on-screen graphics.
            var options = new ImageExportOptions
            {
                ExportRange = zoomToFit ? ExportRange.SetOfViews : ExportRange.VisibleRegionOfCurrentView,
                FilePath = Path.Combine(folder, prefix),
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = Math.Max(64, Math.Min(4096, parameters.Value<int?>("pixelSize") ?? 1600)),
                FitDirection = FitDirectionType.Horizontal
            };
            if (zoomToFit)
                options.SetViewsAndSheets(new List<ElementId> { active.Id });

            var before = ExportFileUtils.Snapshot(folder);
            doc.ExportImage(options);
            var file = ExportFileUtils.ChangedFiles(folder, before)
                .FirstOrDefault(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            if (file == null)
                return Fail("Revit did not write the capture image.");

            return Ok($"Captured '{active.Name}'{(zoomed ? " (zoomed to fit)" : "")}.", new
            {
                viewId = active.Id.GetValue(),
                viewName = active.Name,
                viewType = active.ViewType.ToString(),
                scale = active.ViewType == ViewType.DrawingSheet || active.ViewType == ViewType.Schedule
                    ? (int?)null
                    : active.Scale,
                zoomedToFit = zoomed,
                restoredActiveView = willRestore,
                file
            });
        }
    }
}
