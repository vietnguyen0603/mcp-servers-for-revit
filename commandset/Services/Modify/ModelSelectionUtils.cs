using System.Globalization;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Modify
{
    /// <summary>
    ///     Element selection and level helpers shared by copy_to_levels,
    ///     delete_elements and set_workset. Every given filter is ANDed.
    /// </summary>
    internal static class ModelSelectionUtils
    {
        internal const double FeetPerMm = 1.0 / 304.8;
        internal const double LevelTolerance = 1.0 * FeetPerMm;

        /// <summary>Levels sorted by elevation (then name for a stable order).</summary>
        internal static List<Level> SortedLevels(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).ThenBy(l => l.Name, StringComparer.Ordinal).ToList();
        }

        /// <summary>Level from a name, element id or elevation in mm (within 1 mm); throws when none matches.</summary>
        internal static Level ResolveLevel(List<Level> levels, JToken token, string field)
        {
            if (token == null || token.Type == JTokenType.Null)
                throw new ArgumentException($"'{field}' is required.");

            if (token.Type == JTokenType.String)
            {
                var name = token.Value<string>().Trim();
                var byName = levels.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal))
                             ?? levels.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
                if (byName != null) return byName;
                if (double.TryParse(name, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
                    return ResolveLevel(levels, new JValue(numeric), field);
                throw new ArgumentException($"{field}: level '{name}' not found. Levels: {string.Join(", ", levels.Select(l => l.Name))}.");
            }

            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                throw new ArgumentException($"'{field}' must be a level name, level id or elevation in mm.");

            var value = token.Value<double>();
            if (token.Type == JTokenType.Integer && value > 0)
            {
                var byId = levels.FirstOrDefault(l => l.Id.GetValue() == (long)value);
                if (byId != null) return byId;
            }

            var feet = value * FeetPerMm;
            return levels.FirstOrDefault(l => Math.Abs(l.Elevation - feet) < LevelTolerance)
                   ?? throw new ArgumentException($"{field}: no level with id or elevation {value} (mm).");
        }

        /// <summary>
        ///     The level an element is associated with: reference level for framing,
        ///     base level for columns and walls, level for floors and level-based families.
        /// </summary>
        internal static ElementId GetLevelId(Element element)
        {
            foreach (var bip in new[]
                     {
                         BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,
                         BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,
                         BuiltInParameter.WALL_BASE_CONSTRAINT,
                         BuiltInParameter.LEVEL_PARAM,
                         BuiltInParameter.FAMILY_LEVEL_PARAM
                     })
            {
                var id = LevelParamValue(element, bip);
                if (id != null) return id;
            }

            if (element.LevelId != null && element.LevelId != ElementId.InvalidElementId)
                return element.LevelId;

            return LevelParamValue(element, BuiltInParameter.SCHEDULE_LEVEL_PARAM) ?? ElementId.InvalidElementId;
        }

        private static ElementId LevelParamValue(Element element, BuiltInParameter bip)
        {
            var parameter = element.get_Parameter(bip);
            if (parameter == null || parameter.StorageType != StorageType.ElementId) return null;
            var id = parameter.AsElementId();
            return id != null && id != ElementId.InvalidElementId ? id : null;
        }

        internal static string Comments(Element element) =>
            element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? string.Empty;

        internal static string Mark(Element element) =>
            element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? string.Empty;

        internal static string TypeName(Document doc, Element element)
        {
            var typeId = element.GetTypeId();
            return typeId == null || typeId == ElementId.InvalidElementId ? string.Empty : doc.GetElement(typeId)?.Name ?? string.Empty;
        }

        internal static string CategoryKey(Element element)
        {
            var category = element.Category;
            if (category == null) return "(none)";
            var value = category.Id.GetValue();
            // Cast to the enum itself: its underlying type is Int32 before Revit 2024 and Int64 after,
            // and Enum.IsDefined throws when given a boxed integer of the other width.
            var builtIn = (BuiltInCategory)value;
            if (value < 0 && Enum.IsDefined(typeof(BuiltInCategory), builtIn))
            {
                var name = builtIn.ToString();
                return name.StartsWith("OST_", StringComparison.Ordinal) ? name.Substring(4) : name;
            }
            return category.Name;
        }

        /// <summary>
        ///     Collects the elements matching categories / elementIds / comments / mark / type name
        ///     and (optionally) levels. Element types, view-specific elements and elements without a
        ///     category are never returned.
        /// </summary>
        internal static List<Element> Collect(Document doc, JObject parameters, List<Level> levels, List<string> warnings,
            bool useLevels = true)
        {
            var categoryIds = ReadCategories(doc, parameters, warnings);
            var idTokens = parameters["elementIds"] as JArray;
            if ((categoryIds == null || categoryIds.Count == 0) && (idTokens == null || idTokens.Count == 0))
                throw new ArgumentException("Give 'categories' and/or 'elementIds'.");

            IEnumerable<Element> elements;
            if (idTokens != null && idTokens.Count > 0)
            {
                var list = new List<Element>();
                var missing = 0;
                foreach (var token in idTokens)
                {
                    var element = doc.GetElement(token.Value<long>().ToRevitElementId());
                    if (element == null) missing++;
                    else list.Add(element);
                }
                if (missing > 0) warnings.Add($"{missing} element id(s) not found.");
                elements = list;
                if (categoryIds != null && categoryIds.Count > 0)
                {
                    var set = new HashSet<long>(categoryIds.Select(c => c.GetValue()));
                    elements = elements.Where(e => e.Category != null && set.Contains(e.Category.Id.GetValue()));
                }
            }
            else
            {
                elements = new FilteredElementCollector(doc)
                    .WherePasses(new ElementMulticategoryFilter(categoryIds))
                    .WhereElementIsNotElementType();
            }

            elements = elements.Where(e => !(e is ElementType) && e.Category != null && !e.ViewSpecific);

            var commentsEquals = parameters.Value<string>("commentsEquals");
            if (commentsEquals != null)
                elements = elements.Where(e => string.Equals(Comments(e), commentsEquals, StringComparison.Ordinal));

            var commentsStartsWith = parameters.Value<string>("commentsStartsWith");
            if (!string.IsNullOrEmpty(commentsStartsWith))
                elements = elements.Where(e => Comments(e).StartsWith(commentsStartsWith, StringComparison.Ordinal));

            var markStartsWith = parameters.Value<string>("markStartsWith");
            if (!string.IsNullOrEmpty(markStartsWith))
                elements = elements.Where(e => Mark(e).StartsWith(markStartsWith, StringComparison.Ordinal));

            var typeNameEquals = parameters.Value<string>("typeNameEquals");
            if (!string.IsNullOrEmpty(typeNameEquals))
                elements = elements.Where(e => string.Equals(TypeName(doc, e), typeNameEquals.Trim(), StringComparison.OrdinalIgnoreCase));

            if (useLevels && parameters["levels"] is JArray levelTokens && levelTokens.Count > 0)
            {
                var levelIds = new HashSet<long>();
                for (var i = 0; i < levelTokens.Count; i++)
                    levelIds.Add(ResolveLevel(levels, levelTokens[i], $"levels[{i}]").Id.GetValue());
                elements = elements.Where(e => levelIds.Contains(GetLevelId(e).GetValue()));
            }

            return elements.ToList();
        }

        private static List<ElementId> ReadCategories(Document doc, JObject parameters, List<string> warnings)
        {
            if (!(parameters["categories"] is JArray tokens) || tokens.Count == 0) return null;
            var ids = new List<ElementId>();
            var unknown = new List<string>();
            foreach (var token in tokens)
            {
                var name = token.Value<string>();
                var category = DocumentationUtils.ResolveCategory(doc, name);
                if (category == null) unknown.Add(name);
                else if (ids.All(id => id != category.Id)) ids.Add(category.Id);
            }
            if (unknown.Count > 0)
            {
                if (ids.Count == 0)
                    throw new ArgumentException($"Unknown categories: {string.Join(", ", unknown)}.");
                warnings.Add($"Unknown categories ignored: {string.Join(", ", unknown)}.");
            }
            return ids;
        }

        /// <summary>Counts per category key (deterministic order).</summary>
        internal static JObject CountBy(IEnumerable<Element> elements, Func<Element, string> key)
        {
            var result = new JObject();
            foreach (var group in elements.GroupBy(key).OrderBy(g => g.Key, StringComparer.Ordinal))
                result[group.Key] = group.Count();
            return result;
        }

        internal static string LevelName(Document doc, ElementId levelId)
        {
            if (levelId == null || levelId == ElementId.InvalidElementId) return "(no level)";
            return doc.GetElement(levelId)?.Name ?? "(no level)";
        }

        /// <summary>
        ///     Starts a transaction whose failures never block on a dialog: warnings are
        ///     discarded, errors with a default resolution are resolved (and reported in
        ///     <paramref name="messages" />), other errors roll the transaction back.
        /// </summary>
        internal static Transaction StartTransaction(Document doc, string name, List<string> messages)
        {
            var transaction = new Transaction(doc, name);
            var options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new QuietFailures(messages));
            options.SetClearAfterRollback(true);
            options.SetForcedModalHandling(false);
            transaction.SetFailureHandlingOptions(options);
            transaction.Start();
            return transaction;
        }

        private class QuietFailures : IFailuresPreprocessor
        {
            private readonly List<string> _messages;

            public QuietFailures(List<string> messages) => _messages = messages;

            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                var resolved = false;
                foreach (var failure in accessor.GetFailureMessages())
                {
                    var severity = failure.GetSeverity();
                    if (severity == FailureSeverity.Warning)
                    {
                        accessor.DeleteWarning(failure);
                        continue;
                    }

                    var text = failure.GetDescriptionText();
                    if (severity == FailureSeverity.Error && failure.HasResolutions())
                    {
                        accessor.ResolveFailure(failure);
                        resolved = true;
                        _messages?.Add($"Resolved Revit error: {text}");
                        continue;
                    }

                    _messages?.Add($"Revit error: {text}");
                    return FailureProcessingResult.ProceedWithRollBack;
                }

                return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
            }
        }
    }
}
