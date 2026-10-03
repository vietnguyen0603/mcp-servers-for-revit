using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Lists detail group types with their instance and member counts and a sample
    ///     instance (id and view) that modify_detail_groups can copy from. Read-only.
    /// </summary>
    public class ListDetailGroupsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "List Detail Groups";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var nameContains = parameters.Value<string>("nameContains")?.Trim();
            var limit = Math.Max(1, Math.Min(parameters.Value<int?>("limit") ?? 500, 5000));
            var offset = Math.Max(0, parameters.Value<int?>("offset") ?? 0);

            var instancesByType = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_IOSDetailGroups)
                .WhereElementIsNotElementType()
                .OfType<Group>()
                .GroupBy(g => g.GroupType.Id.GetValue())
                .ToDictionary(g => g.Key, g => g.OrderBy(i => i.Id.GetValue()).ToList());

            var types = DetailGroupUtils.DetailGroupTypes(doc)
                .Where(t => string.IsNullOrEmpty(nameContains)
                            || t.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var page = types.Skip(offset).Take(limit).Select(t =>
            {
                instancesByType.TryGetValue(t.Id.GetValue(), out var instances);
                var sample = instances?.FirstOrDefault();
                return new
                {
                    groupTypeId = t.Id.GetValue(),
                    name = t.Name,
                    instanceCount = instances?.Count ?? 0,
                    memberCount = sample?.GetMemberIds().Count,
                    sampleInstanceId = sample?.Id.GetValue(),
                    sampleViewId = sample?.OwnerViewId.GetValue(),
                    sampleViewName = sample == null ? null : DocumentationUtils.ViewName(doc, sample.OwnerViewId)
                };
            }).ToList();

            return Ok($"{types.Count} detail group types; returned {page.Count} from offset {offset}.",
                new { total = types.Count, offset, limit, groupTypes = page });
        }
    }

    internal static class DetailGroupUtils
    {
        public static IEnumerable<GroupType> DetailGroupTypes(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(GroupType))
                .OfCategory(BuiltInCategory.OST_IOSDetailGroups)
                .Cast<GroupType>();
        }
    }
}
