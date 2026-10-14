using System.Text.RegularExpressions;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Library
{
    /// <summary>
    ///     Finds duplicate families/types (same category, same type parameter values, same
    ///     drawn geometry - see <see cref="FamilyFingerprint" />) and merges them: instances
    ///     are switched to the kept type, element-id type parameters and default types that
    ///     point at the duplicate are re-pointed, then the duplicate type is deleted (or its
    ///     whole family when every type of it was merged). Instances inside groups or nested
    ///     in other families are never switched; their type is kept and reported.
    ///     Keep order: depended-on (referenced by other types, default type, used in groups),
    ///     then preferNamePattern, then the un-numbered name, then the most used.
    ///     Explicit pairs force a merge decision but are still compared unless force:true.
    /// </summary>
    public class MergeDuplicateFamiliesEventHandler : JsonParameterEventHandler
    {
        private const int MaxListedIds = 20;

        public override string GetName() => "Merge Duplicate Families";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var dryRun = parameters.Value<bool?>("dryRun") ?? true;
            var force = parameters.Value<bool?>("force") ?? false;
            var categories = ReadStrings(parameters["categories"]);
            var familyPatterns = ReadStrings(parameters["familyNamePattern"]);
            var includeSystemTypes = parameters.Value<bool?>("includeSystemTypes") ?? false;
            var preferPatterns = ReadStrings(parameters["preferNamePattern"]);
            var ignore = new HashSet<string>(ReadStrings(parameters["ignoreParameters"]), StringComparer.OrdinalIgnoreCase);
            var maxFamilyOpens = parameters.Value<int?>("maxFamilyOpens") ?? 300;
            var maxResults = parameters.Value<int?>("maxResults") ?? 500;
            var pairs = parameters["pairs"] as JArray;
            if (force && (pairs == null || pairs.Count == 0))
                throw new ArgumentException("force only applies to explicit pairs.");
            foreach (var name in categories)
                if (CategoryNameUtils.ResolveCategory(doc, name) == null)
                    throw new ArgumentException($"Unknown category '{name}'.");

            var context = new Context(doc, ignore, maxFamilyOpens);
            var warnings = new JArray();
            var notMerged = new JArray();
            var plan = new List<MergeItem>();

            if (pairs != null && pairs.Count > 0)
            {
                PlanPairs(context, pairs, force, plan, notMerged);
            }
            else
            {
                var scope = new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>()
                    .Where(t => t is FamilySymbol symbol
                        ? !symbol.Family.IsInPlace
                        : includeSystemTypes && t.Category != null && IsMergeableSystemType(t))
                    .Where(t => SourceDocumentUtils.InCategories(t.Category, categories))
                    .Where(t => SourceDocumentUtils.LikeAny(SafeFamilyName(t), familyPatterns))
                    .ToList();
                PlanAuto(context, scope, preferPatterns, plan, notMerged);
            }

            context.ScanUsage(plan.SelectMany(i => new[] { i.Keep, i.Merge }));

            var familiesBefore = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .Count(f => SourceDocumentUtils.InCategories(f.FamilyCategory, categories));

            List<Deletion> deletions;
            if (dryRun)
            {
                DryRun(plan);
                deletions = DeleteMerged(doc, plan, true);
            }
            else
            {
                using (var transaction = DocumentationUtils.StartTransaction(doc, "MCP: Merge Duplicate Families"))
                {
                    SwitchInstances(doc, plan);
                    deletions = DeleteMerged(doc, plan, false);
                    var status = transaction.Commit();
                    if (status != TransactionStatus.Committed)
                    {
                        foreach (var item in plan)
                        {
                            item.Failed = true;
                            item.Deleted = false;
                            item.FamilyDeleted = false;
                            item.Switched = 0;
                            item.Repointed = 0;
                            item.AddWarning($"Transaction was not committed ({status}).");
                        }

                        deletions.ForEach(d => d.Done = false);
                    }
                }
            }

            foreach (var item in plan)
                foreach (var warning in item.Warnings)
                    warnings.Add($"{item.Merge.DisplayName}: {warning}");
            var results = plan.Select(i => Report(i, dryRun)).ToList();

            var familiesDeleted = deletions.Count(d => d.IsFamily && d.Done);
            var typesMerged = plan.Count(i => i.Deleted);
            var counts = new JObject
            {
                ["familiesBefore"] = familiesBefore,
                ["familiesAfter"] = familiesBefore - familiesDeleted,
                ["familiesDeleted"] = familiesDeleted,
                ["typesMerged"] = typesMerged,
                ["typesKeptPartially"] = plan.Count(i => !i.Deleted && !i.Failed),
                ["instancesSwitched"] = plan.Sum(i => i.Switched),
                ["referencesRepointed"] = plan.Sum(i => i.Repointed),
                ["instancesSkippedInGroups"] = plan.Sum(i => i.Merge.GroupInstances.Count),
                ["instancesSkippedNested"] = plan.Sum(i => i.Merge.NestedInstances.Count),
                ["failed"] = plan.Count(i => i.Failed),
                ["notMerged"] = notMerged.Count,
                ["familiesOpened"] = context.FamilyOpens,
                ["warnings"] = warnings.Count
            };

            var response = new JObject
            {
                ["dryRun"] = dryRun,
                ["force"] = force,
                ["counts"] = counts,
                ["merges"] = new JArray(results.Take(maxResults)),
                ["notMerged"] = new JArray(notMerged.Take(maxResults)),
                ["warnings"] = warnings,
                ["fingerprint"] = "type parameters (names/identity skipped) + FamilySymbol geometry + family drawing (EditFamily read-only: lines, regions, text/labels, forms, nested placement, imports; coordinates to 1e-4 ft)"
            };
            if (results.Count > maxResults || notMerged.Count > maxResults)
                response["truncated"] = $"Lists capped at maxResults ({maxResults}).";

            var message = (dryRun ? "Dry run: would merge " : "Merged ") +
                          $"{typesMerged} types ({familiesDeleted} families removed, {familiesBefore} -> {familiesBefore - familiesDeleted}), " +
                          $"{counts["instancesSwitched"]} instances switched, {counts["instancesSkippedInGroups"]} skipped in groups, {notMerged.Count} candidates not merged." +
                          (dryRun ? " Nothing changed; run with dryRun:false to apply." : "");
            return Ok(message, response);
        }

        // ---------------------------------------------------------------- planning

        private static void PlanAuto(Context context, List<ElementType> scope, List<string> preferPatterns,
            List<MergeItem> plan, JArray notMerged)
        {
            var candidates = scope.Select(context.Get).ToList();
            var groups = candidates
                .GroupBy(c => c.CategoryId + "|" + c.ClassName + "|" + c.ParamHash + "|" + c.GeometryHash)
                .Where(g => g.Count() > 1)
                .ToList();

            var clusters = new List<List<Candidate>>();
            foreach (var group in groups)
            {
                var members = group.ToList();
                var byDrawing = new Dictionary<string, List<Candidate>>();
                foreach (var member in members)
                {
                    var drawing = context.Drawing(member, out var error);
                    if (drawing == null)
                    {
                        notMerged.Add(new JObject
                        {
                            ["types"] = new JArray(Describe(member)),
                            ["reason"] = "Drawing not compared: " + error
                        });
                        continue;
                    }

                    if (!byDrawing.TryGetValue(drawing, out var list))
                        byDrawing[drawing] = list = new List<Candidate>();
                    list.Add(member);
                }

                if (byDrawing.Count > 1)
                    notMerged.Add(new JObject
                    {
                        ["types"] = new JArray(members.Select(Describe)),
                        ["reason"] = $"Same type parameters but the drawn geometry differs ({byDrawing.Count} different drawings).",
                        ["drawings"] = new JArray(byDrawing.Values.Select(l => new JObject
                        {
                            ["types"] = new JArray(l.Select(c => c.DisplayName)),
                            ["content"] = context.DrawingSummary(l[0])
                        }))
                    });

                clusters.AddRange(byDrawing.Values.Where(l => l.Count > 1));
            }

            if (clusters.Count == 0)
                return;

            // Usage decides the keeper, so scan it before ranking.
            context.ScanUsage(clusters.SelectMany(c => c));
            foreach (var cluster in clusters)
            {
                var keep = cluster
                    .OrderByDescending(c => c.DependScore > 0)
                    .ThenByDescending(c => preferPatterns.Count > 0 && (SourceDocumentUtils.LikeAny(c.FamilyName, preferPatterns) || SourceDocumentUtils.LikeAny(c.TypeName, preferPatterns) || SourceDocumentUtils.LikeAny(c.DisplayName, preferPatterns)))
                    .ThenBy(c => IsNumbered(c, cluster))
                    .ThenByDescending(c => c.Uses)
                    .ThenByDescending(c => c.DependScore)
                    .ThenBy(c => c.Id.GetValue())
                    .First();
                foreach (var member in cluster.Where(c => c != keep))
                    plan.Add(new MergeItem(keep, member, "auto"));
            }
        }

        private static void PlanPairs(Context context, JArray pairs, bool force, List<MergeItem> plan, JArray notMerged)
        {
            var merged = new HashSet<long>();
            var kept = new HashSet<long>();
            for (var index = 0; index < pairs.Count; index++)
            {
                var pair = pairs[index] as JObject;
                var entry = new JObject { ["pair"] = index };
                try
                {
                    if (pair == null)
                        throw new ArgumentException("Each pair needs 'keep' and 'merge'.");
                    var keep = Resolve(context.Doc, pair["keep"], "keep");
                    var merge = Resolve(context.Doc, pair["merge"], "merge");
                    if (keep.Id == merge.Id)
                        throw new ArgumentException("keep and merge are the same element.");

                    var items = new List<MergeItem>();
                    if (keep is Family keepFamily && merge is Family mergeFamily)
                        items.AddRange(PairFamilies(context, keepFamily, mergeFamily, force, entry));
                    else if (keep is ElementType keepType && merge is ElementType mergeType)
                    {
                        if (!IsMergeableSystemType(keepType) || !IsMergeableSystemType(mergeType))
                            throw new ArgumentException("Group, assembly, link and import types can't be merged.");
                        items.AddRange(PairTypes(context, context.Get(keepType), context.Get(mergeType), force, entry));
                    }
                    else
                        throw new ArgumentException("keep and merge must both be families or both be types.");

                    foreach (var item in items)
                    {
                        var keepId = item.Keep.Id.GetValue();
                        var mergeId = item.Merge.Id.GetValue();
                        if (merged.Contains(mergeId) || merged.Contains(keepId) || kept.Contains(mergeId))
                        {
                            notMerged.Add(new JObject
                            {
                                ["pair"] = index,
                                ["types"] = new JArray(Describe(item.Keep), Describe(item.Merge)),
                                ["reason"] = "Type is already merged or kept by another pair in this call."
                            });
                            continue;
                        }

                        merged.Add(mergeId);
                        kept.Add(keepId);
                        plan.Add(item);
                    }

                    if (entry["reason"] != null)
                        notMerged.Add(entry);
                }
                catch (ArgumentException ex)
                {
                    entry["reason"] = ex.Message;
                    notMerged.Add(entry);
                }
            }
        }

        private static IEnumerable<MergeItem> PairTypes(Context context, Candidate keep, Candidate merge, bool force, JObject entry)
        {
            if (keep.CategoryId != merge.CategoryId || keep.ClassName != merge.ClassName)
                throw new ArgumentException($"Different categories/kinds: {keep.DisplayName} ({keep.CategoryName}) vs {merge.DisplayName} ({merge.CategoryName}).");
            if (force)
                return new[] { new MergeItem(keep, merge, "pair-forced") };

            var reasons = Compare(context, keep, merge);
            if (reasons.Count == 0)
                return new[] { new MergeItem(keep, merge, "pair") };
            entry["types"] = new JArray(Describe(keep), Describe(merge));
            entry["reason"] = "Not identical (use force:true to merge anyway).";
            entry["differences"] = new JArray(reasons.Take(25));
            return new MergeItem[0];
        }

        private static IEnumerable<MergeItem> PairFamilies(Context context, Family keepFamily, Family mergeFamily, bool force, JObject entry)
        {
            if (keepFamily.FamilyCategory?.Id.GetValue() != mergeFamily.FamilyCategory?.Id.GetValue())
                throw new ArgumentException($"Different categories: {keepFamily.Name} ({keepFamily.FamilyCategory?.Name}) vs {mergeFamily.Name} ({mergeFamily.FamilyCategory?.Name}).");

            var keepTypes = keepFamily.GetFamilySymbolIds().Select(id => context.Get((ElementType)context.Doc.GetElement(id))).ToList();
            var mergeTypes = mergeFamily.GetFamilySymbolIds().Select(id => context.Get((ElementType)context.Doc.GetElement(id))).ToList();
            if (keepTypes.Count == 0 || mergeTypes.Count == 0)
                throw new ArgumentException("Both families need at least one type.");

            if (!force)
            {
                var keepDrawing = context.Drawing(keepTypes[0], out var keepError);
                var mergeDrawing = context.Drawing(mergeTypes[0], out var mergeError);
                if (keepDrawing == null || mergeDrawing == null)
                    throw new ArgumentException("Drawing not compared: " + (keepError ?? mergeError) + " Use force:true to merge anyway.");
                if (keepDrawing != mergeDrawing)
                    throw new ArgumentException($"The drawn geometry differs (keep: {context.DrawingSummary(keepTypes[0])}; merge: {context.DrawingSummary(mergeTypes[0])}). Use force:true to merge anyway.");
            }

            var items = new List<MergeItem>();
            var unmatched = new JArray();
            foreach (var merge in mergeTypes)
            {
                var sameName = keepTypes.FirstOrDefault(k => string.Equals(k.TypeName, merge.TypeName, StringComparison.OrdinalIgnoreCase));
                var target = keepTypes.FirstOrDefault(k => k.ParamHash == merge.ParamHash && k.GeometryHash == merge.GeometryHash && k == sameName)
                             ?? keepTypes.FirstOrDefault(k => k.ParamHash == merge.ParamHash && k.GeometryHash == merge.GeometryHash);
                if (target == null && force)
                    target = sameName ?? (keepTypes.Count == 1 ? keepTypes[0] : null);
                if (target != null)
                {
                    items.Add(new MergeItem(target, merge, force ? "pair-forced" : "pair"));
                    continue;
                }

                var compareWith = sameName ?? keepTypes[0];
                unmatched.Add(new JObject
                {
                    ["type"] = Describe(merge),
                    ["closest"] = compareWith.DisplayName,
                    ["differences"] = new JArray(Compare(context, compareWith, merge).Take(15))
                });
            }

            if (unmatched.Count > 0)
            {
                entry["reason"] = $"{unmatched.Count} type(s) of '{mergeFamily.Name}' have no identical type in '{keepFamily.Name}' and were left (use force:true to map by type name).";
                entry["unmatched"] = unmatched;
            }

            return items;
        }

        private static List<string> Compare(Context context, Candidate keep, Candidate merge)
        {
            var reasons = FamilyFingerprint.Differences(keep.Parameters, merge.Parameters).Select(d => "parameter " + d).ToList();
            if (keep.GeometryHash != merge.GeometryHash)
                reasons.Add("type geometry differs");
            var keepDrawing = context.Drawing(keep, out var keepError);
            var mergeDrawing = context.Drawing(merge, out var mergeError);
            if (keepDrawing == null || mergeDrawing == null)
                reasons.Add("drawing not compared: " + (keepError ?? mergeError));
            else if (keepDrawing != mergeDrawing)
                reasons.Add($"drawn geometry differs (keep: {context.DrawingSummary(keep)}; merge: {context.DrawingSummary(merge)})");
            return reasons;
        }

        private static Element Resolve(Document doc, JToken token, string label)
        {
            if (token == null || token.Type == JTokenType.Null)
                throw new ArgumentException($"'{label}' is required.");
            if (token.Type == JTokenType.Integer)
            {
                var element = doc.GetElement(token.Value<long>().ToRevitElementId());
                if (element is Family || element is ElementType)
                    return element;
                throw new ArgumentException($"{label} id {token} is not a family or type.");
            }

            var text = token.ToString().Trim();
            var colon = text.IndexOf(':');
            if (colon > 0)
            {
                var familyName = text.Substring(0, colon).Trim();
                var typeName = text.Substring(colon + 1).Trim();
                var matches = new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>()
                    .Where(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(SafeFamilyName(t), familyName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matches.Count == 1)
                    return matches[0];
                throw new ArgumentException(matches.Count == 0
                    ? $"{label}: no type '{typeName}' in family '{familyName}'."
                    : $"{label}: '{text}' matches {matches.Count} types; use the type id.");
            }

            var family = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, text, StringComparison.OrdinalIgnoreCase));
            if (family != null)
                return family;
            var types = new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>()
                .Where(t => string.Equals(t.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (types.Count == 1)
                return types[0];
            throw new ArgumentException(types.Count == 0
                ? $"{label}: no family or type named '{text}'."
                : $"{label}: '{text}' matches {types.Count} types; use 'Family : Type' or the type id.");
        }

        /// <summary>True when the name is another member's name plus a number ("Callout 2", "Callout1").</summary>
        private static bool IsNumbered(Candidate candidate, List<Candidate> cluster)
        {
            return cluster.Any(other => other != candidate &&
                                        (NumberedOf(candidate.FamilyName, other.FamilyName) ||
                                         (string.Equals(candidate.FamilyName, other.FamilyName, StringComparison.OrdinalIgnoreCase) && NumberedOf(candidate.TypeName, other.TypeName)) ||
                                         (NumberedOf(candidate.TypeName, other.TypeName) && !NumberedOf(other.FamilyName, candidate.FamilyName))));
        }

        private static bool NumberedOf(string name, string baseName)
        {
            return !string.IsNullOrEmpty(baseName) && name != null && name.Length > baseName.Length
                   && name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)
                   && Regex.IsMatch(name.Substring(baseName.Length), @"^\s*\d+$");
        }

        // ---------------------------------------------------------------- execution

        private static void DryRun(List<MergeItem> plan)
        {
            foreach (var item in plan)
            {
                item.Switched = item.Merge.Instances.Count;
                item.Repointed = item.Merge.References.Count(r => !r.ReadOnly);
                item.Remaining = item.Merge.GroupInstances.Count + item.Merge.NestedInstances.Count + item.Merge.References.Count(r => r.ReadOnly);
            }
        }

        /// <summary>
        ///     Switches instances to the kept type and re-points element-id type parameters
        ///     and default types; one sub-transaction per merge inside the caller's transaction.
        /// </summary>
        private static void SwitchInstances(Document doc, List<MergeItem> plan)
        {
            foreach (var item in plan)
            {
                var sub = new SubTransaction(doc);
                sub.Start();
                try
                {
                    var keepId = item.Keep.Id;
                    if (item.Keep.Type is FamilySymbol keepSymbol && !keepSymbol.IsActive)
                        keepSymbol.Activate();

                    foreach (var id in item.Merge.Instances)
                    {
                        try
                        {
                            doc.GetElement(id)?.ChangeTypeId(keepId);
                            item.Switched++;
                        }
                        catch (Exception ex)
                        {
                            item.Remaining++;
                            item.AddWarning($"Instance {id.GetValue()} not switched: {ex.Message}");
                        }
                    }

                    foreach (var reference in item.Merge.References)
                    {
                        var owner = doc.GetElement(reference.Owner);
                        var parameter = owner?.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Id == reference.ParameterId);
                        if (reference.ReadOnly || parameter == null || parameter.IsReadOnly || !SafeSet(parameter, keepId))
                        {
                            item.Remaining++;
                            item.AddWarning($"Reference from '{owner?.Name}' ({reference.Owner.GetValue()}) parameter '{parameter?.Definition?.Name}' not re-pointed.");
                            continue;
                        }

                        item.Repointed++;
                    }

                    foreach (var group in item.Merge.DefaultGroups)
                        try
                        {
                            doc.SetDefaultElementTypeId(group, keepId);
                        }
                        catch (Exception ex)
                        {
                            item.Remaining++;
                            item.AddWarning($"Default type for {group} not changed: {ex.Message}");
                        }

                    if (item.Merge.DefaultForCategory != null)
                        try
                        {
                            doc.SetDefaultFamilyTypeId(item.Merge.DefaultForCategory, keepId);
                        }
                        catch (Exception ex)
                        {
                            item.Remaining++;
                            item.AddWarning($"Default family type not changed: {ex.Message}");
                        }

                    item.Remaining += item.Merge.GroupInstances.Count + item.Merge.NestedInstances.Count;
                    sub.Commit();
                }
                catch (Exception ex)
                {
                    if (sub.HasStarted())
                        sub.RollBack();
                    item.Failed = true;
                    item.Switched = 0;
                    item.Repointed = 0;
                    item.AddWarning("Merge failed: " + ex.Message);
                }
            }
        }

        private class Deletion
        {
            public ElementId Id;
            public bool IsFamily;
            public bool Done;
            public List<MergeItem> Items = new List<MergeItem>();
        }

        /// <summary>
        ///     Deletes (or, in a dry run, checks) the fully merged types; the family is deleted
        ///     instead when all of its types are merged away. A deletion that would take any
        ///     non-type element with it (an instance not switched, a view ...) is skipped.
        ///     Runs inside the caller's transaction when not a dry run.
        /// </summary>
        private static List<Deletion> DeleteMerged(Document doc, List<MergeItem> plan, bool dryRun)
        {
            var deletions = new List<Deletion>();
            var ready = plan.Where(i => !i.Failed && i.Remaining == 0).ToList();
            var readyIds = new HashSet<long>(ready.Select(i => i.Merge.Id.GetValue()));
            var keptIds = new HashSet<long>(plan.Select(i => i.Keep.Id.GetValue()));

            foreach (var byFamily in ready.GroupBy(i => i.Merge.Family?.Id.GetValue() ?? -i.Merge.Id.GetValue()))
            {
                var family = byFamily.First().Merge.Family;
                if (family != null && family.GetFamilySymbolIds().All(id => readyIds.Contains(id.GetValue()) && !keptIds.Contains(id.GetValue())))
                    deletions.Add(new Deletion { Id = family.Id, IsFamily = true, Items = byFamily.ToList() });
                else
                    deletions.AddRange(byFamily.Select(i => new Deletion { Id = i.Merge.Id, Items = new List<MergeItem> { i } }));
            }

            foreach (var deletion in deletions)
            {
                // In a dry run the instances are not switched yet; they would be by then.
                var allowed = dryRun
                    ? new HashSet<long>(deletion.Items.SelectMany(i => i.Merge.Instances).Select(id => id.GetValue()))
                    : new HashSet<long>();
                var blockers = Blockers(doc, deletion.Id, allowed);
                if (blockers.Count > 0)
                {
                    foreach (var item in deletion.Items)
                        item.AddWarning($"Not deleted: would also delete {blockers.Count} other element(s), e.g. {string.Join(", ", blockers.Take(5))}.");
                    continue;
                }

                if (dryRun)
                {
                    deletion.Done = true;
                }
                else
                {
                    var sub = new SubTransaction(doc);
                    sub.Start();
                    try
                    {
                        doc.Delete(deletion.Id);
                        sub.Commit();
                        deletion.Done = true;
                    }
                    catch (Exception ex)
                    {
                        if (sub.HasStarted())
                            sub.RollBack();
                        foreach (var item in deletion.Items)
                            item.AddWarning("Delete failed: " + ex.Message);
                    }
                }

                if (deletion.Done)
                    foreach (var item in deletion.Items)
                    {
                        item.Deleted = true;
                        item.FamilyDeleted = deletion.IsFamily;
                    }
            }

            return deletions;
        }

        private static List<long> Blockers(Document doc, ElementId id, HashSet<long> allowed)
        {
            var element = doc.GetElement(id);
            if (element == null)
                return new List<long>();
            return element.GetDependentElements(null)
                .Where(dep => dep != id && !allowed.Contains(dep.GetValue()))
                .Where(dep =>
                {
                    var dependent = doc.GetElement(dep);
                    return dependent != null && !(dependent is ElementType) && !(dependent is GraphicsStyle) && !(dependent is Family);
                })
                .Select(dep => dep.GetValue())
                .ToList();
        }

        private static bool SafeSet(Parameter parameter, ElementId value)
        {
            try
            {
                return parameter.Set(value);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static JObject Report(MergeItem item, bool dryRun)
        {
            var status = item.Failed ? "failed"
                : item.Deleted ? (dryRun ? "wouldMerge" : "merged")
                : "partial";
            var report = new JObject
            {
                ["status"] = status,
                ["keep"] = Describe(item.Keep),
                ["merge"] = Describe(item.Merge),
                ["match"] = item.Match,
                ["instancesSwitched"] = item.Switched,
                ["referencesRepointed"] = item.Repointed
            };
            if (item.Merge.GroupInstances.Count > 0)
                report["skippedInGroups"] = new JArray(item.Merge.GroupInstances.Take(MaxListedIds).Select(id => id.GetValue()));
            if (item.Merge.NestedInstances.Count > 0)
                report["skippedNested"] = new JArray(item.Merge.NestedInstances.Take(MaxListedIds).Select(id => id.GetValue()));
            if (status == "partial")
                report["note"] = "Type kept: some instances are in groups / nested families, or a reference could not be re-pointed.";
            if (item.FamilyDeleted)
                report["familyDeleted"] = true;
            if (item.Warnings.Count > 0)
                report["warnings"] = new JArray(item.Warnings);
            return report;
        }

        private static JObject Describe(Candidate candidate)
        {
            var entry = new JObject
            {
                ["id"] = candidate.Id.GetValue(),
                ["category"] = candidate.CategoryName,
                ["family"] = candidate.FamilyName,
                ["type"] = candidate.TypeName
            };
            if (candidate.UsageScanned)
            {
                entry["uses"] = candidate.Uses;
                if (candidate.DependScore > 0)
                    entry["dependedOn"] = candidate.DependScore;
            }

            return entry;
        }

        /// <summary>Group, assembly, link and import types are never merged (switching them changes content).</summary>
        private static bool IsMergeableSystemType(ElementType type)
        {
            return !(type is GroupType) && !(type is AssemblyType) && !(type is CADLinkType) && !(type is RevitLinkType);
        }

        private static string SafeFamilyName(ElementType type)
        {
            try
            {
                return type.FamilyName ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static List<string> ReadStrings(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return new List<string>();
            if (token.Type == JTokenType.Array)
                return token.Values<string>().Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            var single = token.ToString();
            return string.IsNullOrWhiteSpace(single) ? new List<string>() : new List<string> { single };
        }

        // ---------------------------------------------------------------- model

        private class Reference
        {
            public ElementId Owner;
            public ElementId ParameterId;
            public bool ReadOnly;
        }

        private class Candidate
        {
            public Candidate(ElementType type, ISet<string> ignore)
            {
                Type = type;
                Id = type.Id;
                Family = (type as FamilySymbol)?.Family;
                FamilyName = SafeFamilyName(type);
                TypeName = type.Name;
                var category = type.Category ?? Family?.FamilyCategory;
                CategoryId = category?.Id.GetValue() ?? -1;
                CategoryName = category?.Name;
                ClassName = type.GetType().Name;
                Parameters = FamilyFingerprint.TypeParameters(type, ignore);
                ParamHash = FamilyFingerprint.Hash(Parameters);
                GeometryHash = FamilyFingerprint.TypeGeometry(type);
            }

            public ElementType Type { get; }
            public ElementId Id { get; }
            public Family Family { get; }
            public string FamilyName { get; }
            public string TypeName { get; }
            public string DisplayName => string.IsNullOrEmpty(FamilyName) ? TypeName : FamilyName + " : " + TypeName;
            public long CategoryId { get; }
            public string CategoryName { get; }
            public string ClassName { get; }
            public SortedDictionary<string, KeyValuePair<string, string>> Parameters { get; }
            public string ParamHash { get; }
            public string GeometryHash { get; }

            public bool UsageScanned { get; set; }
            public List<ElementId> Instances { get; } = new List<ElementId>();
            public List<ElementId> GroupInstances { get; } = new List<ElementId>();
            public List<ElementId> NestedInstances { get; } = new List<ElementId>();
            public List<Reference> References { get; } = new List<Reference>();
            public List<ElementTypeGroup> DefaultGroups { get; } = new List<ElementTypeGroup>();
            public ElementId DefaultForCategory { get; set; }
            public int Uses => Instances.Count + GroupInstances.Count + NestedInstances.Count;

            public int DependScore => References.Count + GroupInstances.Count + NestedInstances.Count +
                                      DefaultGroups.Count + (DefaultForCategory != null ? 1 : 0);
        }

        private class MergeItem
        {
            public MergeItem(Candidate keep, Candidate merge, string match)
            {
                Keep = keep;
                Merge = merge;
                Match = match;
            }

            public Candidate Keep { get; set; }
            public Candidate Merge { get; }
            public string Match { get; }
            public int Switched { get; set; }
            public int Repointed { get; set; }
            public int Remaining { get; set; }
            public bool Failed { get; set; }
            public bool Deleted { get; set; }
            public bool FamilyDeleted { get; set; }
            public List<string> Warnings { get; } = new List<string>();

            public void AddWarning(string warning)
            {
                if (Warnings.Count < MaxListedIds)
                    Warnings.Add(warning);
            }
        }

        private class Context
        {
            private readonly Dictionary<long, Candidate> _candidates = new Dictionary<long, Candidate>();
            private readonly Dictionary<long, (string Hash, string Summary, string Error)> _drawings = new Dictionary<long, (string, string, string)>();
            private readonly ISet<string> _ignore;
            private readonly int _maxFamilyOpens;

            public Context(Document doc, ISet<string> ignore, int maxFamilyOpens)
            {
                Doc = doc;
                _ignore = ignore;
                _maxFamilyOpens = maxFamilyOpens;
            }

            public Document Doc { get; }
            public int FamilyOpens { get; private set; }

            public Candidate Get(ElementType type)
            {
                var key = type.Id.GetValue();
                if (!_candidates.TryGetValue(key, out var candidate))
                    _candidates[key] = candidate = new Candidate(type, _ignore);
                return candidate;
            }

            /// <summary>Family drawing hash ("" for system types), or null with an error.</summary>
            public string Drawing(Candidate candidate, out string error)
            {
                error = null;
                if (candidate.Family == null)
                    return "";
                var key = candidate.Family.Id.GetValue();
                if (!_drawings.TryGetValue(key, out var drawing))
                {
                    if (FamilyOpens >= _maxFamilyOpens)
                    {
                        error = $"maxFamilyOpens ({_maxFamilyOpens}) reached; narrow the scope or raise the limit.";
                        return null;
                    }

                    FamilyOpens++;
                    var hash = FamilyFingerprint.FamilyDrawing(Doc, candidate.Family, out var summary, out var openError);
                    _drawings[key] = drawing = (hash, summary.ToString(), openError);
                }

                error = drawing.Error;
                return drawing.Hash;
            }

            public string DrawingSummary(Candidate candidate)
            {
                return candidate.Family != null && _drawings.TryGetValue(candidate.Family.Id.GetValue(), out var drawing)
                    ? drawing.Summary
                    : "";
            }

            /// <summary>
            ///     One pass over the model: instances per type (split into switchable, inside
            ///     groups, nested in other families), element-id parameters of types that point
            ///     at a candidate, and default types.
            /// </summary>
            public void ScanUsage(IEnumerable<Candidate> candidates)
            {
                var targets = candidates.Where(c => !c.UsageScanned).GroupBy(c => c.Id.GetValue()).ToDictionary(g => g.Key, g => g.First());
                if (targets.Count == 0)
                    return;
                foreach (var candidate in targets.Values)
                    candidate.UsageScanned = true;

                foreach (var element in new FilteredElementCollector(Doc).WhereElementIsNotElementType())
                {
                    ElementId typeId;
                    try
                    {
                        typeId = element.GetTypeId();
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (typeId == null || typeId == ElementId.InvalidElementId || !targets.TryGetValue(typeId.GetValue(), out var candidate))
                        continue;
                    if (element.GroupId != null && element.GroupId != ElementId.InvalidElementId)
                        candidate.GroupInstances.Add(element.Id);
                    else if (element is FamilyInstance instance && instance.SuperComponent != null)
                        candidate.NestedInstances.Add(element.Id);
                    else
                        candidate.Instances.Add(element.Id);
                }

                foreach (var type in new FilteredElementCollector(Doc).WhereElementIsElementType())
                {
                    foreach (Parameter parameter in type.Parameters)
                    {
                        if (parameter.StorageType != StorageType.ElementId)
                            continue;
                        var value = parameter.AsElementId();
                        if (value == null || value == type.Id || !targets.TryGetValue(value.GetValue(), out var candidate))
                            continue;
                        candidate.References.Add(new Reference { Owner = type.Id, ParameterId = parameter.Id, ReadOnly = parameter.IsReadOnly });
                    }
                }

                foreach (ElementTypeGroup group in Enum.GetValues(typeof(ElementTypeGroup)))
                {
                    try
                    {
                        var id = Doc.GetDefaultElementTypeId(group);
                        if (id != null && id != ElementId.InvalidElementId && targets.TryGetValue(id.GetValue(), out var candidate) && !candidate.DefaultGroups.Contains(group))
                            candidate.DefaultGroups.Add(group);
                    }
                    catch (Exception)
                    {
                        // Not every group has a default in every document.
                    }
                }

                foreach (var candidate in targets.Values.Where(c => c.Family?.FamilyCategory != null))
                {
                    try
                    {
                        var categoryId = candidate.Family.FamilyCategory.Id;
                        if (Doc.GetDefaultFamilyTypeId(categoryId) == candidate.Id)
                            candidate.DefaultForCategory = categoryId;
                    }
                    catch (Exception)
                    {
                        // Category without a default family type.
                    }
                }
            }
        }
    }
}
