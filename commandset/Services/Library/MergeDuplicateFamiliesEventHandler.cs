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

            // Always every loadable family in the document (any category, any mode); candidateFamilies is the scope.
            var familiesBefore = new FilteredElementCollector(doc).OfClass(typeof(Family)).GetElementCount();
            var candidateFamilies = context.CandidateFamilies;

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
                ["candidateFamilies"] = candidateFamilies,
                ["familiesDeleted"] = familiesDeleted,
                ["typesDeleted"] = deletions.Where(d => !d.IsFamily && d.Done).Sum(d => d.Items.Count),
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
                ["countsNote"] = "familiesBefore/After = all loadable families in the document (every category, both modes); candidateFamilies = families whose types were considered (scope or pairs).",
                ["merges"] = new JArray(results.Take(maxResults)),
                ["deleted"] = new JArray(deletions.Take(maxResults).Select(d => DescribeDeletion(d, dryRun))),
                ["notMerged"] = new JArray(notMerged.Take(maxResults)),
                ["warnings"] = warnings,
                ["fingerprint"] = "type parameters (names/identity skipped) + FamilySymbol geometry + family drawing (EditFamily read-only: lines, regions, text/labels, forms, nested placement, imports, other); " +
                                  "no ids or names; compared order-independently in mm within 0.05 + 1e-5 relative"
            };
            if (results.Count > maxResults || notMerged.Count > maxResults || deletions.Count > maxResults)
                response["truncated"] = $"Lists capped at maxResults ({maxResults}).";

            var message = (dryRun ? "Dry run: would merge " : "Merged ") +
                          $"{typesMerged} types ({familiesDeleted} families removed, families in document {familiesBefore} -> {familiesBefore - familiesDeleted}; {candidateFamilies} candidate families), " +
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
                .GroupBy(c => c.CategoryId + "|" + c.ClassName + "|" + c.ParamHash)
                .Where(g => g.Count() > 1)
                .ToList();

            var clusters = new List<List<Candidate>>();
            foreach (var group in groups)
            {
                var members = group.ToList();
                // Same type parameters: split by type geometry + drawing, both compared within tolerance.
                var byGeometry = new List<List<Candidate>>();
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

                    var home = byGeometry.FirstOrDefault(l => SameGeometry(context, l[0], member));
                    if (home == null)
                        byGeometry.Add(home = new List<Candidate>());
                    home.Add(member);
                }

                if (byGeometry.Count > 1)
                    notMerged.Add(new JObject
                    {
                        ["types"] = new JArray(members.Select(Describe)),
                        ["reason"] = $"Same type parameters but the geometry differs ({byGeometry.Count} different drawings).",
                        ["drawings"] = new JArray(byGeometry.Select(l => new JObject
                        {
                            ["types"] = new JArray(l.Select(c => c.DisplayName)),
                            ["content"] = context.DrawingSummary(l[0]),
                            ["differencesFromFirst"] = l == byGeometry[0]
                                ? null
                                : new JArray(GeometryDifferences(context, byGeometry[0][0], l[0]).Take(10))
                        }))
                    });

                clusters.AddRange(byGeometry.Where(l => l.Count > 1));
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
                if (!FamilyFingerprint.SameDrawing(keepDrawing, mergeDrawing))
                {
                    entry["types"] = new JArray(Describe(keepTypes[0]), Describe(mergeTypes[0]));
                    entry["differences"] = new JArray(FamilyFingerprint.DrawingDifferences(keepDrawing, mergeDrawing).Take(25));
                    throw new ArgumentException($"The drawn geometry differs (keep: {context.DrawingSummary(keepTypes[0])}; merge: {context.DrawingSummary(mergeTypes[0])}); see 'differences'. Use force:true to merge anyway.");
                }
            }

            var items = new List<MergeItem>();
            var unmatched = new JArray();
            foreach (var merge in mergeTypes)
            {
                var sameName = keepTypes.FirstOrDefault(k => string.Equals(k.TypeName, merge.TypeName, StringComparison.OrdinalIgnoreCase));
                var target = (sameName != null && sameName.ParamHash == merge.ParamHash && SameTypeGeometry(sameName, merge) ? sameName : null)
                             ?? keepTypes.FirstOrDefault(k => k.ParamHash == merge.ParamHash && SameTypeGeometry(k, merge));
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
            reasons.AddRange(GeometryDifferences(context, keep, merge));
            return reasons;
        }

        private static bool SameTypeGeometry(Candidate a, Candidate b)
        {
            return a.GeometryHash == b.GeometryHash || !a.GeometryKnown || !b.GeometryKnown ||
                   FamilyFingerprint.SameTokens(a.GeometryTokens, b.GeometryTokens);
        }

        /// <summary>Type geometry and family drawing match within tolerance (drawing errors count as different).</summary>
        private static bool SameGeometry(Context context, Candidate a, Candidate b)
        {
            if (!SameTypeGeometry(a, b))
                return false;
            if (a.Family != null && b.Family != null && a.Family.Id == b.Family.Id)
                return true;
            return FamilyFingerprint.SameDrawing(context.Drawing(a, out _), context.Drawing(b, out _));
        }

        /// <summary>What differs in the type geometry and the family drawing, aspect by aspect.</summary>
        private static List<string> GeometryDifferences(Context context, Candidate keep, Candidate merge)
        {
            var reasons = new List<string>();
            if (!SameTypeGeometry(keep, merge))
                reasons.AddRange(FamilyFingerprint.TokenDifferences("type geometry", keep.GeometryTokens, merge.GeometryTokens));
            if (keep.Family != null && merge.Family != null && keep.Family.Id == merge.Family.Id)
                return reasons;
            var keepDrawing = context.Drawing(keep, out var keepError);
            var mergeDrawing = context.Drawing(merge, out var mergeError);
            if (keepDrawing == null || mergeDrawing == null)
                reasons.Add("drawing not compared: " + (keepError ?? mergeError));
            else if (!FamilyFingerprint.SameDrawing(keepDrawing, mergeDrawing))
            {
                reasons.Add($"drawn geometry differs (keep: {context.DrawingSummary(keep)}; merge: {context.DrawingSummary(merge)})");
                reasons.AddRange(FamilyFingerprint.DrawingDifferences(keepDrawing, mergeDrawing).Select(d => "drawing " + d));
            }

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
            public string Name;
            public List<string> TypeNames = new List<string>();
            public int AlsoDeleted;
            public List<string> Blockers = new List<string>();
        }

        /// <summary>
        ///     Deletes (or, in a dry run, checks) the fully merged types; the family is deleted
        ///     instead when all of its types are merged away (the last type of a family can't be
        ///     deleted alone). Dependents that belong to the family itself (types, subcategories,
        ///     internal category-less elements) go with it; a deletion that would take real model
        ///     content (an instance, a view, a tag/dimension in a view, a grouped element ...) is
        ///     skipped and the blockers are reported with class/category. The document is
        ///     regenerated first so switched instances no longer count as dependents, and a
        ///     deletion that still removes a switched instance is rolled back.
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

            var protectedIds = new HashSet<long>(plan.SelectMany(i => i.Merge.Instances.Concat(i.Keep.Instances)).Select(id => id.GetValue()));

            // The switched instances must leave the dependency graph before it is read.
            if (!dryRun)
            {
                try
                {
                    doc.Regenerate();
                }
                catch (Exception)
                {
                    // Deletion below still checks the dependents.
                }
            }

            foreach (var deletion in deletions)
            {
                var element = doc.GetElement(deletion.Id);
                deletion.Name = element is Family family ? family.Name
                    : deletion.Items.Count > 0 ? deletion.Items[0].Merge.DisplayName : element?.Name;
                deletion.TypeNames = deletion.Items.Select(i => i.Merge.TypeName).ToList();

                // In a dry run the instances are not switched yet; they would be by then.
                var allowed = dryRun
                    ? new HashSet<long>(deletion.Items.SelectMany(i => i.Merge.Instances).Select(id => id.GetValue()))
                    : new HashSet<long>();
                var dependents = Dependents(doc, deletion.Id, allowed, out var blockers);
                if (blockers.Count > 0)
                {
                    foreach (var item in deletion.Items)
                        item.AddWarning($"Not deleted: deleting {(deletion.IsFamily ? "family" : "type")} '{deletion.Name}' would also delete {blockers.Count} model element(s), e.g. {string.Join(", ", blockers.Take(5))}.");
                    deletion.Blockers = blockers;
                    continue;
                }

                if (dryRun)
                {
                    deletion.Done = true;
                    deletion.AlsoDeleted = dependents;
                }
                else
                {
                    var sub = new SubTransaction(doc);
                    sub.Start();
                    try
                    {
                        var deleted = doc.Delete(deletion.Id) ?? new List<ElementId>();
                        // Safety net: never take a switched (or kept) instance with the duplicate.
                        var lost = deleted.Where(d => protectedIds.Contains(d.GetValue())).Select(d => d.GetValue()).ToList();
                        if (lost.Count > 0)
                        {
                            sub.RollBack();
                            deletion.Blockers = lost.Select(d => d + " (switched instance)").ToList();
                            foreach (var item in deletion.Items)
                                item.AddWarning($"Not deleted: deleting '{deletion.Name}' would also delete {lost.Count} switched instance(s), e.g. {string.Join(", ", lost.Take(5))}.");
                            continue;
                        }

                        sub.Commit();
                        deletion.Done = true;
                        deletion.AlsoDeleted = deleted.Count(id => id != deletion.Id);
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

        /// <summary>
        ///     Elements that would be deleted with <paramref name="id" />. Returns how many there are and, in
        ///     <paramref name="blockers" />, the ones that are real model content ("id (Class/Category)"):
        ///     family instances, views, view-specific elements (tags, dimensions, detail items), grouped
        ///     elements and any other element with a model/annotation/analytical category. Types,
        ///     subcategories, families and internal category-less elements owned by the family are fine.
        /// </summary>
        private static int Dependents(Document doc, ElementId id, HashSet<long> allowed, out List<string> blockers)
        {
            blockers = new List<string>();
            var element = doc.GetElement(id);
            if (element == null)
                return 0;
            var count = 0;
            foreach (var dep in element.GetDependentElements(null))
            {
                if (dep == id)
                    continue;
                var dependent = doc.GetElement(dep);
                if (dependent == null)
                    continue;
                count++;
                if (allowed.Contains(dep.GetValue()) || !IsModelContent(dependent))
                    continue;
                blockers.Add($"{dep.GetValue()} ({dependent.GetType().Name}/{dependent.Category?.Name ?? "no category"})");
            }

            return count;
        }

        private static bool IsModelContent(Element element)
        {
            if (element is ElementType || element is GraphicsStyle || element is Family || element is Material)
                return false;
            if (element is FamilyInstance || element is View)
                return true;
            if (element.OwnerViewId != null && element.OwnerViewId != ElementId.InvalidElementId)
                return true;
            if (element.GroupId != null && element.GroupId != ElementId.InvalidElementId)
                return true;
            var category = element.Category;
            if (category == null)
                return false;
            return category.CategoryType == CategoryType.Model || category.CategoryType == CategoryType.Annotation ||
                   category.CategoryType == CategoryType.AnalyticalModel;
        }

        private static JObject DescribeDeletion(Deletion deletion, bool dryRun)
        {
            var entry = new JObject
            {
                ["kind"] = deletion.IsFamily ? "family" : "type",
                ["id"] = deletion.Id.GetValue(),
                ["name"] = deletion.Name,
                ["status"] = deletion.Done ? (dryRun ? "wouldDelete" : "deleted") : "kept"
            };
            if (deletion.IsFamily)
                entry["mergedTypes"] = new JArray(deletion.TypeNames);
            if (deletion.Done)
                entry["alsoDeleted"] = deletion.AlsoDeleted;
            if (deletion.Blockers.Count > 0)
                entry["blockedBy"] = new JArray(deletion.Blockers.Take(MaxListedIds));
            return entry;
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
                GeometryTokens = FamilyFingerprint.TypeGeometry(type);
                GeometryHash = GeometryTokens.Count == 0 ? "" : FamilyFingerprint.Hash(GeometryTokens);
                // An inactive symbol (never placed) may return no geometry; then only the drawing decides.
                GeometryKnown = GeometryTokens.Count > 0 || !(type is FamilySymbol fs && !fs.IsActive);
            }

            public bool GeometryKnown { get; }

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
            public List<string> GeometryTokens { get; }

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
            private readonly Dictionary<long, (FamilyFingerprint.Drawing Drawing, string Error)> _drawings = new Dictionary<long, (FamilyFingerprint.Drawing, string)>();
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

            /// <summary>Family drawing (empty for system types), or null with an error.</summary>
            public FamilyFingerprint.Drawing Drawing(Candidate candidate, out string error)
            {
                error = null;
                if (candidate.Family == null)
                    return FamilyFingerprint.Drawing.Empty;
                var key = candidate.Family.Id.GetValue();
                if (!_drawings.TryGetValue(key, out var drawing))
                {
                    if (FamilyOpens >= _maxFamilyOpens)
                    {
                        error = $"maxFamilyOpens ({_maxFamilyOpens}) reached; narrow the scope or raise the limit.";
                        return null;
                    }

                    FamilyOpens++;
                    var result = FamilyFingerprint.FamilyDrawing(Doc, candidate.Family, out var openError);
                    _drawings[key] = drawing = (result, openError);
                }

                error = drawing.Error;
                return drawing.Drawing;
            }

            public string DrawingSummary(Candidate candidate)
            {
                return candidate.Family != null && _drawings.TryGetValue(candidate.Family.Id.GetValue(), out var drawing) && drawing.Drawing != null
                    ? drawing.Drawing.Summary.ToString()
                    : "";
            }

            /// <summary>Distinct loadable families among the types considered (scope or pair families).</summary>
            public int CandidateFamilies => _candidates.Values.Where(c => c.Family != null).Select(c => c.Family.Id.GetValue()).Distinct().Count();

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
