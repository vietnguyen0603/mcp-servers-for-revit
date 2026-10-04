using System.Text;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    ///     Forgiving category / subcategory lookup shared by the graphics
    ///     standards and view template tools. Revit names some built-in
    ///     subcategories with angle brackets ("&lt;Hidden Lines&gt;",
    ///     "&lt;Overhead&gt;"), so names are compared ignoring surrounding
    ///     &lt;&gt;, case and repeated / surrounding whitespace.
    /// </summary>
    public static class CategoryNameUtils
    {
        /// <summary>"  &lt;Hidden   Lines&gt; " becomes "hidden lines".</summary>
        public static string Normalize(string name)
        {
            var text = (name ?? "").Trim();
            while (text.Length >= 2 && text[0] == '<' && text[text.Length - 1] == '>')
                text = text.Substring(1, text.Length - 2).Trim();
            text = text.TrimStart('<').TrimEnd('>').Trim();

            var builder = new StringBuilder(text.Length);
            var space = false;
            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    space = builder.Length > 0;
                    continue;
                }

                if (space)
                    builder.Append(' ');
                space = false;
                builder.Append(char.ToLowerInvariant(c));
            }

            return builder.ToString();
        }

        public static bool NamesEqual(string a, string b) => Normalize(a) == Normalize(b);

        /// <summary>
        ///     Resolves a top-level category by BuiltInCategory name ("OST_Walls"),
        ///     display name, or a display name differing only in case, spaces or &lt;&gt;.
        /// </summary>
        public static Category ResolveCategory(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            var exact = DocumentationUtils.ResolveCategory(doc, name);
            if (exact != null)
                return exact;

            var key = Normalize(name);
            if (key.Length == 0)
                return null;
            foreach (Category category in doc.Settings.Categories)
                if (Normalize(category.Name) == key)
                    return category;
            return null;
        }

        /// <summary>
        ///     Resolves "Category" or "Category/Subcategory" (e.g.
        ///     "Structural Framing/Hidden Lines" finds "&lt;Hidden Lines&gt;").
        ///     The full text is tried first because a few category names contain '/'.
        /// </summary>
        public static Category ResolveCategoryPath(Document doc, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            var whole = ResolveCategory(doc, path);
            if (whole != null)
                return whole;

            for (var slash = path.IndexOf('/'); slash > 0 && slash < path.Length - 1; slash = path.IndexOf('/', slash + 1))
            {
                var parent = ResolveCategory(doc, path.Substring(0, slash));
                if (parent == null)
                    continue;
                var subKey = Normalize(path.Substring(slash + 1));
                foreach (Category sub in parent.SubCategories)
                    if (Normalize(sub.Name) == subKey)
                        return sub;
            }

            return null;
        }

        /// <summary>"Parent/Sub" for a subcategory, else the category name.</summary>
        public static string PathOf(Category category) =>
            category.Parent != null ? $"{category.Parent.Name}/{category.Name}" : category.Name;

        /// <summary>Model and annotation categories plus their subcategories as "Category" / "Category/Sub" paths.</summary>
        public static List<string> AllCategoryPaths(Document doc)
        {
            var paths = new List<string>();
            foreach (Category category in doc.Settings.Categories)
            {
                if (category.CategoryType != CategoryType.Model && category.CategoryType != CategoryType.Annotation)
                    continue;
                paths.Add(category.Name);
                foreach (Category sub in category.SubCategories)
                    paths.Add($"{category.Name}/{sub.Name}");
            }

            return paths;
        }

        /// <summary>
        ///     Closest candidates for an unknown category path. When the parent
        ///     category resolves, only its subcategories are ranked.
        /// </summary>
        public static string SuggestCategory(Document doc, string path, IEnumerable<string> allPaths = null)
        {
            var candidates = allPaths ?? AllCategoryPaths(doc);
            var slash = path?.IndexOf('/') ?? -1;
            if (slash > 0)
            {
                var parent = ResolveCategory(doc, path.Substring(0, slash));
                if (parent != null)
                {
                    var subs = parent.SubCategories.Cast<Category>().Select(s => $"{parent.Name}/{s.Name}").ToList();
                    var subSuggestion = Suggest(path, subs);
                    if (subSuggestion.Length > 0)
                        return subSuggestion;
                    if (subs.Count > 0 && subs.Count <= 30)
                        return $" Subcategories of '{parent.Name}': {string.Join(", ", subs.Select(s => s.Substring(parent.Name.Length + 1)).OrderBy(s => s, StringComparer.OrdinalIgnoreCase))}.";
                }
            }

            return Suggest(path, candidates);
        }

        /// <summary>" Did you mean: a, b, c?" for the closest candidates, or "".</summary>
        public static string Suggest(string query, IEnumerable<string> candidates)
        {
            var key = Key(query);
            if (key.Length == 0)
                return "";
            var ranked = candidates.Where(c => !string.IsNullOrEmpty(c)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(c =>
                {
                    var ck = Key(c);
                    var score = ck == key ? 0
                        : ck.Contains(key) || key.Contains(ck) ? 1 + Math.Abs(ck.Length - key.Length) / 10.0
                        : 3 + Levenshtein(ck, key) / (double)Math.Max(1, key.Length);
                    return (name: c, score);
                })
                .Where(x => x.score < 4)
                .OrderBy(x => x.score).ThenBy(x => x.name.Length)
                .Take(5)
                .Select(x => x.name)
                .ToList();
            return ranked.Count == 0 ? "" : $" Did you mean: {string.Join(", ", ranked)}?";
        }

        private static string Key(string text) =>
            new string((text ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        private static int Levenshtein(string a, string b)
        {
            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++) previous[j] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                        previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var swap = previous;
                previous = current;
                current = swap;
            }

            return previous[b.Length];
        }
    }
}
