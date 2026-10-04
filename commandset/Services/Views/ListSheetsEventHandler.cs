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
    /// <remarks>
    ///     Sheet contents are gathered with one document-wide collector per
    ///     element class, grouped by owner sheet. View-scoped collectors
    ///     (<c>new FilteredElementCollector(doc, sheet.Id)</c>) evaluate
    ///     visibility per sheet and made large sheet sets time out.
    /// </remarks>
    public class ListSheetsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "List Sheets";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var numberContains = parameters.Value<string>("numberContains");
            var includeContents = parameters.Value<bool?>("includeContents") ?? true;
            var offset = Math.Max(0, parameters.Value<int?>("offset") ?? 0);
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

            var page = matching.Skip(offset).Take(limit).ToList();

            ILookup<long, FamilyInstance> titleBlocksBySheet = null;
            ILookup<long, ScheduleSheetInstance> schedulesBySheet = null;
            if (includeContents && page.Count > 0)
            {
                titleBlocksBySheet = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>()
                    .ToLookup(tb => tb.OwnerViewId.GetValue());
                schedulesBySheet = new FilteredElementCollector(doc)
                    .OfClass(typeof(ScheduleSheetInstance))
                    .Cast<ScheduleSheetInstance>()
                    .Where(i => !i.IsTitleblockRevisionSchedule)
                    .ToLookup(i => i.OwnerViewId.GetValue());
            }

            var viewNames = new Dictionary<long, string>();
            string ViewName(ElementId id)
            {
                var key = id.GetValue();
                if (!viewNames.TryGetValue(key, out var name))
                    viewNames[key] = name = DocumentationUtils.ViewName(doc, id);
                return name;
            }

            var sheets = page.Select(sheet =>
            {
                var sheetId = sheet.Id.GetValue();
                return new
                {
                    id = sheetId,
                    number = sheet.SheetNumber,
                    name = sheet.Name,
                    isPlaceholder = sheet.IsPlaceholder,
                    titleBlocks = includeContents
                        ? titleBlocksBySheet[sheetId]
                            .Select(tb => new
                            {
                                id = tb.Id.GetValue(),
                                typeId = tb.Symbol.Id.GetValue(),
                                familyName = tb.Symbol.FamilyName,
                                typeName = tb.Symbol.Name
                            })
                            .ToList()
                        : null,
                    viewports = includeContents
                        ? sheet.GetAllViewports()
                            .Select(id => doc.GetElement(id) as Viewport)
                            .Where(vp => vp != null)
                            .Select(vp => new
                            {
                                viewportId = vp.Id.GetValue(),
                                viewId = vp.ViewId.GetValue(),
                                viewName = ViewName(vp.ViewId),
                                center = DocumentationUtils.PointToMm(vp.GetBoxCenter())
                            })
                            .ToList()
                        : null,
                    schedules = includeContents
                        ? schedulesBySheet[sheetId]
                            .Select(i => new
                            {
                                instanceId = i.Id.GetValue(),
                                scheduleId = i.ScheduleId.GetValue(),
                                scheduleName = ViewName(i.ScheduleId),
                                origin = DocumentationUtils.PointToMm(i.Point)
                            })
                            .ToList()
                        : null
                };
            }).ToList();

            var titleBlockTypes = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType()
                .OfType<FamilySymbol>()
                .OrderBy(t => t.FamilyName)
                .ThenBy(t => t.Name)
                .Select(t => new { id = t.Id.GetValue(), familyName = t.FamilyName, typeName = t.Name })
                .ToList();

            var shownTo = offset + sheets.Count;
            return Ok(sheets.Count == matching.Count
                    ? $"Found {matching.Count} sheets."
                    : $"Found {matching.Count} sheets; returned {sheets.Count} (offset {offset}).",
                new
                {
                    total = matching.Count,
                    offset,
                    returned = sheets.Count,
                    truncated = shownTo < matching.Count,
                    nextOffset = shownTo < matching.Count ? (int?)shownTo : null,
                    sheets,
                    titleBlockTypes
                });
        }
    }
}
