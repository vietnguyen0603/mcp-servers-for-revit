using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Framing
{
    /// <summary>
    ///     Selects placed structural framing and columns by ids, category, comments,
    ///     mark, family/type name and level. Every given criterion must match;
    ///     text matches are exact and case-insensitive.
    /// </summary>
    internal static class StructuralMemberSelector
    {
        public static List<FamilyInstance> Select(Document doc, JObject filter)
        {
            filter ??= new JObject();
            var ids = ReadSet(filter["elementIds"]);
            var comments = ReadSet(filter["comments"]);
            var marks = ReadSet(filter["marks"]);
            var familyName = filter.Value<string>("familyName");
            var typeName = filter.Value<string>("typeName");
            var level = filter.Value<string>("level");
            var categories = Categories(filter.Value<string>("category"));

            return new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(f => f.Category != null && categories.Contains((BuiltInCategory)f.Category.Id.GetValue()))
                .Where(f => ids == null || ids.Contains(f.Id.GetValue().ToString()))
                .Where(f => comments == null || comments.Contains(f.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? ""))
                .Where(f => marks == null || marks.Contains(f.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? ""))
                .Where(f => familyName == null || string.Equals(f.Symbol.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
                .Where(f => typeName == null || string.Equals(f.Symbol.Name, typeName, StringComparison.OrdinalIgnoreCase))
                .Where(f => level == null || string.Equals(LevelOf(doc, f)?.Name, level, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Id.GetValue())
                .ToList();
        }

        public static bool IsColumn(FamilyInstance instance) =>
            instance.Category.Id.GetValue() == (long)BuiltInCategory.OST_StructuralColumns;

        /// <summary>Reference level for framing, base level for columns.</summary>
        public static Level LevelOf(Document doc, FamilyInstance instance)
        {
            var parameter = instance.get_Parameter(IsColumn(instance) ? BuiltInParameter.FAMILY_BASE_LEVEL_PARAM : BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
            return doc.GetElement(parameter?.AsElementId() ?? instance.LevelId) as Level ?? doc.GetElement(instance.LevelId) as Level;
        }

        private static HashSet<BuiltInCategory> Categories(string category)
        {
            switch ((category ?? "all").Trim().ToLowerInvariant())
            {
                case "framing": return new HashSet<BuiltInCategory> { BuiltInCategory.OST_StructuralFraming };
                case "columns": return new HashSet<BuiltInCategory> { BuiltInCategory.OST_StructuralColumns };
                case "all": return new HashSet<BuiltInCategory> { BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralColumns };
                default: throw new ArgumentException($"Invalid category '{category}' (framing, columns, all).");
            }
        }

        private static HashSet<string> ReadSet(JToken token)
        {
            if (token is not JArray array || array.Count == 0)
                return null;
            return new HashSet<string>(array.Select(t => t.ToString()), StringComparer.OrdinalIgnoreCase);
        }
    }
}
