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
            var results = DocumentationUtils.RunBatch(doc, "MCP: Load Families", items, item => Load(doc, (JObject)item, roots, overwrite));
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
            if (already != null && !overwrite)
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
                typeCount = types.Count,
                types = types.Take(100)
            };
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

        private class OverwriteOptions : IFamilyLoadOptions
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
