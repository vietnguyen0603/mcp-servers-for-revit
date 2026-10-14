using System.Text.RegularExpressions;
using Group = Autodesk.Revit.DB.Group;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Library
{
    /// <summary>
    ///     Harvests the drafting standards actually used in the drafting views
    ///     (or the views in scope), ranked by use: scales, line styles, text,
    ///     dimension and filled region types, detail-component and generic
    ///     annotation families (with a role such as breakLine / rebar /
    ///     fastener / subtitle / weld / sectionCut), detail groups, viewport
    ///     types, title blocks and view naming. Read-only; no transaction.
    /// </summary>
    public class GetDraftingStandardsEventHandler : JsonParameterEventHandler
    {
        internal static readonly string[] AllSections =
        {
            "scales", "lineStyles", "textTypes", "dimensionTypes", "filledRegionTypes", "detailComponents",
            "annotationSymbols", "roles", "detailGroups", "viewportTypes", "titleBlocks", "viewNames"
        };

        private static readonly HashSet<string> SkippedSymbolParameters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Comments", "Mark", "Image" };

        public override string GetName() => "Get Drafting Standards";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var requested = parameters["sections"] is JArray s && s.Count > 0
                ? s.Select(t => t.ToString()).ToList()
                : AllSections.ToList();
            var unknown = requested.Where(r => !AllSections.Contains(r)).ToList();
            if (unknown.Count > 0)
                return Fail($"Unknown sections: {string.Join(", ", unknown)}. Expected: {string.Join(", ", AllSections)}.");

            var top = Math.Max(1, Math.Min(parameters.Value<int?>("top") ?? 30, 500));
            var includeUnused = parameters.Value<bool?>("includeUnused") ?? false;
            var sampleCount = Math.Max(0, Math.Min(parameters.Value<int?>("viewNameSamples") ?? 16, 200));
            var namePattern = DraftingLibraryUtils.CompileRegex(
                parameters.Value<string>("namePattern") ?? DraftingLibraryUtils.DefaultNamePattern, "namePattern");

            var index = ViewportIndex.Build(doc);
            var views = DraftingLibraryUtils.ResolveViews(doc, parameters, index);
            var viewIds = new HashSet<long>(views.Select(v => v.Id.GetValue()));

            var sections = new JObject();
            foreach (var section in AllSections.Where(requested.Contains))
            {
                JToken value;
                switch (section)
                {
                    case "scales":
                        value = new JArray(views.GroupBy(v => v.Scale)
                            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                            .Select(g => new JObject { ["scale"] = g.Key, ["label"] = "1:" + g.Key, ["views"] = g.Count() }));
                        break;
                    case "lineStyles":
                        value = LineStyles(doc, viewIds, top, includeUnused);
                        break;
                    case "textTypes":
                        value = TextTypes(doc, viewIds, top, includeUnused);
                        break;
                    case "dimensionTypes":
                        value = DimensionTypes(doc, viewIds, top, includeUnused);
                        break;
                    case "filledRegionTypes":
                        value = FilledRegionTypes(doc, viewIds, top, includeUnused);
                        break;
                    case "detailComponents":
                        value = DetailComponents(doc, viewIds, top);
                        break;
                    case "annotationSymbols":
                        value = AnnotationSymbols(doc, viewIds, top);
                        break;
                    case "roles":
                        value = Roles(doc, viewIds);
                        break;
                    case "detailGroups":
                        value = Ranked(DraftingLibraryUtils.Owned<Group>(doc, viewIds)
                            .GroupBy(g => g.GroupType?.Name ?? "?")
                            .Select(g => new JObject { ["name"] = g.Key, ["uses"] = g.Count() }), top);
                        break;
                    case "viewportTypes":
                        value = ViewportTypes(doc, top, includeUnused);
                        break;
                    case "titleBlocks":
                        value = Ranked(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                            .WhereElementIsNotElementType()
                            .OfType<FamilyInstance>()
                            .GroupBy(t => (t.Symbol?.FamilyName ?? "?") + "\u0001" + (t.Symbol?.Name ?? "?"))
                            .Select(g => new JObject
                            {
                                ["family"] = g.Key.Split('\u0001')[0],
                                ["type"] = g.Key.Split('\u0001')[1],
                                ["uses"] = g.Count()
                            }), top);
                        break;
                    default:
                        value = ViewNames(views, namePattern, sampleCount, top);
                        break;
                }

                sections[section] = value;
            }

            var result = new JObject
            {
                ["documentTitle"] = doc.Title,
                ["generatedAt"] = DateTime.Now.ToString("yyyy-MM-dd"),
                ["views"] = views.Count,
                ["draftingViewsOnly"] = parameters["viewIds"] == null || !parameters["viewIds"].HasValues,
                ["top"] = top,
                ["sections"] = sections
            };
            return Ok($"Harvested {sections.Count} sections from {views.Count} views of '{doc.Title}'.", result);
        }

        /// <summary>Orders rows by "uses" descending then name, keeps <paramref name="top" />, reports the total.</summary>
        private static JObject Ranked(IEnumerable<JObject> rows, int top)
        {
            var list = rows
                .OrderByDescending(r => r.Value<int>("uses"))
                .ThenBy(r => r.Value<string>("name") ?? r.Value<string>("family"), StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Value<string>("type"), StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new JObject
            {
                ["total"] = list.Count,
                ["truncated"] = list.Count > top,
                ["items"] = new JArray(list.Take(top))
            };
        }

        private static JObject LineStyles(Document doc, HashSet<long> viewIds, int top, bool includeUnused)
        {
            var uses = DraftingLibraryUtils.DetailCurves(doc, viewIds)
                .GroupBy(c => c.LineStyle.Id.GetValue())
                .ToDictionary(g => g.Key, g => g.Count());
            var lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            var rows = new List<JObject>();
            if (lines != null)
            {
                foreach (Category c in lines.SubCategories)
                {
                    var style = c.GetGraphicsStyle(GraphicsStyleType.Projection);
                    var id = style?.Id.GetValue() ?? -1;
                    uses.TryGetValue(id, out var count);
                    if (count == 0 && !includeUnused)
                        continue;
                    rows.Add(new JObject
                    {
                        ["name"] = c.Name,
                        ["id"] = id,
                        ["uses"] = count,
                        ["weight"] = c.GetLineWeight(GraphicsStyleType.Projection),
                        ["color"] = DraftingLibraryUtils.ColorHex(c.LineColor),
                        ["pattern"] = DraftingLibraryUtils.LinePatternName(doc, c.GetLinePatternId(GraphicsStyleType.Projection))
                    });
                }
            }

            var result = Ranked(rows, top);
            result["detailLines"] = uses.Values.Sum();
            return result;
        }

        private static JObject TextTypes(Document doc, HashSet<long> viewIds, int top, bool includeUnused)
        {
            var notes = DraftingLibraryUtils.Owned<TextNote>(doc, viewIds);
            var uses = notes.GroupBy(t => t.GetTypeId().GetValue()).ToDictionary(g => g.Key, g => g.Count());
            var rows = new List<JObject>();
            foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>())
            {
                uses.TryGetValue(t.Id.GetValue(), out var count);
                if (count == 0 && !includeUnused)
                    continue;
                var size = t.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0;
                var arrowId = t.get_Parameter(BuiltInParameter.LEADER_ARROWHEAD)?.AsElementId();
                rows.Add(new JObject
                {
                    ["name"] = t.Name,
                    ["id"] = t.Id.GetValue(),
                    ["uses"] = count,
                    ["sizeMm"] = Math.Round(size * DocumentationUtils.MmPerFoot, 2),
                    ["sizeIn"] = Math.Round(size * 12, 4),
                    ["font"] = t.get_Parameter(BuiltInParameter.TEXT_FONT)?.AsString(),
                    ["bold"] = t.get_Parameter(BuiltInParameter.TEXT_STYLE_BOLD)?.AsInteger() == 1,
                    ["leaderArrowhead"] = arrowId == null || arrowId == ElementId.InvalidElementId
                        ? "None"
                        : doc.GetElement(arrowId)?.Name
                });
            }

            var result = Ranked(rows, top);
            result["textNotes"] = notes.Count;
            result["withLeaders"] = notes.Count(n => n.LeaderCount > 0);
            return result;
        }

        private static JObject DimensionTypes(Document doc, HashSet<long> viewIds, int top, bool includeUnused)
        {
            var dims = DraftingLibraryUtils.Dimensions(doc, viewIds);
            var uses = dims.GroupBy(d => d.GetTypeId().GetValue()).ToDictionary(g => g.Key, g => g.Count());
            var rows = new List<JObject>();
            foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>())
            {
                uses.TryGetValue(t.Id.GetValue(), out var count);
                if (string.IsNullOrEmpty(t.Name) || count == 0 && !includeUnused)
                    continue;
                rows.Add(new JObject
                {
                    ["name"] = t.Name,
                    ["id"] = t.Id.GetValue(),
                    ["uses"] = count,
                    ["style"] = t.StyleType.ToString()
                });
            }

            var overrides = new Dictionary<string, int>();
            foreach (var d in dims)
            {
                try
                {
                    foreach (var o in DraftingLibraryUtils.Overrides(d))
                    {
                        if (string.IsNullOrWhiteSpace(o.valueOverride))
                            continue;
                        overrides.TryGetValue(o.valueOverride, out var n);
                        overrides[o.valueOverride] = n + 1;
                    }
                }
                catch (Exception)
                {
                    // Dimensions with invalid references cannot report segments.
                }
            }

            var result = Ranked(rows, top);
            result["dimensions"] = dims.Count;
            result["commonOverrides"] = new JArray(overrides
                .OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal)
                .Take(Math.Min(top, 30))
                .Select(p => new JObject { ["text"] = p.Key, ["uses"] = p.Value }));
            return result;
        }

        private static JObject FilledRegionTypes(Document doc, HashSet<long> viewIds, int top, bool includeUnused)
        {
            var regions = DraftingLibraryUtils.Owned<FilledRegion>(doc, viewIds);
            var uses = regions.GroupBy(r => r.GetTypeId().GetValue()).ToDictionary(g => g.Key, g => g.Count());
            var rows = new List<JObject>();
            foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>())
            {
                uses.TryGetValue(t.Id.GetValue(), out var count);
                if (count == 0 && !includeUnused)
                    continue;
                rows.Add(new JObject
                {
                    ["name"] = t.Name,
                    ["id"] = t.Id.GetValue(),
                    ["uses"] = count,
                    ["isMasking"] = t.IsMasking,
                    ["cadDerived"] = IsCadName(t.Name)
                });
            }

            var result = Ranked(rows, top);
            result["filledRegions"] = regions.Count;
            return result;
        }

        internal static bool IsCadName(string name) =>
            !string.IsNullOrEmpty(name) && name.IndexOf(".dwg", StringComparison.OrdinalIgnoreCase) >= 0;

        private static JObject DetailComponents(Document doc, HashSet<long> viewIds, int top)
        {
            var rows = DraftingLibraryUtils.DetailComponents(doc, viewIds)
                .Where(f => f.Symbol != null)
                .GroupBy(f => f.Symbol.Id.GetValue())
                .Select(g =>
                {
                    var symbol = g.First().Symbol;
                    var placement = symbol.Family?.FamilyPlacementType == FamilyPlacementType.CurveBasedDetail
                        ? "line"
                        : "point";
                    return new JObject
                    {
                        ["family"] = symbol.FamilyName,
                        ["type"] = symbol.Name,
                        ["typeId"] = g.Key,
                        ["uses"] = g.Count(),
                        ["placement"] = placement,
                        ["role"] = DraftingLibraryUtils.RoleOf(symbol.FamilyName)
                    };
                });
            return Ranked(rows, top);
        }

        private static JObject AnnotationSymbols(Document doc, HashSet<long> viewIds, int top)
        {
            var rows = DraftingLibraryUtils.OwnedOfCategory<AnnotationSymbol>(doc, viewIds,
                    BuiltInCategory.OST_GenericAnnotation)
                .Where(a => a.Symbol != null && a.SuperComponent == null)
                .GroupBy(a => a.Symbol.Id.GetValue())
                .Select(g =>
                {
                    var first = g.First();
                    var symbol = first.Symbol;
                    var editable = first.Parameters.Cast<Parameter>()
                        .Where(p => p.Definition != null && !p.IsReadOnly && p.StorageType != StorageType.ElementId)
                        .Select(p => p.Definition.Name)
                        .Where(n => !SkippedSymbolParameters.Any(x => n.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
                        .Distinct()
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
                    return new JObject
                    {
                        ["family"] = symbol.FamilyName,
                        ["type"] = symbol.Name,
                        ["typeId"] = g.Key,
                        ["uses"] = g.Count(),
                        ["hasLeaders"] = g.Any(HasLeaders),
                        ["role"] = DraftingLibraryUtils.RoleOf(symbol.FamilyName),
                        ["parameters"] = new JArray(editable)
                    };
                });
            return Ranked(rows, top);
        }

        private static bool HasLeaders(AnnotationSymbol symbol)
        {
            try
            {
                return symbol.GetLeaders().Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The five most used family types per role across detail items and generic annotations.</summary>
        private static JObject Roles(Document doc, HashSet<long> viewIds)
        {
            var instances = DraftingLibraryUtils.DetailComponents(doc, viewIds)
                .Cast<FamilyInstance>()
                .Concat(DraftingLibraryUtils.OwnedOfCategory<FamilyInstance>(doc, viewIds,
                    BuiltInCategory.OST_GenericAnnotation).Where(a => a.SuperComponent == null))
                .Where(f => f.Symbol != null);
            var byRole = instances
                .GroupBy(f => DraftingLibraryUtils.RoleOf(f.Symbol.FamilyName))
                .Where(g => g.Key != "other")
                .ToDictionary(g => g.Key, g => g.ToList());

            var result = new JObject();
            foreach (var role in DraftingLibraryUtils.RoleNames.Where(byRole.ContainsKey))
            {
                result[role] = new JArray(byRole[role]
                    .GroupBy(f => f.Symbol.Id.GetValue())
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.First().Symbol.FamilyName, StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .Select(g =>
                    {
                        var symbol = g.First().Symbol;
                        return new JObject
                        {
                            ["family"] = symbol.FamilyName,
                            ["type"] = symbol.Name,
                            ["typeId"] = g.Key,
                            ["uses"] = g.Count(),
                            ["category"] = symbol.Category?.Name,
                            ["placement"] = symbol.Family?.FamilyPlacementType == FamilyPlacementType.CurveBasedDetail
                                ? "line"
                                : "point"
                        };
                    }));
            }

            return result;
        }

        private static JObject ViewportTypes(Document doc, int top, bool includeUnused)
        {
            var viewports = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().ToList();
            var uses = viewports.GroupBy(v => v.GetTypeId().GetValue()).ToDictionary(g => g.Key, g => g.Count());
            var types = new Dictionary<long, Element>();
            foreach (var id in uses.Keys)
            {
                var type = doc.GetElement(id.ToRevitElementId());
                if (type != null)
                    types[id] = type;
            }

            if (includeUnused && viewports.Count > 0)
            {
                foreach (var id in viewports[0].GetValidTypes())
                {
                    var type = doc.GetElement(id);
                    if (type != null)
                        types[id.GetValue()] = type;
                }
            }

            return Ranked(types.Select(p => new JObject
            {
                ["name"] = p.Value.Name,
                ["id"] = p.Key,
                ["uses"] = uses.TryGetValue(p.Key, out var n) ? n : 0
            }), top);
        }

        private static JObject ViewNames(List<View> views, Regex pattern, int sampleCount, int top)
        {
            var prefixes = views
                .Select(v => Regex.Match(v.Name, @"^[A-Za-z]{1,6}(?=[-_ ])"))
                .Where(m => m.Success)
                .GroupBy(m => m.Value.ToUpperInvariant())
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Take(top)
                .Select(g => new JObject { ["prefix"] = g.Key, ["views"] = g.Count() });
            var step = sampleCount == 0 ? 0 : Math.Max(1, views.Count / sampleCount);
            var samples = step == 0
                ? new List<string>()
                : views.Where((v, i) => i % step == 0).Take(sampleCount).Select(v => v.Name).ToList();
            return new JObject
            {
                ["total"] = views.Count,
                ["pattern"] = pattern.ToString(),
                ["matching"] = views.Count(v => pattern.IsMatch(v.Name)),
                ["prefixes"] = new JArray(prefixes),
                ["samples"] = new JArray(samples)
            };
        }
    }
}
