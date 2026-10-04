using System.Text.RegularExpressions;
using RevitMCPCommandSet.Services.Structure;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    ///     Shared machinery for tools that read from another project: resolving an open document by
    ///     title or opening a .rvt/.rte in the background (detached, worksets closed) so it can be
    ///     closed unsaved afterwards, wildcard/category matching, and copying a loadable family
    ///     through the family editor. Used by copy_families, copy_project_standards and
    ///     export_project_style.
    /// </summary>
    internal static class SourceDocumentUtils
    {
        /// <summary>
        ///     Resolves the source project. <paramref name="title" /> picks an open project (exact,
        ///     then partial title match); <paramref name="path" /> opens the file in the background
        ///     unless it is already open. <paramref name="openedHere" /> tells the caller to
        ///     <c>Close(false)</c> the document when done. With neither given and
        ///     <paramref name="allowActive" />, the active document is returned.
        /// </summary>
        public static Document Open(Document active, string title, string path, out bool openedHere, bool allowActive = false)
        {
            openedHere = false;
            var app = active.Application;
            if (!string.IsNullOrWhiteSpace(title))
            {
                var docs = app.Documents.Cast<Document>().Where(d => !d.IsFamilyDocument && !d.IsLinked).ToList();
                var wanted = StripExtension(title.Trim());
                return docs.FirstOrDefault(d => string.Equals(StripExtension(d.Title), wanted, StringComparison.OrdinalIgnoreCase))
                       ?? docs.FirstOrDefault(d => d.Title.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                       ?? throw new ArgumentException($"No open project titled '{title}'. Open: {string.Join(", ", docs.Select(d => d.Title))}.");
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                if (allowActive)
                    return active;
                throw new ArgumentException("Give sourceDocument (title of an open project) or sourcePath (.rvt/.rte).");
            }
            if (!System.IO.File.Exists(path))
                throw new ArgumentException($"File not found: {path}");

            var fullPath = System.IO.Path.GetFullPath(path);
            var open = app.Documents.Cast<Document>().FirstOrDefault(d =>
                !string.IsNullOrEmpty(d.PathName) && string.Equals(System.IO.Path.GetFullPath(d.PathName), fullPath, StringComparison.OrdinalIgnoreCase));
            if (open != null)
                return open;

            var options = new OpenOptions { Audit = false };
            if (BasicFileInfo.Extract(fullPath).IsWorkshared)
            {
                options.DetachFromCentralOption = DetachFromCentralOption.DetachAndDiscardWorksets;
                options.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets));
            }
            var doc = app.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(fullPath), options)
                      ?? throw new InvalidOperationException($"Revit could not open {path}.");
            openedHere = true;
            return doc;
        }

        private static string StripExtension(string name)
        {
            return name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rte", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 4)
                : name;
        }

        /// <summary>Case-insensitive name match; * and ? are wildcards, otherwise exact.</summary>
        public static bool Like(string value, string pattern)
        {
            if (value == null || pattern == null)
                return false;
            if (pattern.IndexOfAny(new[] { '*', '?' }) < 0)
                return string.Equals(value, pattern.Trim(), StringComparison.OrdinalIgnoreCase);
            var regex = "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
        }

        /// <summary>True when no patterns are given or the value matches any of them.</summary>
        public static bool LikeAny(string value, IList<string> patterns)
        {
            return patterns == null || patterns.Count == 0 || patterns.Any(p => Like(value, p));
        }

        /// <summary>
        ///     True when <paramref name="categories" /> is empty or contains the category by
        ///     BuiltInCategory name (with or without the OST_ prefix) or display name.
        /// </summary>
        public static bool InCategories(Category category, IList<string> categories)
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

        public static Family FindFamily(Document doc, string name)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public static string SafeFileName(string name)
        {
            return System.IO.Path.GetInvalidFileNameChars().Aggregate(name, (current, c) => current.Replace(c, '_'));
        }

        /// <summary>Outcome of <see cref="CopyFamily" />.</summary>
        public class FamilyCopyResult
        {
            public Family Family;
            public bool Loaded;
            public bool AlreadyInProject;
            public string SavedPath;
        }

        /// <summary>
        ///     Copies a loadable family with all its types through the family editor
        ///     (EditFamily + LoadFamily), so parameters and formulas are kept. An existing family is
        ///     kept unless <paramref name="overwrite" />. Must run outside any transaction on
        ///     <paramref name="target" />. The source document is not modified.
        /// </summary>
        public static FamilyCopyResult CopyFamily(Document source, Document target, Family family, bool overwrite, string saveToFolder)
        {
            if (family.IsInPlace)
                throw new ArgumentException($"'{family.Name}' is an in-place family and cannot be copied.");
            if (!family.IsEditable)
                throw new ArgumentException($"'{family.Name}' is not editable (system or locked family).");

            var existing = FindFamily(target, family.Name);
            var result = new FamilyCopyResult { AlreadyInProject = existing != null };
            if (existing != null && !overwrite && string.IsNullOrWhiteSpace(saveToFolder))
            {
                result.Family = existing;
                return result;
            }

            var familyDoc = source.EditFamily(family);
            try
            {
                if (!string.IsNullOrWhiteSpace(saveToFolder))
                {
                    result.SavedPath = System.IO.Path.Combine(saveToFolder, SafeFileName(family.Name) + ".rfa");
                    familyDoc.SaveAs(result.SavedPath, new SaveAsOptions { OverwriteExistingFile = true });
                }

                if (existing == null || overwrite)
                {
                    familyDoc.LoadFamily(target, new LoadFamilyEventHandler.OverwriteOptions(overwrite));
                    result.Loaded = true;
                }
            }
            finally
            {
                familyDoc.Close(false);
            }

            result.Family = FindFamily(target, family.Name)
                            ?? throw new InvalidOperationException($"Revit did not load '{family.Name}'.");
            return result;
        }

        /// <summary>Always keeps the destination's type when a copied element brings a type with an existing name.</summary>
        public class UseDestinationTypes : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
            {
                return DuplicateTypeAction.UseDestinationTypes;
            }
        }

        /// <summary>Cancels the copy when it would bring a type whose name already exists in the destination.</summary>
        public class AbortOnDuplicateTypes : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
            {
                return DuplicateTypeAction.Abort;
            }
        }
    }
}
