using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Library
{
    /// <summary>
    ///     Helpers shared by the read-only drafting-library commands
    ///     (get_drafting_standards, audit_detail_library, get_drafting_view_index):
    ///     view scoping, collection of the 2D elements owned by a set of views,
    ///     detail-family role classification and allowed-name matching.
    /// </summary>
    internal static class DraftingLibraryUtils
    {
        /// <summary>Default drafting-view naming convention: PFX-GRP-NN[A][-WR]_DESCRIPTION.</summary>
        public const string DefaultNamePattern = @"^[A-Z]{2,4}-[A-Z]{1,5}-\d+[A-Z]?(-[A-Z]+)?_";

        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

        /// <summary>Family-name patterns per role, first match wins.</summary>
        private static readonly (string Role, Regex Pattern)[] RolePatterns =
        {
            ("sectionCut", new Regex(@"section.*(cut|head)|detail\s*ref|callout\s*(bubble|head)", Options)),
            ("subtitle", new Regex(@"sub[\s_-]*(detail[\s_-]*)?title|detail[\s_-]*title", Options)),
            ("weld", new Regex(@"weld", Options)),
            ("elevationMarker", new Regex(@"elev(ation)?[\s_-]*mark|level[\s_-]*mark", Options)),
            ("breakLine", new Regex(@"break[\s_-]*line", Options)),
            ("rebar", new Regex(@"rebar|reinf|bar[\s_-]*section", Options)),
            ("fastener",
                new Regex(@"fastener|bolt|nail|screw|anchor|washer|\bnut\b|shear[\s_-]*stud|simpson|\bsms\b", Options)),
            ("framing", new Regex(@"stud|track|lumber|ply|joist|rafter|wood|\bcmu\b|masonry", Options)),
            ("steelShape", new Regex(@"aisc|angle|plate|\bhss\b|channel|tube|pipe|shape", Options)),
            ("gridHead", new Regex(@"grid[\s_-]*head", Options))
        };

        public static readonly string[] RoleNames = RolePatterns.Select(r => r.Role).Concat(new[] { "other" }).ToArray();

        /// <summary>Role of a detail-item or generic-annotation family by its name.</summary>
        public static string RoleOf(string familyName)
        {
            if (string.IsNullOrEmpty(familyName))
                return "other";
            foreach (var (role, pattern) in RolePatterns)
                if (pattern.IsMatch(familyName))
                    return role;
            return "other";
        }

        /// <summary>Non-template drafting views ordered by name, then id.</summary>
        public static List<View> DraftingViews(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(ViewDrafting))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Id.GetValue())
                .ToList();
        }

        /// <summary>
        ///     Views in scope: `viewIds` (any non-template, non-sheet view) or all
        ///     drafting views, then narrowed by `sheetNumbers` (views placed on
        ///     those sheets) and `viewNameContains`.
        /// </summary>
        public static List<View> ResolveViews(Document doc, JObject parameters, ViewportIndex index)
        {
            IEnumerable<View> views;
            var ids = AuditDetailReferencesEventHandler.ReadIdSet(parameters["viewIds"]);
            if (ids != null)
            {
                var missing = new List<long>();
                var list = new List<View>();
                foreach (var id in ids)
                {
                    if (doc.GetElement(id.ToRevitElementId()) is View v && !v.IsTemplate && !(v is ViewSheet))
                        list.Add(v);
                    else
                        missing.Add(id);
                }

                if (missing.Count > 0)
                    throw new ArgumentException(
                        $"Not a non-template view (sheets are not allowed): {string.Join(", ", missing.Take(20))}.");
                views = list.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Id.GetValue());
            }
            else
            {
                views = DraftingViews(doc);
            }

            if (parameters["sheetNumbers"] is JArray sheets && sheets.Count > 0)
            {
                var wanted = new HashSet<string>(sheets.Select(t => DetailReferenceUtils.Normalize(t.ToString())));
                views = views.Where(v =>
                {
                    var placed = index.FindView(v.Id);
                    return placed != null && wanted.Contains(DetailReferenceUtils.Normalize(placed.Sheet.SheetNumber));
                });
            }

            var nameContains = parameters.Value<string>("viewNameContains")?.Trim();
            if (!string.IsNullOrEmpty(nameContains))
                views = views.Where(v => v.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0);

            return views.ToList();
        }

        /// <summary>Elements of class <typeparamref name="T" /> owned by one of <paramref name="viewIds" />.</summary>
        public static List<T> Owned<T>(Document doc, HashSet<long> viewIds) where T : Element
        {
            return new FilteredElementCollector(doc).OfClass(typeof(T))
                .WhereElementIsNotElementType()
                .Cast<T>()
                .Where(e => viewIds.Contains(e.OwnerViewId.GetValue()))
                .ToList();
        }

        /// <summary>Elements of a category owned by one of <paramref name="viewIds" />.</summary>
        public static List<T> OwnedOfCategory<T>(Document doc, HashSet<long> viewIds, BuiltInCategory category)
            where T : Element
        {
            return new FilteredElementCollector(doc).OfCategory(category)
                .WhereElementIsNotElementType()
                .OfType<T>()
                .Where(e => viewIds.Contains(e.OwnerViewId.GetValue()))
                .ToList();
        }

        /// <summary>Top-level detail components (nested sub-components skipped).</summary>
        public static List<FamilyInstance> DetailComponents(Document doc, HashSet<long> viewIds)
        {
            return OwnedOfCategory<FamilyInstance>(doc, viewIds, BuiltInCategory.OST_DetailComponents)
                .Where(f => f.SuperComponent == null)
                .ToList();
        }

        /// <summary>Detail lines/arcs (Lines category) with their line style.</summary>
        public static List<CurveElement> DetailCurves(Document doc, HashSet<long> viewIds)
        {
            var lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            return Owned<CurveElement>(doc, viewIds)
                .Where(c => c.Category != null && lines != null && c.Category.Id == lines.Id && c.LineStyle != null)
                .ToList();
        }

        /// <summary>Linear/angular/radial dimensions (spot dimensions excluded).</summary>
        public static List<Dimension> Dimensions(Document doc, HashSet<long> viewIds)
        {
            return Owned<Dimension>(doc, viewIds)
                .Where(d => DocumentationUtils.IsCategory(d, BuiltInCategory.OST_Dimensions))
                .ToList();
        }

        /// <summary>Override text of every segment (single-segment dimensions: the dimension itself).</summary>
        public static IEnumerable<(int segment, string valueOverride, string value)> Overrides(Dimension d)
        {
            if (d.NumberOfSegments > 1)
            {
                var i = 0;
                foreach (DimensionSegment s in d.Segments)
                {
                    yield return (i, s.ValueOverride, s.ValueString);
                    i++;
                }
            }
            else
            {
                yield return (0, d.ValueOverride, d.ValueString);
            }
        }

        public static string LinePatternName(Document doc, ElementId patternId)
        {
            if (patternId == null || patternId == ElementId.InvalidElementId ||
                patternId == LinePatternElement.GetSolidPatternId())
                return "Solid";
            return doc.GetElement(patternId)?.Name ?? "Solid";
        }

        public static string ColorHex(Color color)
        {
            return color == null || !color.IsValid ? null : $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
        }

        /// <summary>A view name by id, cached.</summary>
        public static Func<ElementId, string> ViewNamer(Document doc)
        {
            var cache = new Dictionary<long, string>();
            return id =>
            {
                var key = id.GetValue();
                if (!cache.TryGetValue(key, out var name))
                    cache[key] = name = doc.GetElement(id)?.Name;
                return name;
            };
        }

        public static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            var flat = Regex.Replace(text, @"\s+", " ").Trim();
            return flat.Length <= max ? flat : flat.Substring(0, max - 1) + "…";
        }

        public static Regex CompileRegex(string pattern, string what)
        {
            try
            {
                return new Regex(pattern, Options, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Invalid {what} regex '{pattern}': {ex.Message}");
            }
        }
    }

    /// <summary>
    ///     Allowed-name list: each entry is an exact name (case-insensitive), a
    ///     glob with * and ?, or a regex written as /pattern/.
    /// </summary>
    internal sealed class NameMatcher
    {
        private readonly List<Regex> _patterns = new List<Regex>();
        private readonly HashSet<string> _exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static NameMatcher From(JToken token, string what)
        {
            if (!(token is JArray array) || array.Count == 0)
                return null;
            var matcher = new NameMatcher();
            foreach (var entry in array.Select(t => t.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                var value = entry.Trim();
                if (value.Length > 2 && value.StartsWith("/") && value.EndsWith("/"))
                    matcher._patterns.Add(DraftingLibraryUtils.CompileRegex(value.Substring(1, value.Length - 2), what));
                else if (value.IndexOf('*') >= 0 || value.IndexOf('?') >= 0)
                    matcher._patterns.Add(DraftingLibraryUtils.CompileRegex(
                        "^" + Regex.Escape(value).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", what));
                else
                    matcher._exact.Add(value);
            }

            return matcher;
        }

        public bool IsMatch(string name)
        {
            name = (name ?? string.Empty).Trim();
            return _exact.Contains(name) || _patterns.Any(p => p.IsMatch(name));
        }
    }
}
