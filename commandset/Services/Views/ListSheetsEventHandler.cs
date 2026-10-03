using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Lists sheets with their title blocks, viewports and schedule
    ///     instances, plus the title block types available for new sheets.
    ///     Sheet coordinates are millimetres in sheet space. Read-only.
    /// </summary>
    public class ListSheetsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "List Sheets";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var numberContains = parameters.Value<string>("numberContains");
            var includeContents = parameters.Value<bool?>("includeContents") ?? true;
            var limit = Math.Max(1, Math.Min(parameters.Value<int?>("limit") ?? 500, 5000));

            var matching = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsTemplate)
                .Where(s => string.IsNullOrEmpty(numberContains)
                            || s.SheetNumber.IndexOf(numberContains, StringComparison.OrdinalIgnoreCase) >= 0
                            || s.Name.IndexOf(numberContains, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sheets = matching.Take(limit).Select(sheet => new
            {
                id = sheet.Id.GetValue(),
                number = sheet.SheetNumber,
                name = sheet.Name,
                isPlaceholder = sheet.IsPlaceholder,
                titleBlocks = includeContents ? TitleBlocksOn(doc, sheet) : null,
                viewports = includeContents
                    ? sheet.GetAllViewports()
                        .Select(id => doc.GetElement(id) as Viewport)
                        .Where(vp => vp != null)
                        .Select(vp => new
                        {
                            viewportId = vp.Id.GetValue(),
                            viewId = vp.ViewId.GetValue(),
                            viewName = DocumentationUtils.ViewName(doc, vp.ViewId),
                            center = DocumentationUtils.PointToMm(vp.GetBoxCenter())
                        })
                        .ToList()
                    : null,
                schedules = includeContents
                    ? new FilteredElementCollector(doc, sheet.Id)
                        .OfClass(typeof(ScheduleSheetInstance))
                        .Cast<ScheduleSheetInstance>()
                        .Where(i => !i.IsTitleblockRevisionSchedule)
                        .Select(i => new
                        {
                            instanceId = i.Id.GetValue(),
                            scheduleId = i.ScheduleId.GetValue(),
                            scheduleName = DocumentationUtils.ViewName(doc, i.ScheduleId),
                            origin = DocumentationUtils.PointToMm(i.Point)
                        })
                        .ToList()
                    : null
            }).ToList();

            var titleBlockTypes = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType()
                .Cast<FamilySymbol>()
                .OrderBy(t => t.FamilyName)
                .ThenBy(t => t.Name)
                .Select(t => new { id = t.Id.GetValue(), familyName = t.FamilyName, typeName = t.Name })
                .ToList();

            return Ok($"Found {matching.Count} sheets.", new
            {
                total = matching.Count,
                truncated = matching.Count > sheets.Count,
                sheets,
                titleBlockTypes
            });
        }

        private static object TitleBlocksOn(Document doc, ViewSheet sheet)
        {
            return new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .Select(tb => new
                {
                    id = tb.Id.GetValue(),
                    typeId = tb.Symbol.Id.GetValue(),
                    familyName = tb.Symbol.FamilyName,
                    typeName = tb.Symbol.Name
                })
                .ToList();
        }
    }
}
