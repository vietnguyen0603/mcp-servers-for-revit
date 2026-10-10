using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Framing
{
    /// <summary>
    ///     Read-only check of placed framing and columns: groups the filtered members
    ///     by comments, mark, type, usage, level and/or category and reports count,
    ///     bottom/top elevation ranges (bounding box, mm), total framing length and ids.
    /// </summary>
    public class SummarizeStructuralMembersEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Summarize Structural Members";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var members = StructuralMemberSelector.Select(doc, parameters["filter"] as JObject);
            var groupBy = (parameters["groupBy"] as JArray)?.Select(t => t.ToString()).ToList() ?? new List<string> { "comments", "type" };
            var maxIds = parameters.Value<int?>("maxIds") ?? 50;

            var groups = members
                .GroupBy(m => string.Join(" | ", groupBy.Select(key => KeyOf(doc, m, key))))
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => Describe(g.Key, g.ToList(), maxIds))
                .ToList();

            return Ok($"{members.Count} structural members in {groups.Count} groups.", new JObject
            {
                ["total"] = members.Count,
                ["groupBy"] = new JArray(groupBy),
                ["groups"] = new JArray(groups)
            });
        }

        private static JObject Describe(string key, List<FamilyInstance> members, int maxIds)
        {
            var boxes = members.Select(m => m.get_BoundingBox(null)).Where(b => b != null).ToList();
            var length = members.Where(m => !StructuralMemberSelector.IsColumn(m))
                .Sum(m => m.get_Parameter(BuiltInParameter.INSTANCE_LENGTH_PARAM)?.AsDouble() ?? 0);
            var group = new JObject
            {
                ["key"] = key,
                ["count"] = members.Count,
                ["ids"] = new JArray(members.Take(maxIds).Select(m => m.Id.GetValue()))
            };
            if (boxes.Count > 0)
            {
                group["bottom"] = Range(boxes.Select(b => b.Min.Z));
                group["top"] = Range(boxes.Select(b => b.Max.Z));
            }

            if (length > 0)
                group["totalLength"] = DocumentationUtils.FeetToMm(length);
            return group;
        }

        private static JObject Range(IEnumerable<double> values)
        {
            var list = values.ToList();
            return new JObject { ["min"] = DocumentationUtils.FeetToMm(list.Min()), ["max"] = DocumentationUtils.FeetToMm(list.Max()) };
        }

        private static string KeyOf(Document doc, FamilyInstance member, string key)
        {
            switch (key.Trim().ToLowerInvariant())
            {
                case "comments": return member.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
                case "mark": return member.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
                case "type": return member.Symbol.FamilyName + ": " + member.Symbol.Name;
                case "usage": return StructuralMemberSelector.IsColumn(member) ? "column" : member.StructuralUsage.ToString();
                case "level": return StructuralMemberSelector.LevelOf(doc, member)?.Name ?? "";
                case "category": return member.Category.Name;
                default: throw new ArgumentException($"Invalid groupBy key '{key}' (comments, mark, type, usage, level, category).");
            }
        }
    }
}
