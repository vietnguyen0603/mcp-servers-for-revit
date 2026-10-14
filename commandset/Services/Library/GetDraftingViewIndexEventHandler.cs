using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Library
{
    /// <summary>
    ///     Returns a searchable index of drafting views (or the views in scope),
    ///     one page at a time: view name, scale, sheet/detail number and title
    ///     on sheet, the text of every text note, string parameter values of
    ///     generic annotations (sub-detail titles), and the family : type names
    ///     of detail items and generic annotations with their counts. `limit: 0`
    ///     returns the header (document title/path, view count) only.
    ///     Read-only; no transaction.
    /// </summary>
    public class GetDraftingViewIndexEventHandler : JsonParameterEventHandler
    {
        private static readonly HashSet<string> SkippedSymbolParameters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Comments", "Mark" };

        public override string GetName() => "Get Drafting View Index";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var offset = Math.Max(0, parameters.Value<int?>("offset") ?? 0);
            var limit = Math.Max(0, Math.Min(parameters.Value<int?>("limit") ?? 200, 1000));
            var includeText = parameters.Value<bool?>("includeText") ?? true;
            var includeFamilies = parameters.Value<bool?>("includeFamilies") ?? true;
            var maxTextChars = Math.Max(0, Math.Min(parameters.Value<int?>("maxTextChars") ?? 20000, 200000));

            var index = ViewportIndex.Build(doc);
            var views = DraftingLibraryUtils.ResolveViews(doc, parameters, index);
            var page = views.Skip(offset).Take(limit).ToList();
            var pageIds = new HashSet<long>(page.Select(v => v.Id.GetValue()));

            var texts = new Dictionary<long, List<string>>();
            var families = new Dictionary<long, List<JObject>>();
            if (page.Count > 0 && includeText)
            {
                foreach (var note in DraftingLibraryUtils.Owned<TextNote>(doc, pageIds)
                             .OrderBy(n => n.Id.GetValue()))
                    Add(texts, note.OwnerViewId.GetValue(), Clean(note.Text));
            }

            if (page.Count > 0 && (includeText || includeFamilies))
            {
                var symbols = DraftingLibraryUtils.OwnedOfCategory<AnnotationSymbol>(doc, pageIds,
                        BuiltInCategory.OST_GenericAnnotation)
                    .Where(a => a.SuperComponent == null && a.Symbol != null)
                    .OrderBy(a => a.Id.GetValue())
                    .ToList();
                if (includeText)
                {
                    foreach (var symbol in symbols)
                    foreach (var value in SymbolText(symbol))
                        Add(texts, symbol.OwnerViewId.GetValue(), value);
                }

                if (includeFamilies)
                {
                    var instances = DraftingLibraryUtils.DetailComponents(doc, pageIds)
                        .Where(f => f.Symbol != null)
                        .Select(f => (instance: (FamilyInstance)f, category: "Detail Items"))
                        .Concat(symbols.Select(a => (instance: (FamilyInstance)a, category: "Generic Annotations")));
                    foreach (var g in instances.GroupBy(x => (x.instance.OwnerViewId.GetValue(), x.instance.Symbol.Id.GetValue())))
                    {
                        var symbol = g.First().instance.Symbol;
                        if (!families.TryGetValue(g.Key.Item1, out var list))
                            families[g.Key.Item1] = list = new List<JObject>();
                        list.Add(new JObject
                        {
                            ["name"] = symbol.FamilyName + " : " + symbol.Name,
                            ["category"] = g.First().category,
                            ["count"] = g.Count()
                        });
                    }

                    foreach (var group in DraftingLibraryUtils.Owned<Group>(doc, pageIds)
                                 .GroupBy(x => (x.OwnerViewId.GetValue(), x.GroupType?.Name ?? "?")))
                    {
                        if (!families.TryGetValue(group.Key.Item1, out var list))
                            families[group.Key.Item1] = list = new List<JObject>();
                        list.Add(new JObject
                        {
                            ["name"] = group.Key.Item2,
                            ["category"] = "Detail Groups",
                            ["count"] = group.Count()
                        });
                    }
                }
            }

            var rows = new JArray();
            foreach (var view in page)
            {
                var id = view.Id.GetValue();
                var placed = index.FindView(view.Id);
                var row = new JObject
                {
                    ["id"] = id,
                    ["name"] = view.Name,
                    ["viewType"] = view.ViewType.ToString(),
                    ["scale"] = view.Scale,
                    ["titleOnSheet"] = view.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION)?.AsString(),
                    ["sheetNumber"] = placed?.Sheet.SheetNumber,
                    ["sheetName"] = placed?.Sheet.Name,
                    ["detailNumber"] = placed?.DetailNumber
                };
                if (includeText)
                    row["text"] = new JArray(Cap(texts.TryGetValue(id, out var t) ? t : new List<string>(), maxTextChars));
                if (includeFamilies)
                    row["families"] = new JArray(families.TryGetValue(id, out var f)
                        ? f.OrderByDescending(x => x.Value<int>("count"))
                            .ThenBy(x => x.Value<string>("name"), StringComparer.OrdinalIgnoreCase)
                        : Enumerable.Empty<JObject>());
                rows.Add(row);
            }

            return Ok($"Indexed {page.Count} of {views.Count} views of '{doc.Title}'.", new JObject
            {
                ["documentTitle"] = doc.Title,
                ["documentPath"] = doc.PathName,
                ["total"] = views.Count,
                ["offset"] = offset,
                ["returned"] = page.Count,
                ["truncated"] = offset + page.Count < views.Count,
                ["views"] = rows
            });
        }

        private static void Add(Dictionary<long, List<string>> map, long viewId, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            if (!map.TryGetValue(viewId, out var list))
                map[viewId] = list = new List<string>();
            list.Add(value);
        }

        private static string Clean(string text) =>
            (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();

        /// <summary>Non-empty user-editable string parameter values (e.g. sub-detail title text).</summary>
        private static IEnumerable<string> SymbolText(AnnotationSymbol symbol)
        {
            return symbol.Parameters.Cast<Parameter>()
                .Where(p => p.Definition != null && !p.IsReadOnly && p.StorageType == StorageType.String &&
                            !SkippedSymbolParameters.Contains(p.Definition.Name))
                .Select(p => Clean(p.AsString()))
                .Where(s => s.Length > 0)
                .Distinct();
        }

        /// <summary>Keeps whole strings until the character budget is used up.</summary>
        private static IEnumerable<string> Cap(List<string> values, int maxChars)
        {
            var used = 0;
            foreach (var value in values)
            {
                if (used >= maxChars)
                    yield break;
                var text = value.Length > maxChars - used ? value.Substring(0, maxChars - used) : value;
                used += text.Length;
                yield return text;
            }
        }
    }
}
