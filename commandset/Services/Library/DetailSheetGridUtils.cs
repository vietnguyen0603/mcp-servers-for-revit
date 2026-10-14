using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Library
{
    /// <summary>Drawing area of a detail sheet in sheet feet, with where it came from.</summary>
    internal sealed class GridBorder
    {
        public double X0 { get; set; }
        public double Y0 { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public string Source { get; set; }
        public JObject Lines { get; set; }

        public JObject ToMm() => new JObject
        {
            ["x0"] = DocumentationUtils.FeetToMm(X0),
            ["y0"] = DocumentationUtils.FeetToMm(Y0),
            ["x1"] = DocumentationUtils.FeetToMm(X1),
            ["y1"] = DocumentationUtils.FeetToMm(Y1)
        };
    }

    /// <summary>
    ///     Helpers for layout_detail_sheet_grid: sheet selection, the title
    ///     block's inner border and viewport title geometry. Sheet coordinates
    ///     are feet internally and millimetres on the wire.
    /// </summary>
    internal static class DetailSheetGridUtils
    {
        public const string TemporaryPrefix = "~mcp";
        private const double MinBorderLineMm = 150;
        private const double FullLineRatio = 0.6;

        /// <summary>Sheets matching any of sheetIds / sheetNumberPrefixes / sheetNumberPattern, by sheet number.</summary>
        public static List<ViewSheet> SelectSheets(Document doc, JObject parameters)
        {
            var ids = parameters["sheetIds"] is JArray idArray
                ? new HashSet<long>(idArray.Select(t => t.Value<long>()))
                : null;
            var prefixes = (parameters["sheetNumberPrefixes"] as JArray)?
                .Select(t => t.ToString().Trim())
                .Where(s => s.Length > 0)
                .ToList();
            var pattern = parameters.Value<string>("sheetNumberPattern");
            var regex = string.IsNullOrWhiteSpace(pattern)
                ? null
                : new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if ((ids == null || ids.Count == 0) && (prefixes == null || prefixes.Count == 0) && regex == null)
                throw new ArgumentException("Select sheets with 'sheetIds', 'sheetNumberPrefixes' or 'sheetNumberPattern'.");

            return AllSheets(doc)
                .Where(s => (ids != null && ids.Contains(s.Id.GetValue()))
                            || (prefixes != null && prefixes.Any(p => s.SheetNumber.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                            || (regex != null && regex.IsMatch(s.SheetNumber)))
                .OrderBy(s => s.SheetNumber, StringComparer.Ordinal)
                .ToList();
        }

        public static List<ViewSheet> AllSheets(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsTemplate && !s.IsPlaceholder)
                .ToList();
        }

        public static FamilyInstance TitleBlockOf(Document doc, ViewSheet sheet)
        {
            return new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstElement() as FamilyInstance;
        }

        /// <summary>A viewport is titled unless its type's Show Title is off.</summary>
        public static bool IsTitled(Document doc, Viewport viewport)
        {
            var show = doc.GetElement(viewport.GetTypeId())?.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL);
            return show == null || show.AsInteger() != 0;
        }

        public static Outline LabelOutline(Viewport viewport)
        {
            try
            {
                var outline = viewport.GetLabelOutline();
                return outline == null || outline.IsEmpty ? null : outline;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static JArray BoxMm(Outline outline) => new JArray(
            DocumentationUtils.FeetToMm(outline.MinimumPoint.X),
            DocumentationUtils.FeetToMm(outline.MinimumPoint.Y),
            DocumentationUtils.FeetToMm(outline.MaximumPoint.X),
            DocumentationUtils.FeetToMm(outline.MaximumPoint.Y));

        /// <summary>
        ///     The explicit <c>border</c> {min, max} (mm), else the inner border
        ///     read from the title block family of the first selected sheet that has one.
        /// </summary>
        public static GridBorder ReadBorder(Document doc, JObject parameters, IList<ViewSheet> sheets, List<string> warnings,
            out FamilyInstance titleBlock)
        {
            titleBlock = sheets.Select(s => TitleBlockOf(doc, s)).FirstOrDefault(tb => tb != null);
            if (titleBlock != null)
            {
                var reference = titleBlock;
                var origin = Origin(reference);
                var mismatched = sheets
                    .Where(s =>
                    {
                        var tb = TitleBlockOf(doc, s);
                        return tb != null && (tb.GetTypeId() != reference.GetTypeId() || !Origin(tb).IsAlmostEqualTo(origin));
                    })
                    .Select(s => s.SheetNumber)
                    .ToList();
                if (mismatched.Count > 0)
                    warnings.Add($"Title block type or position differs from {reference.Symbol.FamilyName}: {reference.Name} on: " +
                                 string.Join(", ", mismatched.Take(20)) + "; the same border is used for every sheet.");
            }

            if (parameters["border"] is JObject explicitBorder)
            {
                var min = DocumentationUtils.ReadPointMm(explicitBorder["min"]) ?? throw new ArgumentException("'border.min' is required (mm).");
                var max = DocumentationUtils.ReadPointMm(explicitBorder["max"]) ?? throw new ArgumentException("'border.max' is required (mm).");
                return new GridBorder
                {
                    X0 = Math.Min(min.X, max.X), Y0 = Math.Min(min.Y, max.Y),
                    X1 = Math.Max(min.X, max.X), Y1 = Math.Max(min.Y, max.Y),
                    Source = "override"
                };
            }

            if (titleBlock == null)
            {
                var outline = sheets[0].Outline;
                warnings.Add("No title block on the selected sheets; using the sheet outline as the border.");
                return new GridBorder { X0 = outline.Min.U, Y0 = outline.Min.V, X1 = outline.Max.U, Y1 = outline.Max.V, Source = "sheetOutline" };
            }

            var blockId = titleBlock.Id;
            return DetectBorder(doc, titleBlock, sheets.First(s => TitleBlockOf(doc, s)?.Id == blockId), warnings);
        }

        private static XYZ Origin(FamilyInstance titleBlock) => (titleBlock.Location as LocationPoint)?.Point ?? XYZ.Zero;

        /// <summary>
        ///     Title-block instances expose no geometry, so the family is opened in
        ///     memory and its long straight lines read, then closed without saving.
        ///     The inner border is the innermost full-length line on each side of
        ///     the centre: inside the paper edge, and left of a title strip.
        /// </summary>
        private static GridBorder DetectBorder(Document doc, FamilyInstance titleBlock, ViewSheet sheet, List<string> warnings)
        {
            var origin = Origin(titleBlock);
            var vertical = new SortedDictionary<double, double>();
            var horizontal = new SortedDictionary<double, double>();
            Document familyDoc = null;
            try
            {
                familyDoc = doc.EditFamily(titleBlock.Symbol.Family);
                foreach (var curve in new FilteredElementCollector(familyDoc).OfClass(typeof(CurveElement)).Cast<CurveElement>())
                {
                    if (!(curve.GeometryCurve is Line line) || !line.IsBound)
                        continue;
                    var a = line.GetEndPoint(0);
                    var b = line.GetEndPoint(1);
                    var length = line.Length * DocumentationUtils.MmPerFoot;
                    if (length < MinBorderLineMm)
                        continue;
                    if (Math.Abs(a.X - b.X) < 1e-6)
                        Keep(vertical, Math.Round((a.X + origin.X) * DocumentationUtils.MmPerFoot, 1), length);
                    else if (Math.Abs(a.Y - b.Y) < 1e-6)
                        Keep(horizontal, Math.Round((a.Y + origin.Y) * DocumentationUtils.MmPerFoot, 1), length);
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Title block family could not be read ({ex.Message}); using its bounding box.");
            }
            finally
            {
                familyDoc?.Close(false);
            }

            var box = titleBlock.get_BoundingBox(sheet);
            double fx0 = box?.Min.X ?? sheet.Outline.Min.U, fy0 = box?.Min.Y ?? sheet.Outline.Min.V;
            double fx1 = box?.Max.X ?? sheet.Outline.Max.U, fy1 = box?.Max.Y ?? sheet.Outline.Max.V;

            var found = 0;
            double Side(double? mm, double fallback)
            {
                if (mm == null)
                    return fallback;
                found++;
                return DocumentationUtils.MmToFeet(mm.Value);
            }

            Inner(vertical, out var left, out var right);
            Inner(horizontal, out var bottom, out var top);
            var border = new GridBorder
            {
                X0 = Side(left, fx0),
                Y0 = Side(bottom, fy0),
                X1 = Side(right, fx1),
                Y1 = Side(top, fy1),
                Source = found == 4 ? "titleBlockFamily" : found == 0 ? "titleBlockBox" : "titleBlockFamily+box",
                Lines = new JObject
                {
                    ["vertical"] = Describe(vertical),
                    ["horizontal"] = Describe(horizontal)
                }
            };
            if (found < 4)
                warnings.Add("The inner border was not found on every side; check 'border' and pass an override if needed.");
            if (border.X1 - border.X0 <= 0 || border.Y1 - border.Y0 <= 0)
                throw new InvalidOperationException("The detected border is empty; pass 'border' explicitly.");
            return border;
        }

        private static void Keep(IDictionary<double, double> lines, double key, double length)
        {
            lines[key] = lines.TryGetValue(key, out var existing) ? Math.Max(existing, length) : length;
        }

        /// <summary>Innermost full-length line below and above the middle of the full-length lines.</summary>
        private static void Inner(SortedDictionary<double, double> lines, out double? low, out double? high)
        {
            low = high = null;
            if (lines.Count == 0)
                return;
            var longest = lines.Values.Max();
            var full = lines.Where(kv => kv.Value >= FullLineRatio * longest).Select(kv => kv.Key).ToList();
            var mid = (full.Min() + full.Max()) / 2;
            var below = full.Where(x => x < mid).ToList();
            var above = full.Where(x => x > mid).ToList();
            if (below.Count > 0)
                low = below.Max();
            if (above.Count > 0)
                high = above.Min();
        }

        private static JArray Describe(SortedDictionary<double, double> lines)
        {
            return new JArray(lines
                .OrderByDescending(kv => kv.Value)
                .Take(24)
                .OrderBy(kv => kv.Key)
                .Select(kv => kv.Key.ToString("0.#", CultureInfo.InvariantCulture) + " (" +
                              Math.Round(kv.Value).ToString(CultureInfo.InvariantCulture) + ")"));
        }

        /// <summary>
        ///     Regex alternation of sheet numbers (longest first) for text
        ///     references, or the caller's pattern.
        /// </summary>
        public static string SheetAlternation(string explicitPattern, IEnumerable<string> sheetNumbers)
        {
            if (!string.IsNullOrWhiteSpace(explicitPattern))
                return explicitPattern;
            var escaped = sheetNumbers
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .OrderByDescending(n => n.Length)
                .Select(Regex.Escape)
                .ToList();
            return escaped.Count == 0 ? "(?!)" : string.Join("|", escaped);
        }

        /// <summary>"n/SHEET" or "nA / SHEET" references to the given sheets.</summary>
        public static Regex TextReferenceRegex(string sheetAlternation) =>
            new Regex(@"\b(\d+[A-Z]?)\s*/\s*(" + sheetAlternation + @")\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static readonly Regex ThisSheetRegex =
            new Regex(@"\bDETAIL (\d+[A-Z]?) ON THIS(\s+)SHEET\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
