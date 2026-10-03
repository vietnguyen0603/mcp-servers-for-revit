using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Reads the displayed cell text of a schedule body, or lists the
    ///     project's schedules when no schedule is specified. Read-only.
    /// </summary>
    public class GetScheduleDataEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Get Schedule Data";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule)
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var scheduleId = DocumentationUtils.ReadId(parameters, "scheduleId");
            var scheduleName = parameters.Value<string>("scheduleName");

            if (scheduleId == null && string.IsNullOrWhiteSpace(scheduleName))
            {
                return Ok($"Found {schedules.Count} schedules.", new
                {
                    schedules = schedules.Select(s => new
                    {
                        id = s.Id.GetValue(),
                        name = s.Name,
                        category = s.Definition.CategoryId == ElementId.InvalidElementId
                            ? "Multi-Category"
                            : Category.GetCategory(doc, s.Definition.CategoryId)?.Name
                    }).ToList()
                });
            }

            var schedule = scheduleId != null
                ? schedules.FirstOrDefault(s => s.Id.GetValue() == scheduleId)
                : schedules.FirstOrDefault(s => string.Equals(s.Name, scheduleName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (schedule == null)
                return Fail($"Schedule '{(object)scheduleId ?? scheduleName}' not found.");

            var maxRows = Math.Max(1, Math.Min(parameters.Value<int?>("maxRows") ?? 500, 10000));
            var body = schedule.GetTableData().GetSectionData(SectionType.Body);
            var rowCount = body.NumberOfRows;
            var columnCount = body.NumberOfColumns;

            var rows = new List<List<string>>();
            for (var r = body.FirstRowNumber; r < body.FirstRowNumber + rowCount && rows.Count < maxRows; r++)
            {
                var row = new List<string>(columnCount);
                for (var c = body.FirstColumnNumber; c < body.FirstColumnNumber + columnCount; c++)
                    row.Add(schedule.GetCellText(SectionType.Body, r, c));
                rows.Add(row);
            }

            var definition = schedule.Definition;
            var columns = definition.GetFieldOrder()
                .Select(id => definition.GetField(id))
                .Where(f => !f.IsHidden)
                .Select(f => f.ColumnHeading)
                .ToList();

            return Ok($"Read {rows.Count} of {rowCount} rows from '{schedule.Name}'.", new
            {
                scheduleId = schedule.Id.GetValue(),
                name = schedule.Name,
                columns,
                totalRows = rowCount,
                truncated = rowCount > rows.Count,
                rows
            });
        }
    }
}
