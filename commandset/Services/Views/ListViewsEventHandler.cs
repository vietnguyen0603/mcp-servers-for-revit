using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Lists views (optionally templates and view family types) with the
    ///     sheets they are placed on. Read-only: no transaction is opened.
    /// </summary>
    public class ListViewsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "List Views";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var viewTypes = parameters["viewTypes"]?.ToObject<List<string>>()
                .Select(t => DocumentationUtils.ParseEnum(t, ViewType.Undefined))
                .ToHashSet();
            var includeTemplates = parameters.Value<bool?>("includeTemplates") ?? false;
            var includeViewFamilyTypes = parameters.Value<bool?>("includeViewFamilyTypes") ?? false;
            var nameContains = parameters.Value<string>("nameContains");
            var limit = Math.Max(1, Math.Min(parameters.Value<int?>("limit") ?? 500, 5000));

            var placements = CollectPlacements(doc);

            var matching = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.ViewType != ViewType.Internal && v.ViewType != ViewType.ProjectBrowser
                            && v.ViewType != ViewType.SystemBrowser && v.ViewType != ViewType.Undefined)
                .Where(v => includeTemplates || !v.IsTemplate)
                .Where(v => viewTypes == null || viewTypes.Count == 0 || viewTypes.Contains(v.ViewType))
                .Where(v => string.IsNullOrEmpty(nameContains)
                            || v.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(v => v.ViewType.ToString())
                .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var views = matching.Take(limit).Select(v => new
            {
                id = v.Id.GetValue(),
                name = v.Name,
                viewType = v.ViewType.ToString(),
                isTemplate = v.IsTemplate,
                scale = v.ViewType == ViewType.DrawingSheet || v.ViewType == ViewType.Schedule ? (int?)null : v.Scale,
                level = (v as ViewPlan)?.GenLevel?.Name,
                viewTemplateId = v.ViewTemplateId == ElementId.InvalidElementId ? (long?)null : v.ViewTemplateId.GetValue(),
                sheets = placements.TryGetValue(v.Id.GetValue(), out var sheets) ? sheets : null
            }).ToList();

            object viewFamilyTypes = null;
            if (includeViewFamilyTypes)
            {
                viewFamilyTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .OrderBy(t => t.ViewFamily.ToString())
                    .ThenBy(t => t.Name)
                    .Select(t => new { id = t.Id.GetValue(), name = t.Name, viewFamily = t.ViewFamily.ToString() })
                    .ToList();
            }

            return Ok($"Found {matching.Count} views.", new
            {
                total = matching.Count,
                truncated = matching.Count > views.Count,
                views,
                viewFamilyTypes
            });
        }

        /// <summary>Maps view id to the sheet numbers it is placed on (viewports and schedule instances).</summary>
        internal static Dictionary<long, List<string>> CollectPlacements(Document doc)
        {
            var placements = new Dictionary<long, List<string>>();

            void Add(ElementId viewId, ElementId sheetId)
            {
                if (!(doc.GetElement(sheetId) is ViewSheet sheet))
                    return;
                var key = viewId.GetValue();
                if (!placements.TryGetValue(key, out var list))
                    placements[key] = list = new List<string>();
                if (!list.Contains(sheet.SheetNumber))
                    list.Add(sheet.SheetNumber);
            }

            foreach (var viewport in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
                Add(viewport.ViewId, viewport.SheetId);

            foreach (var instance in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance))
                         .Cast<ScheduleSheetInstance>())
                Add(instance.ScheduleId, instance.OwnerViewId);

            return placements;
        }
    }
}
