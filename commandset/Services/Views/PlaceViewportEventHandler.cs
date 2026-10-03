using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Places views on sheets. Ordinary views become viewports centred on
    ///     <c>center</c>; schedules become schedule sheet instances whose
    ///     top-left corner is at <c>center</c>. Sheet coordinates are mm and
    ///     default to the centre of the sheet outline.
    /// </summary>
    public class PlaceViewportEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Place Viewport";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "viewports");
            var results = DocumentationUtils.RunBatch(doc, "MCP: Place Views On Sheets", items, item => Place(doc, (JObject)item));
            return Ok($"Placed {results.Count(r => r.Value<bool>("success"))} of {results.Count} views.",
                DocumentationUtils.Summarize(results));
        }

        private static object Place(Document doc, JObject item)
        {
            var sheet = DocumentationUtils.GetElement<ViewSheet>(doc, DocumentationUtils.ReadId(item, "sheetId"))
                        ?? throw new ArgumentException("'sheetId' does not refer to a sheet.");
            var view = DocumentationUtils.GetElement<View>(doc, DocumentationUtils.ReadId(item, "viewId"))
                       ?? throw new ArgumentException("'viewId' does not refer to a view.");
            if (view.IsTemplate)
                throw new ArgumentException($"'{view.Name}' is a view template and cannot be placed.");

            var point = DocumentationUtils.ReadPointMm(item["center"]) ?? SheetCenter(sheet);
            point = new XYZ(point.X, point.Y, 0);

            if (view is ViewSchedule schedule)
            {
                if (schedule.IsTitleblockRevisionSchedule || schedule.IsInternalKeynoteSchedule)
                    throw new ArgumentException($"Schedule '{schedule.Name}' cannot be placed on a sheet.");
                var instance = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, point);
                return new
                {
                    kind = "schedule",
                    instanceId = instance.Id.GetValue(),
                    sheetId = sheet.Id.GetValue(),
                    viewId = view.Id.GetValue()
                };
            }

            if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                throw new InvalidOperationException(
                    $"View '{view.Name}' cannot be added to sheet {sheet.SheetNumber}; it may already be placed on a sheet.");

            var viewport = Viewport.Create(doc, sheet.Id, view.Id, point);

            var viewportTypeId = DocumentationUtils.ReadId(item, "viewportTypeId");
            if (viewportTypeId != null)
                viewport.ChangeTypeId(viewportTypeId.Value.ToRevitElementId());

            var rotation = item.Value<string>("rotation");
            if (!string.IsNullOrWhiteSpace(rotation))
                viewport.Rotation = DocumentationUtils.ParseEnum(rotation, ViewportRotation.None);

            return new
            {
                kind = "viewport",
                viewportId = viewport.Id.GetValue(),
                sheetId = sheet.Id.GetValue(),
                viewId = view.Id.GetValue(),
                center = DocumentationUtils.PointToMm(viewport.GetBoxCenter())
            };
        }

        private static XYZ SheetCenter(ViewSheet sheet)
        {
            var outline = sheet.Outline;
            return new XYZ((outline.Min.U + outline.Max.U) / 2, (outline.Min.V + outline.Max.V) / 2, 0);
        }
    }
}
