using System.Text.RegularExpressions;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Cad
{
    /// <summary>
    ///     Removes CAD import leftovers: CAD types (CADLinkType) without
    ///     instances, and imported filled region types / fill patterns whose
    ///     names match "*.dwg*" style patterns. Regions on matched types can be
    ///     switched to a native type first (remapTo). A dry run executes the
    ///     same steps and rolls them back, so the report is exact.
    /// </summary>
    public class PurgeCadLeftoversEventHandler : JsonParameterEventHandler
    {
        private const int MaxListed = 500;
        private static readonly string[] DefaultPatterns = { "*.dwg", "*.dwg-*", "*.dwg *", "*.dxf", "*.dxf-*", "*.dxf *" };
        private static readonly Regex ImportSuffix = new Regex(@"\.(dwg|dxf)([-\s]*\d+)?$", RegexOptions.IgnoreCase);

        public override string GetName() => "Purge CAD Leftovers";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var dryRun = parameters.Value<bool?>("dryRun") ?? true;
            var include = new HashSet<string>(CadUtils.ReadStrings(parameters["include"]), StringComparer.OrdinalIgnoreCase);
            if (include.Count == 0)
                include = new HashSet<string>(new[] { "cadTypes", "filledRegionTypes" }, StringComparer.OrdinalIgnoreCase);
            var patterns = CadUtils.ReadStrings(parameters["namePatterns"]);
            var matches = CadUtils.Matcher(patterns.Count > 0 ? patterns : DefaultPatterns);

            var cadTypes = new List<object>();
            var regionTypes = new List<object>();
            var fillPatterns = new List<object>();
            var duplicateGroups = new List<object>();
            var remapped = 0;
            string remapName = null;

            using (var transaction = DocumentationUtils.StartTransaction(doc, "MCP: Purge CAD Leftovers"))
            {
                if (include.Contains("filledRegionTypes"))
                {
                    var allTypes = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType))
                        .Cast<FilledRegionType>().ToList();
                    var matched = allTypes.Where(t => matches(t.Name)).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
                    var matchedIds = new HashSet<long>(matched.Select(t => t.Id.GetValue()));
                    var nativeNames = new HashSet<string>(allTypes.Where(t => !matchedIds.Contains(t.Id.GetValue())).Select(t => t.Name),
                        StringComparer.OrdinalIgnoreCase);

                    foreach (var group in matched.GroupBy(t => BaseName(t.Name), StringComparer.OrdinalIgnoreCase))
                    {
                        if (group.Count() > 1 || nativeNames.Contains(group.Key))
                            duplicateGroups.Add(new
                            {
                                baseName = group.Key,
                                nativeTypeExists = nativeNames.Contains(group.Key),
                                types = group.Select(t => t.Name).ToList()
                            });
                    }

                    var target = ResolveTarget(doc, parameters["remapTo"]);
                    if (target != null)
                    {
                        if (matchedIds.Contains(target.Id.GetValue()))
                            throw new ArgumentException($"'remapTo' type '{target.Name}' is itself a CAD leftover.");
                        remapName = target.Name;
                        foreach (var region in new FilteredElementCollector(doc).OfClass(typeof(FilledRegion)).Cast<FilledRegion>()
                                     .Where(r => matchedIds.Contains(r.GetTypeId().GetValue())).ToList())
                        {
                            region.ChangeTypeId(target.Id);
                            remapped++;
                        }
                    }

                    var usage = new FilteredElementCollector(doc).OfClass(typeof(FilledRegion)).Cast<FilledRegion>()
                        .GroupBy(r => r.GetTypeId().GetValue()).ToDictionary(g => g.Key, g => g.Count());
                    var remaining = allTypes.Count;
                    foreach (var type in matched)
                    {
                        usage.TryGetValue(type.Id.GetValue(), out var used);
                        if (used > 0)
                        {
                            regionTypes.Add(Entry(type, "kept", $"used by {used} filled regions"));
                            continue;
                        }

                        if (remaining <= 1)
                        {
                            regionTypes.Add(Entry(type, "kept", "last filled region type"));
                            continue;
                        }

                        regionTypes.Add(TryDelete(doc, type, ref remaining));
                    }
                }

                if (include.Contains("cadTypes"))
                {
                    var used = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(ImportInstance))
                        .Select(i => i.GetTypeId().GetValue()));
                    foreach (var type in new FilteredElementCollector(doc).OfClass(typeof(CADLinkType)).Cast<CADLinkType>()
                                 .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        if (used.Contains(type.Id.GetValue()))
                            continue;
                        // Read link info before the element is deleted
                        var linked = type.IsExternalFileReference();
                        var path = CadUtils.LinkPath(doc, type.Id);
                        var count = 0;
                        var entry = JObject.FromObject(TryDelete(doc, type, ref count));
                        entry["linked"] = linked;
                        entry["path"] = path;
                        cadTypes.Add(entry);
                    }
                }

                if (include.Contains("fillPatterns"))
                {
                    doc.Regenerate();
                    var used = UsedFillPatterns(doc);
                    foreach (var pattern in new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement))
                                 .Cast<FillPatternElement>().Where(p => matches(p.Name))
                                 .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        if (used.Contains(pattern.Id.GetValue()))
                        {
                            fillPatterns.Add(Entry(pattern, "kept", "used by a filled region type, material, or view/template filter or category override (element overrides not checked)"));
                            continue;
                        }

                        var count = 0;
                        fillPatterns.Add(TryDelete(doc, pattern, ref count));
                    }
                }

                if (dryRun)
                {
                    transaction.RollBack();
                }
                else
                {
                    var status = transaction.Commit();
                    if (status != TransactionStatus.Committed)
                        return Fail($"Purge was not committed ({status}).");
                }
            }

            int Deleted(List<object> list) => list.Count(e => JObject.FromObject(e).Value<string>("action") == "deleted");
            var deletedCad = Deleted(cadTypes);
            var deletedRegion = Deleted(regionTypes);
            var deletedPattern = Deleted(fillPatterns);
            var verb = dryRun ? "Would delete" : "Deleted";
            return Ok($"{verb} {deletedCad} CAD types, {deletedRegion} filled region types and {deletedPattern} fill patterns" +
                      (remapped > 0 ? $" ({(dryRun ? "would remap" : "remapped")} {remapped} regions to '{remapName}')." : "."), new
            {
                dryRun,
                remapTo = remapName,
                remappedRegions = remapped,
                deleted = new { cadTypes = deletedCad, filledRegionTypes = deletedRegion, fillPatterns = deletedPattern },
                cadTypes = cadTypes.Take(MaxListed).ToList(),
                filledRegionTypes = regionTypes.Take(MaxListed).ToList(),
                fillPatterns = fillPatterns.Take(MaxListed).ToList(),
                duplicateGroups = duplicateGroups.Take(MaxListed).ToList(),
                note = dryRun ? "Dry run: changes were executed and rolled back; pass dryRun:false to apply." : null
            });
        }

        private static string BaseName(string name) => ImportSuffix.Replace(name, "").Trim();

        private static object Entry(Element element, string action, string reason)
        {
            return new { id = element.Id.GetValue(), name = element.Name, action, reason };
        }

        private static object TryDelete(Document doc, Element element, ref int remaining)
        {
            var id = element.Id.GetValue();
            var name = element.Name;
            var sub = new SubTransaction(doc);
            sub.Start();
            try
            {
                doc.Delete(element.Id);
                sub.Commit();
                remaining--;
                return new { id, name, action = "deleted", reason = "unused" };
            }
            catch (Exception ex)
            {
                if (sub.HasStarted())
                    sub.RollBack();
                return new { id, name, action = "failed", reason = ex.Message };
            }
        }

        private static FilledRegionType ResolveTarget(Document doc, JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token.Type == JTokenType.Integer)
                return DocumentationUtils.GetElement<FilledRegionType>(doc, token.Value<long>())
                       ?? throw new ArgumentException($"'remapTo' {token} is not a filled region type.");
            var name = token.Value<string>()?.Trim();
            return new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
                       .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"Filled region type '{name}' not found.");
        }

        /// <summary>Fill patterns referenced by filled region types, materials and view filter overrides.</summary>
        private static HashSet<long> UsedFillPatterns(Document doc)
        {
            var used = new HashSet<long>();

            void Add(ElementId id)
            {
                if (id != null && id != ElementId.InvalidElementId)
                    used.Add(id.GetValue());
            }

            foreach (var type in new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>())
            {
                Add(type.ForegroundPatternId);
                Add(type.BackgroundPatternId);
            }

            foreach (var material in new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>())
            {
                Add(material.SurfaceForegroundPatternId);
                Add(material.SurfaceBackgroundPatternId);
                Add(material.CutForegroundPatternId);
                Add(material.CutBackgroundPatternId);
            }

            void AddOverrides(OverrideGraphicSettings overrides)
            {
                if (overrides == null)
                    return;
                Add(overrides.SurfaceForegroundPatternId);
                Add(overrides.SurfaceBackgroundPatternId);
                Add(overrides.CutForegroundPatternId);
                Add(overrides.CutBackgroundPatternId);
            }

            // Model + annotation categories and their subcategories (V/G category overrides)
            var categoryIds = new List<ElementId>();
            foreach (Category category in doc.Settings.Categories)
            {
                categoryIds.Add(category.Id);
                foreach (Category sub in category.SubCategories)
                    categoryIds.Add(sub.Id);
            }

            // Views and view templates: filter and category overrides. Per-element
            // overrides (View.GetElementOverrides) are not scanned - too costly.
            foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                ICollection<ElementId> filters;
                try
                {
                    if (!view.AreGraphicsOverridesAllowed())
                        continue;
                    filters = view.GetFilters();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var filterId in filters)
                    AddOverrides(view.GetFilterOverrides(filterId));

                foreach (var categoryId in categoryIds)
                {
                    try
                    {
                        AddOverrides(view.GetCategoryOverrides(categoryId));
                    }
                    catch (Exception)
                    {
                        // Category not overridable in this view
                    }
                }
            }

            return used;
        }
    }
}
