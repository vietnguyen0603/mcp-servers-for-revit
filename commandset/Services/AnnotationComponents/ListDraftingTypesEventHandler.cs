using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Lists the drafting types of the document grouped by kind (text note,
    ///     dimension, filled region, line style, viewport and title block types;
    ///     detail group types as a count), optionally filtered by kind and name.
    ///     Read-only; no transaction.
    /// </summary>
    public class ListDraftingTypesEventHandler : JsonParameterEventHandler
    {
        private static readonly string[] AllKinds =
        {
            "textNoteTypes", "dimensionTypes", "filledRegionTypes", "lineStyles", "viewportTypes", "titleBlockTypes",
            "detailGroupTypes"
        };

        public override string GetName() => "List Drafting Types";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var nameContains = parameters.Value<string>("nameContains")?.Trim();
            var requested = parameters["kinds"] is JArray kinds && kinds.Count > 0
                ? kinds.Select(k => k.ToString()).ToList()
                : AllKinds.ToList();
            var unknown = requested.Where(k => !AllKinds.Contains(k)).ToList();
            if (unknown.Count > 0)
                return Fail($"Unknown kinds: {string.Join(", ", unknown)}. Expected: {string.Join(", ", AllKinds)}.");

            bool Matches(params string[] names) => string.IsNullOrEmpty(nameContains)
                                                   || names.Any(n => n != null &&
                                                                     n.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0);

            var result = new JObject();
            var counts = new JObject();
            foreach (var kind in AllKinds.Where(requested.Contains))
            {
                JArray list;
                switch (kind)
                {
                    case "textNoteTypes":
                        list = TextNoteTypes(doc, Matches);
                        break;
                    case "dimensionTypes":
                        list = new JArray(new FilteredElementCollector(doc).OfClass(typeof(DimensionType))
                            .Cast<DimensionType>()
                            .Where(t => !string.IsNullOrEmpty(t.Name) && Matches(t.Name))
                            .OrderBy(t => t.StyleType.ToString()).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                            .Select(t => new JObject
                            {
                                ["id"] = t.Id.GetValue(),
                                ["name"] = t.Name,
                                ["style"] = t.StyleType.ToString()
                            }));
                        break;
                    case "filledRegionTypes":
                        list = new JArray(new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType))
                            .Cast<FilledRegionType>()
                            .Where(t => Matches(t.Name))
                            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                            .Select(t => new JObject
                            {
                                ["id"] = t.Id.GetValue(),
                                ["name"] = t.Name,
                                ["isMasking"] = t.IsMasking
                            }));
                        break;
                    case "lineStyles":
                        list = LineStyles(doc, Matches);
                        break;
                    case "viewportTypes":
                        list = new JArray(ViewportTypes(doc)
                            .Where(t => Matches(t.Name))
                            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                            .Select(t => new JObject { ["id"] = t.Id.GetValue(), ["name"] = t.Name }));
                        break;
                    case "titleBlockTypes":
                        list = new JArray(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                            .WhereElementIsElementType()
                            .OfType<FamilySymbol>()
                            .Where(t => Matches(t.FamilyName, t.Name))
                            .OrderBy(t => t.FamilyName, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                            .Select(t => new JObject
                            {
                                ["id"] = t.Id.GetValue(),
                                ["family"] = t.FamilyName,
                                ["type"] = t.Name
                            }));
                        break;
                    default: // detailGroupTypes: count only
                        counts[kind] = DetailGroupUtils.DetailGroupTypes(doc).Count(t => Matches(t.Name));
                        continue;
                }

                result[kind] = list;
                counts[kind] = list.Count;
            }

            result.AddFirst(new JProperty("counts", counts));
            return Ok(string.Join(", ", counts.Properties().Select(p => $"{p.Value} {p.Name}")) + ".", result);
        }

        private static JArray TextNoteTypes(Document doc, Func<string[], bool> matches)
        {
            return new JArray(new FilteredElementCollector(doc).OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .Where(t => matches(new[] { t.Name }))
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(t =>
                {
                    var size = t.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0;
                    var arrowId = t.get_Parameter(BuiltInParameter.LEADER_ARROWHEAD)?.AsElementId();
                    var arrow = arrowId == null || arrowId == ElementId.InvalidElementId
                        ? "None"
                        : doc.GetElement(arrowId)?.Name;
                    return new JObject
                    {
                        ["id"] = t.Id.GetValue(),
                        ["name"] = t.Name,
                        ["sizeMm"] = Math.Round(size * DocumentationUtils.MmPerFoot, 2),
                        ["sizeIn"] = Math.Round(size * 12, 4),
                        ["font"] = t.get_Parameter(BuiltInParameter.TEXT_FONT)?.AsString(),
                        ["leaderArrowhead"] = arrow
                    };
                }));
        }

        private static JArray LineStyles(Document doc, Func<string[], bool> matches)
        {
            var lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            if (lines == null)
                return new JArray();

            var solid = LinePatternElement.GetSolidPatternId();
            return new JArray(lines.SubCategories.Cast<Category>()
                .Where(c => matches(new[] { c.Name }))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c =>
                {
                    var style = c.GetGraphicsStyle(GraphicsStyleType.Projection);
                    var patternId = c.GetLinePatternId(GraphicsStyleType.Projection);
                    var pattern = patternId == null || patternId == ElementId.InvalidElementId || patternId == solid
                        ? "Solid"
                        : doc.GetElement(patternId)?.Name ?? "Solid";
                    return new JObject
                    {
                        ["id"] = style?.Id.GetValue(),
                        ["name"] = c.Name,
                        ["weight"] = c.GetLineWeight(GraphicsStyleType.Projection),
                        ["pattern"] = pattern
                    };
                }));
        }

        /// <summary>Viewport types: the Viewports category's types plus the valid types of a placed viewport.</summary>
        private static IEnumerable<Element> ViewportTypes(Document doc)
        {
            var types = new Dictionary<long, Element>();
            foreach (var type in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Viewports)
                         .WhereElementIsElementType())
                types[type.Id.GetValue()] = type;

            var sample = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).FirstElement() as Viewport;
            if (sample != null)
            {
                foreach (var id in sample.GetValidTypes())
                {
                    var type = doc.GetElement(id);
                    if (type != null)
                        types[id.GetValue()] = type;
                }
            }

            return types.Values;
        }
    }
}
