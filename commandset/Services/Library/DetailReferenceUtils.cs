using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Library
{
    /// <summary>
    ///     Options shared by the manual detail-reference tools: which family
    ///     instances count as reference bubbles and which text parameters hold
    ///     the referenced detail and sheet numbers.
    /// </summary>
    internal sealed class DetailReferenceOptions
    {
        public static readonly string[] DefaultPlaceholders = { "", "-", "--", "X", "XX", "S-000" };

        public string FamilyNameContains { get; private set; } = "Section Cut";
        public string DetailNumberParam { get; private set; } = "Detail Number";
        public string SheetNumberParam { get; private set; } = "Sheet Number";
        public HashSet<string> Placeholders { get; private set; }

        public static DetailReferenceOptions Read(JObject parameters)
        {
            var options = new DetailReferenceOptions();
            var pattern = parameters.Value<string>("familyNameContains");
            if (!string.IsNullOrWhiteSpace(pattern))
                options.FamilyNameContains = pattern.Trim();
            var detail = parameters.Value<string>("detailNumberParam");
            if (!string.IsNullOrWhiteSpace(detail))
                options.DetailNumberParam = detail.Trim();
            var sheet = parameters.Value<string>("sheetNumberParam");
            if (!string.IsNullOrWhiteSpace(sheet))
                options.SheetNumberParam = sheet.Trim();

            var placeholders = parameters["placeholders"] is JArray array
                ? array.Select(t => t.Type == JTokenType.Null ? string.Empty : t.ToString())
                : DefaultPlaceholders;
            options.Placeholders = new HashSet<string>(placeholders.Select(DetailReferenceUtils.Normalize));
            return options;
        }
    }

    /// <summary>A manual reference bubble and its two reference parameters.</summary>
    internal sealed class ReferenceBubble
    {
        public FamilyInstance Instance { get; set; }
        public Parameter DetailParameter { get; set; }
        public Parameter SheetParameter { get; set; }
        public string DetailNumber => DetailReferenceUtils.ReadText(DetailParameter);
        public string SheetNumber => DetailReferenceUtils.ReadText(SheetParameter);
    }

    /// <summary>A view placed on a sheet, keyed by sheet number + detail number.</summary>
    internal sealed class PlacedView
    {
        public Viewport Viewport { get; set; }
        public ViewSheet Sheet { get; set; }
        public View View { get; set; }
        public string DetailNumber { get; set; }
    }

    /// <summary>Index of every viewport in the document.</summary>
    internal sealed class ViewportIndex
    {
        public Dictionary<string, PlacedView> ByKey { get; } = new Dictionary<string, PlacedView>();
        public Dictionary<long, PlacedView> ByViewId { get; } = new Dictionary<long, PlacedView>();
        public HashSet<string> SheetNumbers { get; } = new HashSet<string>();

        public static ViewportIndex Build(Document doc)
        {
            var index = new ViewportIndex();
            foreach (var sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
            {
                if (sheet.IsTemplate)
                    continue;
                index.SheetNumbers.Add(DetailReferenceUtils.Normalize(sheet.SheetNumber));
                foreach (var viewportId in sheet.GetAllViewports())
                {
                    if (!(doc.GetElement(viewportId) is Viewport viewport))
                        continue;
                    var placed = new PlacedView
                    {
                        Viewport = viewport,
                        Sheet = sheet,
                        View = doc.GetElement(viewport.ViewId) as View,
                        DetailNumber = DetailReferenceUtils.ViewportDetailNumber(viewport)
                    };
                    index.ByKey[DetailReferenceUtils.Key(sheet.SheetNumber, placed.DetailNumber)] = placed;
                    index.ByViewId[viewport.ViewId.GetValue()] = placed;
                }
            }

            return index;
        }

        public PlacedView Find(string sheetNumber, string detailNumber)
        {
            return ByKey.TryGetValue(DetailReferenceUtils.Key(sheetNumber, detailNumber), out var placed)
                ? placed
                : null;
        }

        public PlacedView FindView(ElementId viewId)
        {
            return viewId != null && ByViewId.TryGetValue(viewId.GetValue(), out var placed) ? placed : null;
        }
    }

    internal static class DetailReferenceUtils
    {
        public static string Normalize(string value) => (value ?? string.Empty).Trim().ToUpperInvariant();

        public static string Key(string sheetNumber, string detailNumber) =>
            Normalize(sheetNumber) + "\u0001" + Normalize(detailNumber);

        public static bool SameValue(string a, string b) => Normalize(a) == Normalize(b);

        public static string ReadText(Parameter parameter)
        {
            if (parameter == null || !parameter.HasValue)
                return string.Empty;
            return (parameter.StorageType == StorageType.String ? parameter.AsString() : parameter.AsValueString())
                   ?? string.Empty;
        }

        public static string ViewportDetailNumber(Viewport viewport)
        {
            return viewport.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.AsString() ?? string.Empty;
        }

        /// <summary>Instance parameter first, then the type parameter.</summary>
        public static Parameter FindParameter(FamilyInstance instance, string name)
        {
            return instance.LookupParameter(name) ?? instance.Symbol?.LookupParameter(name);
        }

        /// <summary>
        ///     View-specific family instances whose family name contains the
        ///     configured pattern and that carry both reference parameters.
        ///     Nested sub-components are skipped. Ordered by element id.
        /// </summary>
        public static List<ReferenceBubble> CollectBubbles(Document doc, DetailReferenceOptions options,
            out int missingParameters)
        {
            missingParameters = 0;
            var bubbles = new List<ReferenceBubble>();
            var instances = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .Where(fi => fi.ViewSpecific && fi.SuperComponent == null)
                .Where(fi => (fi.Symbol?.FamilyName ?? string.Empty)
                    .IndexOf(options.FamilyNameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(fi => fi.Id.GetValue());

            foreach (var instance in instances)
            {
                var detail = FindParameter(instance, options.DetailNumberParam);
                var sheet = FindParameter(instance, options.SheetNumberParam);
                if (detail == null || sheet == null)
                {
                    missingParameters++;
                    continue;
                }

                bubbles.Add(new ReferenceBubble { Instance = instance, DetailParameter = detail, SheetParameter = sheet });
            }

            return bubbles;
        }

        /// <summary>Writes a text value to a bubble parameter, refusing shared type parameters.</summary>
        public static void WriteValue(FamilyInstance instance, Parameter parameter, string value)
        {
            if (parameter.Element != null && parameter.Element.Id != instance.Id)
                throw new InvalidOperationException(
                    $"'{parameter.Definition.Name}' is a type parameter; changing it would affect every bubble of the type.");
            if (parameter.IsReadOnly)
                throw new InvalidOperationException($"'{parameter.Definition.Name}' is read-only.");
            var ok = parameter.StorageType == StorageType.String
                ? parameter.Set(value ?? string.Empty)
                : parameter.SetValueString(value ?? string.Empty);
            if (!ok)
                throw new InvalidOperationException($"Could not set '{parameter.Definition.Name}' to '{value}'.");
        }
    }
}
