using System.Text.RegularExpressions;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Structure
{
    /// <summary>
    ///     Copies loadable families (with all their types) and system types (floor, wall, ... types)
    ///     from another project into the active one. The source is an open document (by title) or a
    ///     .rvt/.rte path opened in the background (detached, worksets closed) and closed unsaved, so
    ///     the source file is never modified. Families can also be saved as .rfa files.
    /// </summary>
    public class CopyFamiliesEventHandler : JsonParameterEventHandler
    {
        private const int MaxListed = 500;

        public override string GetName() => "Copy Families";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var target = uiDoc.Document;
            var source = OpenSource(target, parameters, out var openedHere);
            try
            {
                if (source.Equals(target))
                    throw new ArgumentException("The source is the active document; give another document.");

                var categories = (parameters["categories"] as JArray)?.Values<string>().ToList() ?? new List<string>();
                if (parameters.Value<bool?>("listOnly") == true)
                    return Ok($"Listed families of '{source.Title}'.", List(source, categories));

                var unmatched = new List<string>();
                var families = SelectFamilies(source, parameters["families"] as JArray, categories, unmatched);
                var systemTypes = parameters["systemTypes"] as JArray;
                if (families.Count == 0 && unmatched.Count == 0 && (systemTypes == null || systemTypes.Count == 0))
                    throw new ArgumentException("Nothing to copy: give families, categories or systemTypes (use listOnly to see what the source has).");

                var overwrite = parameters.Value<bool?>("overwrite") ?? false;
                var folder = parameters.Value<string>("saveToFolder");
                if (!string.IsNullOrWhiteSpace(folder))
                    System.IO.Directory.CreateDirectory(folder);

                var results = new List<JObject>();
                // A wrong name must not waste the (slow) source open: report it and copy the rest.
                foreach (var name in unmatched)
                    results.Add(Failure(results.Count, $"No family matching '{name}' in '{source.Title}' (use listOnly)."));
                foreach (var family in families)
                    results.Add(Isolate(results.Count, () => CopyFamily(source, target, family, overwrite, folder)));
                if (systemTypes != null && systemTypes.Count > 0)
                    results.AddRange(CopySystemTypes(source, target, systemTypes, results.Count));

                return Ok($"Copied {results.Count(r => r.Value<bool>("success"))} of {results.Count} items from '{source.Title}'.",
                    new { source = source.Title, closedAfterCopy = openedHere, succeeded = results.Count(r => r.Value<bool>("success")), failed = results.Count(r => !r.Value<bool>("success")), results });
            }
            finally
            {
                if (openedHere)
                    source.Close(false);
            }
        }

        private static Document OpenSource(Document target, JObject parameters, out bool openedHere)
        {
            openedHere = false;
            var app = target.Application;
            var title = parameters.Value<string>("sourceDocument");
            var path = parameters.Value<string>("sourcePath");
            if (!string.IsNullOrWhiteSpace(title))
            {
                var docs = app.Documents.Cast<Document>().Where(d => !d.IsFamilyDocument).ToList();
                return docs.FirstOrDefault(d => string.Equals(d.Title, title.Trim(), StringComparison.OrdinalIgnoreCase))
                       ?? docs.FirstOrDefault(d => d.Title.IndexOf(title.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                       ?? throw new ArgumentException($"No open project titled '{title}'. Open: {string.Join(", ", docs.Select(d => d.Title))}.");
            }

            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Give sourceDocument (title of an open project) or sourcePath (.rvt/.rte).");
            if (!System.IO.File.Exists(path))
                throw new ArgumentException($"File not found: {path}");

            var open = app.Documents.Cast<Document>().FirstOrDefault(d =>
                !string.IsNullOrEmpty(d.PathName) && string.Equals(System.IO.Path.GetFullPath(d.PathName), System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
            if (open != null)
                return open;

            var options = new OpenOptions { Audit = false };
            if (BasicFileInfo.Extract(path).IsWorkshared)
            {
                options.DetachFromCentralOption = DetachFromCentralOption.DetachAndDiscardWorksets;
                options.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets));
            }
            var doc = app.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(path), options)
                      ?? throw new InvalidOperationException($"Revit could not open {path}.");
            openedHere = true;
            return doc;
        }

        private static object List(Document source, List<string> categories)
        {
            var counts = new FilteredElementCollector(source).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .GroupBy(i => i.Symbol.Family.Id.GetValue()).ToDictionary(g => g.Key, g => g.Count());
            var families = new FilteredElementCollector(source).OfClass(typeof(Family)).Cast<Family>()
                .Where(f => InCategories(f.FamilyCategory, categories))
                .OrderBy(f => f.FamilyCategory?.Name).ThenBy(f => f.Name)
                .Select(f => new
                {
                    name = f.Name,
                    category = f.FamilyCategory?.Name,
                    typeCount = f.GetFamilySymbolIds().Count,
                    instances = counts.TryGetValue(f.Id.GetValue(), out var n) ? n : 0,
                    inPlace = f.IsInPlace
                }).ToList();
            var systemTypes = new FilteredElementCollector(source).WhereElementIsElementType()
                .Where(t => !(t is FamilySymbol) && t.Category != null && t.Category.CategoryType == CategoryType.Model && InCategories(t.Category, categories))
                .GroupBy(t => t.Category.Name).OrderBy(g => g.Key)
                .Select(g => new { category = g.Key, types = g.Select(t => t.Name).OrderBy(n => n).ToList() }).ToList();
            return new { source = source.Title, familyCount = families.Count, families = families.Take(MaxListed), systemTypes };
        }

        private static List<Family> SelectFamilies(Document source, JArray requested, List<string> categories, List<string> unmatched)
        {
            var all = new FilteredElementCollector(source).OfClass(typeof(Family)).Cast<Family>().ToList();
            var picked = new List<Family>();
            if (requested != null)
            {
                foreach (var token in requested)
                {
                    var name = token.Type == JTokenType.String ? token.ToString() : token.Value<string>("name");
                    var category = token.Type == JTokenType.Object ? token.Value<string>("category") : null;
                    if (string.IsNullOrWhiteSpace(name))
                        throw new ArgumentException("Each families item needs a name (wildcards * and ? allowed).");
                    var matches = all.Where(f => Like(f.Name, name) && InCategories(f.FamilyCategory, category == null ? null : new List<string> { category })).ToList();
                    if (matches.Count == 0)
                        unmatched.Add(name);
                    picked.AddRange(matches);
                }
            }
            else if (categories.Count > 0)
            {
                picked.AddRange(all.Where(f => InCategories(f.FamilyCategory, categories)));
            }
            return picked.GroupBy(f => f.Id).Select(g => g.First()).ToList();
        }

        private static object CopyFamily(Document source, Document target, Family family, bool overwrite, string folder)
        {
            if (family.IsInPlace)
                throw new ArgumentException($"'{family.Name}' is an in-place family and cannot be copied.");
            if (!family.IsEditable)
                throw new ArgumentException($"'{family.Name}' is not editable (system or locked family).");

            var existing = FindFamily(target, family.Name);
            string savedPath = null;
            var familyDoc = source.EditFamily(family);
            try
            {
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    savedPath = System.IO.Path.Combine(folder, SafeFileName(family.Name) + ".rfa");
                    familyDoc.SaveAs(savedPath, new SaveAsOptions { OverwriteExistingFile = true });
                }

                if (existing == null || overwrite)
                    familyDoc.LoadFamily(target, new LoadFamilyEventHandler.OverwriteOptions(overwrite));
            }
            finally
            {
                familyDoc.Close(false);
            }

            var loaded = FindFamily(target, family.Name)
                         ?? throw new InvalidOperationException($"Revit did not load '{family.Name}'.");
            var types = loaded.GetFamilySymbolIds().Select(id => target.GetElement(id)).Where(e => e != null)
                .Select(e => new { typeId = e.Id.GetValue(), name = e.Name }).OrderBy(t => t.name).ToList();
            return new
            {
                kind = "family",
                familyName = loaded.Name,
                familyId = loaded.Id.GetValue(),
                category = loaded.FamilyCategory?.Name,
                loaded = existing == null || overwrite,
                alreadyInProject = existing != null,
                savedPath,
                typeCount = types.Count,
                types = types.Take(50)
            };
        }

        private static List<JObject> CopySystemTypes(Document source, Document target, JArray requested, int firstIndex)
        {
            var results = new List<JObject>();
            var sourceTypes = new FilteredElementCollector(source).WhereElementIsElementType()
                .Where(t => !(t is FamilySymbol) && t.Category != null).ToList();
            var toCopy = new List<(int index, ElementType type)>();

            for (var i = 0; i < requested.Count; i++)
            {
                var index = firstIndex + i;
                var item = requested[i] as JObject;
                var name = item?.Value<string>("name");
                var category = item?.Value<string>("category");
                if (string.IsNullOrWhiteSpace(name))
                {
                    results.Add(Failure(index, "systemTypes items need a name (wildcards allowed) and optionally a category."));
                    continue;
                }
                var matches = sourceTypes.OfType<ElementType>()
                    .Where(t => Like(t.Name, name) && InCategories(t.Category, category == null ? null : new List<string> { category })).ToList();
                if (matches.Count == 0)
                {
                    results.Add(Failure(index, $"No system type matching '{name}'{(category == null ? "" : " in " + category)} in '{source.Title}'."));
                    continue;
                }
                toCopy.AddRange(matches.Select(m => (index, m)));
            }

            var distinct = toCopy.GroupBy(c => c.type.Id).Select(g => g.First()).ToList();
            var skipped = distinct.Where(c => TypeExists(target, c.type)).ToList();
            var copyNow = distinct.Except(skipped).ToList();

            var copiedIds = new Dictionary<ElementId, ElementId>();
            if (copyNow.Count > 0)
            {
                using (var transaction = DocumentationUtils.StartTransaction(target, "MCP: Copy System Types"))
                {
                    var options = new CopyPasteOptions();
                    options.SetDuplicateTypeNamesHandler(new UseDestinationTypes());
                    foreach (var (_, type) in copyNow)
                    {
                        var ids = ElementTransformUtils.CopyElements(source, new List<ElementId> { type.Id }, target, Transform.Identity, options);
                        if (ids.Count > 0)
                            copiedIds[type.Id] = ids.First();
                    }
                    transaction.Commit();
                }
            }

            foreach (var group in distinct.GroupBy(c => c.index))
            {
                results.Add(new JObject
                {
                    ["index"] = group.Key,
                    ["success"] = true,
                    ["kind"] = "systemTypes",
                    ["types"] = new JArray(group.Select(c => new JObject
                    {
                        ["name"] = c.type.Name,
                        ["category"] = c.type.Category.Name,
                        ["typeId"] = copiedIds.TryGetValue(c.type.Id, out var id) ? id.GetValue() : (long?)null,
                        ["copied"] = copiedIds.ContainsKey(c.type.Id),
                        ["alreadyInProject"] = skipped.Contains(c)
                    }))
                });
            }
            return results.OrderBy(r => r.Value<int>("index")).ToList();
        }

        private static bool TypeExists(Document target, ElementType type)
        {
            return new FilteredElementCollector(target).OfClass(type.GetType()).WhereElementIsElementType()
                .Any(t => t.Category?.Id.GetValue() == type.Category.Id.GetValue() && string.Equals(t.Name, type.Name, StringComparison.OrdinalIgnoreCase));
        }

        private static Family FindFamily(Document doc, string name)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool InCategories(Category category, List<string> categories)
        {
            if (categories == null || categories.Count == 0)
                return true;
            if (category == null)
                return false;
            return categories.Any(c =>
            {
                var bic = c.Trim().StartsWith("OST_", StringComparison.OrdinalIgnoreCase) ? c.Trim() : "OST_" + c.Trim().Replace(" ", "");
                return (Enum.TryParse(bic, true, out BuiltInCategory parsed) && category.Id.GetValue() == (long)parsed)
                       || string.Equals(category.Name, c.Trim(), StringComparison.OrdinalIgnoreCase);
            });
        }

        private static bool Like(string value, string pattern)
        {
            if (pattern.IndexOfAny(new[] { '*', '?' }) < 0)
                return string.Equals(value, pattern.Trim(), StringComparison.OrdinalIgnoreCase);
            var regex = "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
        }

        private static string SafeFileName(string name)
        {
            return System.IO.Path.GetInvalidFileNameChars().Aggregate(name, (current, c) => current.Replace(c, '_'));
        }

        private static JObject Isolate(int index, Func<object> action)
        {
            try
            {
                var result = JObject.FromObject(action());
                result.AddFirst(new JProperty("success", true));
                result.AddFirst(new JProperty("index", index));
                return result;
            }
            catch (Exception ex)
            {
                return Failure(index, ex.Message);
            }
        }

        private static JObject Failure(int index, string message)
        {
            return new JObject { ["index"] = index, ["success"] = false, ["message"] = message };
        }

        private class UseDestinationTypes : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
            {
                return DuplicateTypeAction.UseDestinationTypes;
            }
        }
    }
}
