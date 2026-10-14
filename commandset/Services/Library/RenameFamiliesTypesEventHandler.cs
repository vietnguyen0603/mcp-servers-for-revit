using System.Text.RegularExpressions;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Library
{
    /// <summary>
    ///     Find/replace on the names of loadable families, their types and system types
    ///     (wall, floor, text, dimension, filled region, view, viewport ... types), and
    ///     optionally line patterns, fill patterns and materials. Used to convert a firm
    ///     template (e.g. TT -> AC). Renames that would collide with an existing name in
    ///     the same name scope are skipped and reported; chains (A->B while B->C) are
    ///     resolved by ordering. Names that can't be changed in place (shared parameters,
    ///     DWG/link names, line styles) are listed separately. dryRun (default) only lists.
    /// </summary>
    public class RenameFamiliesTypesEventHandler : JsonParameterEventHandler
    {
        private static readonly char[] InvalidNameChars = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };
        private static readonly char[] InvalidFamilyChars = { '/', '*', '"' };

        public override string GetName() => "Rename Families Types";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var find = parameters.Value<string>("find");
            if (string.IsNullOrEmpty(find))
                throw new ArgumentException("'find' is required.");
            var replace = parameters.Value<string>("replace") ?? "";
            var useRegex = parameters.Value<bool?>("regex") ?? false;
            var matchCase = parameters.Value<bool?>("matchCase") ?? true;
            var target = (parameters.Value<string>("target") ?? "both").Trim().ToLowerInvariant();
            if (target != "family" && target != "type" && target != "both")
                throw new ArgumentException("'target' must be family, type or both.");
            var categories = ReadStrings(parameters["categories"]);
            var familyPatterns = ReadStrings(parameters["familyNamePattern"]);
            var includeSystemTypes = parameters.Value<bool?>("includeSystemTypes") ?? true;
            var also = new HashSet<string>(ReadStrings(parameters["alsoRename"]), StringComparer.OrdinalIgnoreCase);
            var dryRun = parameters.Value<bool?>("dryRun") ?? true;
            var maxResults = parameters.Value<int?>("maxResults") ?? 1000;

            foreach (var name in categories)
                if (CategoryNameUtils.ResolveCategory(doc, name) == null)
                    throw new ArgumentException($"Unknown category '{name}'.");

            Regex regex;
            try
            {
                regex = new Regex(useRegex ? find : Regex.Escape(find),
                    matchCase ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Invalid regex '{find}': {ex.Message}");
            }

            var replacement = useRegex ? replace : replace.Replace("$", "$$");
            string NewName(string old) => regex.Replace(old, replacement).Trim();

            var all = CollectNamed(doc).ToList();
            var occupied = all.GroupBy(n => n.Scope)
                .ToDictionary(g => g.Key, g => g.GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase));

            var planned = new List<Named>();
            var invalid = new JArray();
            var cannotRename = new JArray();

            foreach (var item in all)
            {
                if (!InScope(item, target, includeSystemTypes, also, categories, familyPatterns))
                    continue;
                var newName = NewName(item.Name);
                if (newName == item.Name)
                    continue;

                if (item.CannotRenameReason != null)
                {
                    cannotRename.Add(Describe(item, newName, item.CannotRenameReason));
                    continue;
                }

                var reason = Validate(item, newName);
                if (reason != null)
                {
                    invalid.Add(Describe(item, newName, reason));
                    continue;
                }

                item.NewName = newName;
                planned.Add(item);
            }

            // Shared parameters are bound by GUID; renaming means editing the shared parameter file.
            foreach (var shared in new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)).Cast<SharedParameterElement>())
            {
                var name = shared.GetDefinition()?.Name ?? shared.Name;
                var newName = NewName(name);
                if (newName != name)
                    cannotRename.Add(new JObject
                    {
                        ["id"] = shared.Id.GetValue(),
                        ["kind"] = "sharedParameter",
                        ["name"] = name,
                        ["wouldBe"] = newName,
                        ["guid"] = shared.GuidValue.ToString(),
                        ["reason"] = "Shared parameter: the name comes from the shared parameter file (GUID-bound). Rename it there and re-load the families / re-bind the project parameter."
                    });
            }

            // Order the renames so a name freed by one rename can be taken by another.
            var ordered = new List<Named>();
            var pending = planned.ToList();
            bool progress;
            do
            {
                progress = false;
                foreach (var item in pending.ToList())
                {
                    var names = occupied[item.Scope];
                    if (names.TryGetValue(item.NewName, out var holder) && !ReferenceEquals(holder, item))
                        continue;
                    names.Remove(item.Name);
                    names[item.NewName] = item;
                    ordered.Add(item);
                    pending.Remove(item);
                    progress = true;
                }
            } while (progress && pending.Count > 0);

            var collisions = new JArray();
            foreach (var item in pending)
            {
                var entry = Describe(item, item.NewName, "Name already used in the same scope; skipped (consider merge_duplicate_families).");
                if (occupied[item.Scope].TryGetValue(item.NewName, out var holder))
                {
                    entry["conflictsWithId"] = holder.Id.GetValue();
                    entry["conflictsWithName"] = holder.NewName ?? holder.Name;
                }

                collisions.Add(entry);
            }

            List<JObject> results;
            if (dryRun)
            {
                results = ordered.Select(item => Describe(item, item.NewName, null)).ToList();
            }
            else
            {
                var items = new JArray(Enumerable.Range(0, ordered.Count));
                results = DocumentationUtils.RunBatch(doc, "MCP: Rename Families and Types", items, token =>
                {
                    var item = ordered[token.Value<int>()];
                    var element = doc.GetElement(item.Id);
                    try
                    {
                        element.Name = item.NewName;
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"{item.Kind} '{item.Name}' -> '{item.NewName}' (id {item.Id.GetValue()}): {ex.Message}");
                    }

                    if (element.Name != item.NewName)
                        throw new InvalidOperationException($"{item.Kind} '{item.Name}': Revit kept the name '{element.Name}'.");
                    return Describe(item, item.NewName, null);
                });
            }

            var succeeded = results.Count(r => r["success"] == null || r.Value<bool>("success"));
            var counts = new JObject
            {
                ["renamed"] = succeeded,
                ["failed"] = results.Count - succeeded,
                ["families"] = ordered.Count(i => i.Kind == "family"),
                ["familyTypes"] = ordered.Count(i => i.Kind == "familyType"),
                ["systemTypes"] = ordered.Count(i => i.Kind == "systemType"),
                ["styles"] = ordered.Count(i => i.Kind != "family" && i.Kind != "familyType" && i.Kind != "systemType"),
                ["collisions"] = collisions.Count,
                ["invalid"] = invalid.Count,
                ["cannotRenameInPlace"] = cannotRename.Count
            };
            var response = new JObject
            {
                ["dryRun"] = dryRun,
                ["counts"] = counts,
                ["renames"] = new JArray(results.Take(maxResults)),
                ["collisions"] = collisions,
                ["invalid"] = invalid,
                ["cannotRenameInPlace"] = cannotRename
            };
            if (results.Count > maxResults)
                response["truncated"] = $"Listed {maxResults} of {results.Count} renames (raise maxResults).";

            var message = dryRun
                ? $"Would rename {ordered.Count} names ({collisions.Count} collisions skipped, {cannotRename.Count} can't be renamed in place). Nothing changed; run with dryRun:false to apply."
                : $"Renamed {succeeded} of {ordered.Count} names ({collisions.Count} collisions skipped, {cannotRename.Count} can't be renamed in place).";
            return Ok(message, response);
        }

        private static bool InScope(Named item, string target, bool includeSystemTypes, HashSet<string> also,
            List<string> categories, List<string> familyPatterns)
        {
            switch (item.Kind)
            {
                case "family":
                    if (target == "type") return false;
                    break;
                case "familyType":
                    if (target == "family") return false;
                    break;
                case "systemType":
                case "cadImport":
                case "revitLink":
                    if (target == "family" || !includeSystemTypes) return false;
                    break;
                default:
                    // Styles: line patterns, fill patterns, materials, line styles, opted in by alsoRename.
                    return also.Contains(item.Option);
            }

            return (categories.Count == 0 || SourceDocumentUtils.InCategories(item.Category, categories))
                   && SourceDocumentUtils.LikeAny(item.FamilyName, familyPatterns);
        }

        private static string Validate(Named item, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName))
                return "The new name would be empty.";
            var bad = InvalidNameChars.Concat(item.Kind == "family" ? InvalidFamilyChars : new char[0])
                .Where(c => newName.IndexOf(c) >= 0 && item.Name.IndexOf(c) < 0).ToList();
            return bad.Count > 0 ? $"The new name contains characters Revit does not allow: {string.Join(" ", bad)}" : null;
        }

        private static JObject Describe(Named item, string newName, string reason)
        {
            var entry = new JObject
            {
                ["id"] = item.Id.GetValue(),
                ["kind"] = item.Kind,
                ["category"] = item.Category?.Name
            };
            if (item.Kind != "family" && !string.IsNullOrEmpty(item.FamilyName))
                entry["family"] = item.FamilyName;
            entry["oldName"] = item.Name;
            entry["newName"] = newName;
            if (reason != null)
                entry["reason"] = reason;
            return entry;
        }

        /// <summary>
        ///     Every renameable name in the document with its uniqueness scope:
        ///     families (document-wide), the types of one family, system types per
        ///     class + category, line/fill patterns, materials and line styles.
        /// </summary>
        private static IEnumerable<Named> CollectNamed(Document doc)
        {
            foreach (var family in new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>())
                yield return new Named(family, "family", "families", family.FamilyCategory, family.Name);

            foreach (var type in new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>())
            {
                string familyName;
                try
                {
                    familyName = type.FamilyName;
                }
                catch (Exception)
                {
                    familyName = "";
                }

                if (type is FamilySymbol symbol)
                {
                    yield return new Named(type, "familyType", "symbols:" + symbol.Family.Id.GetValue(), type.Category ?? symbol.Family.FamilyCategory, familyName);
                }
                else if (type is CADLinkType)
                {
                    yield return new Named(type, "cadImport", "cad", type.Category, familyName)
                    {
                        CannotRenameReason = "DWG/CAD import or link: the type name is the file name. Re-import/re-link from a renamed file, or delete the import."
                    };
                }
                else if (type is RevitLinkType)
                {
                    yield return new Named(type, "revitLink", "rvtlink", type.Category, familyName)
                    {
                        CannotRenameReason = "Revit link: the type name is the linked file name."
                    };
                }
                else
                {
                    var categoryId = type.Category?.Id.GetValue() ?? -1;
                    var scope = "system:" + type.GetType().Name + ":" + categoryId + (categoryId == -1 ? ":" + familyName : "");
                    yield return new Named(type, "systemType", scope, type.Category, familyName);
                }
            }

            foreach (var pattern in new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement)))
                yield return new Named(pattern, "linePattern", "linePatterns", null, "") { Option = "linePatterns" };
            foreach (var pattern in new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)))
                yield return new Named(pattern, "fillPattern", "fillPatterns", null, "") { Option = "fillPatterns" };
            foreach (var material in new FilteredElementCollector(doc).OfClass(typeof(Material)))
                yield return new Named(material, "material", "materials", null, "") { Option = "materials" };

            var lines = Category.GetCategory(doc, BuiltInCategory.OST_Lines);
            if (lines != null)
                foreach (Category sub in lines.SubCategories)
                {
                    var style = sub.GetGraphicsStyle(GraphicsStyleType.Projection);
                    if (style == null)
                        continue;
                    yield return new Named(style, "lineStyle", "lineStyles", lines, "") { Option = "lineStyles", Name = sub.Name, CannotRenameReason = "Line styles (subcategories) can't be renamed through the Revit API; rename them in Manage > Additional Settings > Line Styles." };
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

        private class Named
        {
            public Named(Element element, string kind, string scope, Category category, string familyName)
            {
                Id = element.Id;
                Kind = kind;
                Scope = scope;
                Category = category;
                FamilyName = familyName ?? "";
                Name = element.Name;
            }

            public ElementId Id { get; }
            public string Kind { get; }
            public string Scope { get; }
            public Category Category { get; }
            public string FamilyName { get; }
            public string Name { get; set; }
            public string NewName { get; set; }
            public string Option { get; set; }
            public string CannotRenameReason { get; set; }
        }
    }
}
