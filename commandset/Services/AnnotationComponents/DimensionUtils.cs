using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Dimension helpers shared by create_dimensions and modify_annotations:
    ///     type resolution by id/name, text (override/prefix/suffix/above/below)
    ///     on the dimension or its segments, and a millimetre summary.
    /// </summary>
    internal static class DimensionUtils
    {
        private static readonly string[] TextFields = { "override", "prefix", "suffix", "above", "below" };

        /// <summary>
        ///     Resolves a linear dimension type. A positive id wins over the name.
        ///     Names match exactly (case-insensitive) first, then by a unique
        ///     partial match; ambiguous or unknown names throw with candidates.
        ///     Returns null when neither is given (keep Revit's default type).
        /// </summary>
        public static DimensionType ResolveType(Document doc, long? typeId, string name)
        {
            if (typeId != null)
                return DocumentationUtils.GetElement<DimensionType>(doc, typeId)
                       ?? throw new ArgumentException($"dimensionStyleId {typeId} is not a dimension type.");

            if (string.IsNullOrWhiteSpace(name))
                return null;

            var wanted = name.Trim();
            var types = new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                .Where(t => t.StyleType == DimensionStyleType.Linear)
                .OrderBy(t => t.Id.GetValue())
                .ToList();

            var exact = types.FirstOrDefault(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;

            // Legacy default of the tool: "Linear" is the style, not a type name.
            if (string.Equals(wanted, "Linear", StringComparison.OrdinalIgnoreCase))
                return null;

            var partial = types.Where(t => t.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (partial.Count == 1)
                return partial[0];
            if (partial.Count > 1)
                throw new ArgumentException(
                    $"Dimension type '{wanted}' is ambiguous; pass dimensionStyleId or an exact name. Candidates: " +
                    string.Join("; ", partial.Select(t => $"'{t.Name}' (id {t.Id.GetValue()})")) + ".");

            throw new ArgumentException($"Linear dimension type '{wanted}' not found. Available: " +
                                        string.Join("; ", types.Select(t => $"'{t.Name}' (id {t.Id.GetValue()})").Take(40)) + ".");
        }

        /// <summary>True when the token carries a 'text' object or a non-empty 'segments' array.</summary>
        public static bool HasText(JToken item) =>
            item?["text"] is JObject || (item?["segments"] is JArray segments && segments.Count > 0);

        /// <summary>
        ///     Applies item["text"] (to the dimension, or to every segment of a
        ///     multi-segment dimension) and item["segments"] (by 0-based index).
        ///     Each property is set independently; failures are returned as
        ///     warnings instead of aborting. Returns the number of values applied.
        /// </summary>
        public static int ApplyText(Dimension dimension, JToken item, List<string> warnings)
        {
            var applied = 0;
            var segmentCount = dimension.NumberOfSegments;

            if (item?["text"] is JObject text)
            {
                if (segmentCount > 1)
                {
                    for (var i = 0; i < segmentCount; i++)
                        applied += ApplyFields(new SegmentText(dimension.Segments.get_Item(i)), text, $"segment {i}", warnings);
                }
                else
                {
                    applied += ApplyFields(new DimensionText(dimension), text, "dimension", warnings);
                }
            }

            if (item?["segments"] is JArray segments)
            {
                foreach (var token in segments.OfType<JObject>())
                {
                    var index = token.Value<int?>("index");
                    if (index == null || index < 0 || index >= Math.Max(segmentCount, 1))
                    {
                        warnings.Add($"segment {index?.ToString() ?? "?"}: index out of range (dimension has {Math.Max(segmentCount, 1)} segment(s)).");
                        continue;
                    }

                    ITextTarget target = segmentCount > 1
                        ? new SegmentText(dimension.Segments.get_Item(index.Value))
                        : new DimensionText(dimension);
                    applied += ApplyFields(target, token, $"segment {index}", warnings);
                }
            }

            return applied;
        }

        /// <summary>Id, type, segment count, values (mm) and referenced element ids of a dimension.</summary>
        public static JObject Describe(Document doc, Dimension dimension)
        {
            var count = dimension.NumberOfSegments;
            var values = count > 1
                ? dimension.Segments.Cast<DimensionSegment>().Select(s => s.Value).ToList()
                : new List<double?> { dimension.Value };

            return new JObject
            {
                ["id"] = dimension.Id.GetValue(),
                ["typeId"] = dimension.GetTypeId().GetValue(),
                ["typeName"] = doc.GetElement(dimension.GetTypeId())?.Name,
                ["segmentCount"] = Math.Max(count, 1),
                ["valuesMm"] = new JArray(values.Select(v => v.HasValue ? (object)DocumentationUtils.FeetToMm(v.Value) : null)),
                ["referencedElementIds"] = new JArray(dimension.References.Cast<Reference>()
                    .Select(r => r.ElementId.GetValue()).Distinct())
            };
        }

        /// <summary>Throws when a dimension has fewer than 2 references or measures 0.</summary>
        public static void RequireMeasurable(Dimension dimension)
        {
            if (dimension.References.Size < 2)
                throw new InvalidOperationException("Dimension has fewer than 2 references.");

            var values = dimension.NumberOfSegments > 1
                ? dimension.Segments.Cast<DimensionSegment>().Select(s => s.Value)
                : new[] { dimension.Value };
            if (values.Any(v => v.HasValue && Math.Abs(v.Value) < 1e-9))
                throw new InvalidOperationException(
                    "Dimension measures 0: references coincide or are not spaced along the dimension direction.");
        }

        private static int ApplyFields(ITextTarget target, JObject fields, string label, List<string> warnings)
        {
            var applied = 0;
            foreach (var field in TextFields)
            {
                var token = fields[field];
                if (token == null || token.Type == JTokenType.Null)
                    continue;
                try
                {
                    target.Set(field, token.ToString());
                    applied++;
                }
                catch (Exception ex)
                {
                    warnings.Add($"{label} {field}: {ex.Message}");
                }
            }

            return applied;
        }

        private interface ITextTarget
        {
            void Set(string field, string value);
        }

        private sealed class DimensionText : ITextTarget
        {
            private readonly Dimension _dimension;
            public DimensionText(Dimension dimension) => _dimension = dimension;

            public void Set(string field, string value)
            {
                switch (field)
                {
                    case "override": _dimension.ValueOverride = value; break;
                    case "prefix": _dimension.Prefix = value; break;
                    case "suffix": _dimension.Suffix = value; break;
                    case "above": _dimension.Above = value; break;
                    case "below": _dimension.Below = value; break;
                }
            }
        }

        private sealed class SegmentText : ITextTarget
        {
            private readonly DimensionSegment _segment;
            public SegmentText(DimensionSegment segment) => _segment = segment;

            public void Set(string field, string value)
            {
                switch (field)
                {
                    case "override": _segment.ValueOverride = value; break;
                    case "prefix": _segment.Prefix = value; break;
                    case "suffix": _segment.Suffix = value; break;
                    case "above": _segment.Above = value; break;
                    case "below": _segment.Below = value; break;
                }
            }
        }
    }
}
