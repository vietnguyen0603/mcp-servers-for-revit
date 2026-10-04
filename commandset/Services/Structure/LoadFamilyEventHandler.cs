using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Structure
{
    /// <summary>
    ///     Loads .rfa families by explicit path or by family name searched in the
    ///     Revit library folders (Options > File Locations plus the default
    ///     ProgramData library), or only searches (searchOnly). Returns the loaded
    ///     family with its types so they can be used or duplicated right away.
    /// </summary>
    public class LoadFamilyEventHandler : JsonParameterEventHandler
    {
        private const int MaxMatches = 30;

        public override string GetName() => "Load Family";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "families");
            var roots = LibraryRoots(doc, parameters);

            if (parameters.Value<bool?>("searchOnly") == true)
            {
                var found = items.Select((item, index) =>
                {
                    var key = item.Value<string>("name") ?? item.Value<string>("path") ?? "";
                    return (object)new { index, query = key, matches = Search(roots, key, item.Value<bool?>("exact") ?? false).Take(MaxMatches).ToList() };
                }).ToList();
                return Ok($"Searched {roots.Count} library folders.", new { roots, results = found });
            }

            var overwrite = parameters.Value<bool?>("overwrite") ?? false;
            var expanded = ExpandFolders(items);
            var results = DocumentationUtils.RunBatch(doc, "MCP: Load Families", expanded, item => Load(doc, (JObject)item, roots, overwrite));
            return Ok($"Loaded {results.Count(r => r.Value<bool>("success"))} of {results.Count} families.",
                DocumentationUtils.Summarize(results));
        }

        private static object Load(Document doc, JObject item, List<string> roots, bool overwrite)
        {
            var path = item.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
            {
                var name = item.Value<string>("name") ?? throw new ArgumentException("Give 'path' or 'name'.");
                var matches = Search(roots, name, true).ToList();
                if (matches.Count == 0)
                    matches = Search(roots, name, false).ToList();
                if (matches.Count == 0)
                    throw new ArgumentException($"No family file named '{name}' in: {string.Join("; ", roots)}.");
                var preferred = item.Value<string>("pathContains");
                if (!string.IsNullOrWhiteSpace(preferred))
                    matches = matches.Where(m => m.IndexOf(preferred, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (matches.Count != 1 && matches.Select(System.IO.Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                    throw new ArgumentException($"'{name}' is ambiguous; pass 'path' or 'pathContains'. Candidates: {string.Join(" | ", matches.Take(10))}.");
                path = matches[0];
            }

            if (!System.IO.File.Exists(path))
                throw new ArgumentException($"File not found: {path}");

            var familyName = System.IO.Path.GetFileNameWithoutExtension(path);
            var already = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));

            Family family;
            bool loaded;
            var typeNames = (item["types"] as JArray)?.Values<string>().Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            var loadedTypes = new List<string>();
            if (typeNames != null && typeNames.Count > 0)
            {
                // Selected types only (large type catalogs); adds missing types to an already loaded family.
                var missing = new List<string>();
                foreach (var typeName in typeNames)
                {
                    var exists = already != null && already.GetFamilySymbolIds().Any(id =>
                        string.Equals(doc.GetElement(id)?.Name, typeName, StringComparison.OrdinalIgnoreCase));
                    if (exists && !overwrite)
                        continue;
                    if (doc.LoadFamilySymbol(path, typeName, new OverwriteOptions(overwrite), out FamilySymbol symbol) || symbol != null)
                        loadedTypes.Add(typeName);
                    else if (!exists)
                        missing.Add(typeName);
                }
                if (missing.Count > 0 && loadedTypes.Count == 0)
                    throw new ArgumentException($"Types not found in '{familyName}': {string.Join(", ", missing)}. Check names with load_family searchOnly or load the whole family.");
                family = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                             .FirstOrDefault(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase))
                         ?? throw new InvalidOperationException($"Revit did not load '{path}'.");
                loaded = loadedTypes.Count > 0;
                if (missing.Count > 0)
                    loadedTypes.Add("not found: " + string.Join(", ", missing));
            }
            else if (already != null && !overwrite)
            {
                family = already;
                loaded = false;
            }
            else
            {
                loaded = doc.LoadFamily(path, new OverwriteOptions(overwrite), out family);
                family = family ?? new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                    .FirstOrDefault(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
                if (family == null)
                    throw new InvalidOperationException($"Revit did not load '{path}'.");
            }

            var types = family.GetFamilySymbolIds().Select(id => doc.GetElement(id)).Where(e => e != null)
                .Select(e => new { typeId = e.Id.GetValue(), name = e.Name }).OrderBy(t => t.name).ToList();
            return new
            {
                familyId = family.Id.GetValue(),
                familyName = family.Name,
                category = family.FamilyCategory?.Name,
                path,
                loaded,
                alreadyInProject = already != null,
                loadedTypes = loadedTypes.Count > 0 ? loadedTypes : null,
                typeCount = types.Count,
                types = types.Take(100)
            };
        }

        /// <summary>Replaces {folder, pattern?, recursive?} items with one {path, types?} item per matching .rfa file.</summary>
        private static JArray ExpandFolders(JArray items)
        {
            var expanded = new JArray();
            foreach (var token in items)
            {
                var folder = token.Value<string>("folder");
                if (string.IsNullOrWhiteSpace(folder))
                {
                    expanded.Add(token);
                    continue;
                }
                if (!System.IO.Directory.Exists(folder))
                    throw new ArgumentException($"Folder not found: {folder}");
                var pattern = token.Value<string>("pattern");
                if (string.IsNullOrWhiteSpace(pattern)) pattern = "*.rfa";
                else if (!pattern.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)) pattern += ".rfa";
                var option = token.Value<bool?>("recursive") == true ? System.IO.SearchOption.AllDirectories : System.IO.SearchOption.TopDirectoryOnly;
                var files = System.IO.Directory.GetFiles(folder, pattern, option)
                    .Where(f => !System.Text.RegularExpressions.Regex.IsMatch(f, @"\.\d{4}\.rfa$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    .OrderBy(f => f).ToList();
                if (files.Count == 0)
                    throw new ArgumentException($"No .rfa files matching '{pattern}' in {folder}.");
                foreach (var file in files)
                    expanded.Add(new JObject { ["path"] = file });
            }
            if (expanded.Count > 300)
                throw new ArgumentException($"{expanded.Count} families to load; narrow the folder pattern (max 300 per call).");
            return expanded;
        }

        private static List<string> LibraryRoots(Document doc, JObject parameters)
        {
            var roots = new List<string>();
            if (parameters["libraryRoots"] is JArray extra)
                roots.AddRange(extra.Values<string>());
            try
            {
                roots.AddRange(doc.Application.GetLibraryPaths().Values);
            }
            catch
            {
                // Library paths unavailable; fall back to the default folder below.
            }
            roots.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Autodesk", "RVT " + doc.Application.VersionNumber, "Libraries"));
            return roots.Where(r => !string.IsNullOrWhiteSpace(r) && System.IO.Directory.Exists(r))
                .Select(r => r.TrimEnd('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Recursive search for '&lt;name&gt;.rfa' (exact) or files whose name contains every word of the query.</summary>
        private static IEnumerable<string> Search(List<string> roots, string query, bool exact)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(query.Trim());
            var words = name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                IEnumerable<string> files;
                try
                {
                    files = System.IO.Directory.EnumerateFiles(root, "*.rfa", System.IO.SearchOption.AllDirectories);
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                {
                    var stem = System.IO.Path.GetFileNameWithoutExtension(file);
                    var hit = exact
                        ? string.Equals(stem, name, StringComparison.OrdinalIgnoreCase)
                        : words.All(w => stem.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (hit && seen.Add(file))
                        yield return file;
                }
            }
        }

        internal class OverwriteOptions : IFamilyLoadOptions
        {
            private readonly bool _overwrite;

            public OverwriteOptions(bool overwrite)
            {
                _overwrite = overwrite;
            }

            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = _overwrite;
                return _overwrite;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = _overwrite;
                return _overwrite;
            }
        }
    }
}
