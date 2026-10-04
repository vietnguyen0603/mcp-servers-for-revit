using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services.Structure;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     style_tag_families: makes loaded tag / annotation families match the project text
    ///     style. For each selected family: EditFamily -> set every text / label type
    ///     (TextElementType: font, size, width factor, bold, italic, underline, colour, line
    ///     weight, background) and optionally the family category line weight / colour ->
    ///     LoadFamily back with overwrite -> close the family document. Line weight / colour can
    ///     also be set on the project's object style of the family category, which is what Revit
    ///     uses for tag lines and leaders once the category exists in the project.
    ///     Must run outside any transaction of the project document.
    /// </summary>
    public class StyleTagFamiliesEventHandler : JsonParameterEventHandler
    {
        private const int DefaultMaxFamilies = 100;

        public override string GetName() => "Style Tag Families";

        private class Style
        {
            public string Font;
            public double? SizeFeet;
            public double? WidthFactor;
            public int? Bold, Italic, Underline, Color, LineWeight, Background, LineColor;

            public bool TextEmpty => Font == null && SizeFeet == null && WidthFactor == null && Bold == null && Italic == null &&
                                     Underline == null && Color == null && LineWeight == null && Background == null;

            public bool LinesEmpty => LineWeight == null && LineColor == null;
        }

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            if (doc.IsFamilyDocument) return Fail("style_tag_families works in a project document; it edits the families loaded in it.");
            if (doc.IsModifiable) return Fail("The project document has an open transaction; families cannot be edited now.");

            var notes = new List<string>();
            var style = ReadStyle(doc, parameters, notes);
            if (style.TextEmpty && style.LinesEmpty)
                return Fail("Give likeTextType and/or at least one of font, textSizeMm, widthFactor, bold, italic, underline, color, lineWeight, lineColor, background.");

            var families = SelectFamilies(doc, parameters, notes, out var skipped);
            var maxFamilies = Math.Max(1, Math.Min(parameters.Value<int?>("maxFamilies") ?? DefaultMaxFamilies, 1000));
            if (families.Count > maxFamilies)
                return Fail($"{families.Count} families match, more than maxFamilies ({maxFamilies}). Narrow 'families' / 'categories' or raise maxFamilies.");
            if (families.Count == 0)
                return Ok("No editable family matches.", new JObject { ["families"] = new JArray(), ["skipped"] = JArray.FromObject(skipped), ["notes"] = new JArray(notes) });

            var dryRun = parameters.Value<bool?>("dryRun") ?? false;
            var familyLines = parameters.Value<bool?>("familyCategoryLines") ?? true;
            var projectLines = parameters.Value<bool?>("projectObjectStyles") ?? true;

            var reports = new JArray();
            var reloaded = 0;
            var failed = 0;
            var categoriesForProject = new HashSet<long>();

            foreach (var family in families)
            {
                var report = new JObject
                {
                    ["family"] = family.Name,
                    ["familyId"] = family.Id.GetValue(),
                    ["category"] = family.FamilyCategory?.Name
                };
                Document familyDoc = null;
                try
                {
                    familyDoc = doc.EditFamily(family);
                    var textTypes = new FilteredElementCollector(familyDoc).WhereElementIsElementType()
                        .OfType<TextElementType>().OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
                    var typeReports = new JArray();
                    var changes = 0;
                    var linesChanged = 0;

                    using (var transaction = dryRun ? null : new Transaction(familyDoc, "MCP: Style text types"))
                    {
                        transaction?.Start();
                        foreach (var type in textTypes)
                        {
                            var changed = ApplyText(type, style, dryRun);
                            if (changed.Count > 0) changes++;
                            typeReports.Add(new JObject { ["name"] = type.Name, ["changed"] = changed });
                        }

                        if (!style.LinesEmpty && familyLines)
                            linesChanged = ApplyLines(familyDoc.OwnerFamily?.FamilyCategory, style, dryRun, out _);
                        transaction?.Commit();
                    }

                    if (family.FamilyCategory != null && !style.LinesEmpty && projectLines)
                    {
                        categoriesForProject.Add(family.FamilyCategory.Id.GetValue());
                    }

                    report["textTypes"] = typeReports;
                    report["textTypesChanged"] = changes;
                    if (!style.LinesEmpty && familyLines) report["familyLineStylesChanged"] = linesChanged;
                    if (textTypes.Count == 0) report["note"] = "No text / label types in this family.";

                    if (!dryRun && (changes > 0 || linesChanged > 0))
                    {
                        familyDoc.LoadFamily(doc, new LoadFamilyEventHandler.OverwriteOptions(true));
                        report["reloaded"] = true;
                        reloaded++;
                    }
                    else
                    {
                        report["reloaded"] = false;
                    }
                }
                catch (Exception ex)
                {
                    report["error"] = ex.Message;
                    failed++;
                }
                finally
                {
                    try
                    {
                        familyDoc?.Close(false);
                    }
                    catch (Exception)
                    {
                        // already closed
                    }
                }

                reports.Add(report);
            }

            JArray projectStyles = null;
            if (categoriesForProject.Count > 0)
                projectStyles = ApplyProjectObjectStyles(doc, categoriesForProject.OrderBy(id => id), style, dryRun, notes);

            var response = new JObject
            {
                ["dryRun"] = dryRun,
                ["applied"] = StyleJson(style),
                ["familyCount"] = families.Count,
                [dryRun ? "wouldReload" : "reloaded"] = dryRun ? reports.Count(r => r.Value<int?>("textTypesChanged") > 0 || r.Value<int?>("familyLineStylesChanged") > 0) : reloaded,
                ["failed"] = failed,
                ["families"] = reports
            };
            if (projectStyles != null) response["projectObjectStyles"] = projectStyles;
            if (skipped.Count > 0) response["skipped"] = JArray.FromObject(skipped);
            if (notes.Count > 0) response["notes"] = new JArray(notes);

            var message = dryRun
                ? $"Dry run: {families.Count} family(ies) checked; pass dryRun:false to edit and reload them."
                : $"Restyled and reloaded {reloaded} of {families.Count} family(ies); {failed} failed.";
            return Ok(message, response);
        }

        // ---------------------------------------------------------------- inputs

        private static Style ReadStyle(Document doc, JObject p, List<string> notes)
        {
            var style = new Style();
            var like = p.Value<string>("likeTextType");
            if (!string.IsNullOrWhiteSpace(like))
            {
                var source = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>()
                                 .FirstOrDefault(t => string.Equals(t.Name, like.Trim(), StringComparison.OrdinalIgnoreCase))
                             ?? throw new ArgumentException($"Text type '{like}' not found in the project. Text types: " +
                                                            string.Join(", ", new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Select(t => t.Name).Take(40)));
                style.Font = source.get_Parameter(BuiltInParameter.TEXT_FONT)?.AsString();
                style.SizeFeet = source.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble();
                style.WidthFactor = source.get_Parameter(BuiltInParameter.TEXT_WIDTH_SCALE)?.AsDouble();
                style.Bold = source.get_Parameter(BuiltInParameter.TEXT_STYLE_BOLD)?.AsInteger();
                style.Italic = source.get_Parameter(BuiltInParameter.TEXT_STYLE_ITALIC)?.AsInteger();
                style.Underline = source.get_Parameter(BuiltInParameter.TEXT_STYLE_UNDERLINE)?.AsInteger();
                notes.Add($"Font, size, width factor, bold, italic and underline copied from '{source.Name}'.");
            }

            var font = p.Value<string>("font");
            if (!string.IsNullOrWhiteSpace(font)) style.Font = font.Trim();
            var size = p.Value<double?>("textSizeMm");
            if (size != null)
            {
                if (size <= 0 || size > 100) throw new ArgumentException("textSizeMm must be between 0 and 100.");
                style.SizeFeet = size.Value / 304.8;
            }
            var width = p.Value<double?>("widthFactor");
            if (width != null)
            {
                if (width < 0.01 || width > 10) throw new ArgumentException("widthFactor must be between 0.01 and 10.");
                style.WidthFactor = width;
            }
            if (p["bold"] != null) style.Bold = p.Value<bool>("bold") ? 1 : 0;
            if (p["italic"] != null) style.Italic = p.Value<bool>("italic") ? 1 : 0;
            if (p["underline"] != null) style.Underline = p.Value<bool>("underline") ? 1 : 0;
            if (p["color"] != null) style.Color = ManageAnnotationTypesEventHandler.ParseColor(p["color"]);
            if (p["lineColor"] != null) style.LineColor = ManageAnnotationTypesEventHandler.ParseColor(p["lineColor"]);
            var pen = p.Value<int?>("lineWeight");
            if (pen != null)
            {
                if (pen < 1 || pen > 16) throw new ArgumentException("lineWeight must be a pen number 1-16.");
                style.LineWeight = pen;
            }
            var background = p.Value<string>("background")?.Trim();
            if (!string.IsNullOrEmpty(background))
                style.Background = string.Equals(background, "transparent", StringComparison.OrdinalIgnoreCase) ? 1
                    : string.Equals(background, "opaque", StringComparison.OrdinalIgnoreCase) ? 0
                    : throw new ArgumentException("background must be 'opaque' or 'transparent'.");
            return style;
        }

        private static List<Family> SelectFamilies(Document doc, JObject p, List<string> notes, out List<object> skipped)
        {
            skipped = new List<object>();
            var names = p["families"]?.ToObject<List<string>>()?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList()
                        ?? new List<string>();
            var categoryNames = p["categories"]?.ToObject<List<string>>() ?? new List<string>();
            if (names.Count == 0 && categoryNames.Count == 0)
                throw new ArgumentException("Give 'families' and/or 'categories' (e.g. OST_StructuralFramingTags, OST_MultiCategoryTags, OST_GridHeads).");

            var categoryIds = new HashSet<long>();
            foreach (var name in categoryNames)
            {
                var category = DocumentationUtils.ResolveCategory(doc, name);
                if (category == null) notes.Add($"Category '{name}' not found; ignored.");
                else categoryIds.Add(category.Id.GetValue());
            }
            if (categoryNames.Count > 0 && categoryIds.Count == 0 && names.Count == 0)
                throw new ArgumentException($"None of the categories were found: {string.Join(", ", categoryNames)}.");

            var all = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().ToList();
            var result = new List<Family>();
            foreach (var family in all.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                var byName = names.Any(n => string.Equals(n, family.Name, StringComparison.OrdinalIgnoreCase));
                var byCategory = family.FamilyCategory != null && categoryIds.Contains(family.FamilyCategory.Id.GetValue());
                if (!byName && !byCategory) continue;
                if (family.IsInPlace || !family.IsEditable)
                {
                    skipped.Add(new { family = family.Name, reason = family.IsInPlace ? "in-place family" : "not editable" });
                    continue;
                }
                result.Add(family);
            }

            foreach (var name in names.Where(n => all.All(f => !string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase))))
                notes.Add($"Family '{name}' is not loaded in the project.");
            return result;
        }

        // ---------------------------------------------------------------- applying

        private static JObject ApplyText(TextElementType type, Style style, bool dryRun)
        {
            var changed = new JObject();
            SetString(type, BuiltInParameter.TEXT_FONT, style.Font, "font", changed, dryRun);
            SetDouble(type, BuiltInParameter.TEXT_SIZE, style.SizeFeet, "textSizeMm", changed, dryRun, 304.8);
            SetDouble(type, BuiltInParameter.TEXT_WIDTH_SCALE, style.WidthFactor, "widthFactor", changed, dryRun, 1);
            SetInt(type, BuiltInParameter.TEXT_STYLE_BOLD, style.Bold, "bold", changed, dryRun);
            SetInt(type, BuiltInParameter.TEXT_STYLE_ITALIC, style.Italic, "italic", changed, dryRun);
            SetInt(type, BuiltInParameter.TEXT_STYLE_UNDERLINE, style.Underline, "underline", changed, dryRun);
            SetInt(type, BuiltInParameter.LINE_COLOR, style.Color, "color", changed, dryRun, true);
            SetInt(type, BuiltInParameter.LINE_PEN, style.LineWeight, "lineWeight", changed, dryRun);
            SetInt(type, BuiltInParameter.TEXT_BACKGROUND, style.Background, "background", changed, dryRun);
            return changed;
        }

        private static Parameter Writable(Element element, BuiltInParameter bip)
        {
            var parameter = element.get_Parameter(bip);
            return parameter == null || parameter.IsReadOnly ? null : parameter;
        }

        private static void SetString(Element e, BuiltInParameter bip, string value, string key, JObject changed, bool dryRun)
        {
            if (value == null) return;
            var parameter = Writable(e, bip);
            if (parameter == null || parameter.StorageType != StorageType.String) return;
            var before = parameter.AsString();
            if (string.Equals(before, value, StringComparison.Ordinal)) return;
            if (!dryRun && !parameter.Set(value)) return;
            changed[key] = new JArray(before, value);
        }

        private static void SetDouble(Element e, BuiltInParameter bip, double? value, string key, JObject changed, bool dryRun, double reportScale)
        {
            if (value == null) return;
            var parameter = Writable(e, bip);
            if (parameter == null || parameter.StorageType != StorageType.Double) return;
            var before = parameter.AsDouble();
            if (Math.Abs(before - value.Value) < 1e-9) return;
            if (!dryRun && !parameter.Set(value.Value)) return;
            changed[key] = new JArray(Math.Round(before * reportScale, 4), Math.Round(value.Value * reportScale, 4));
        }

        private static void SetInt(Element e, BuiltInParameter bip, int? value, string key, JObject changed, bool dryRun, bool isColor = false)
        {
            if (value == null) return;
            var parameter = Writable(e, bip);
            if (parameter == null || parameter.StorageType != StorageType.Integer) return;
            var before = parameter.AsInteger();
            if (before == value.Value) return;
            if (!dryRun && !parameter.Set(value.Value)) return;
            changed[key] = isColor ? new JArray(ColorText(before), ColorText(value.Value)) : new JArray(before, value.Value);
        }

        private static string ColorText(int value) => $"#{value & 0xFF:X2}{(value >> 8) & 0xFF:X2}{(value >> 16) & 0xFF:X2}";

        private static Color ToColor(int value) => new Color((byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF));

        /// <summary>Sets the projection line weight / colour of a category and its subcategories; returns how many changed.</summary>
        private static int ApplyLines(Category category, Style style, bool dryRun, out List<string> names)
        {
            names = new List<string>();
            if (category == null) return 0;
            var count = 0;
            var all = new List<Category> { category };
            foreach (Category sub in category.SubCategories) all.Add(sub);
            foreach (var c in all)
            {
                var changed = false;
                if (style.LineWeight != null)
                {
                    var current = c.GetLineWeight(GraphicsStyleType.Projection);
                    if (current != style.LineWeight)
                    {
                        if (!dryRun) c.SetLineWeight(style.LineWeight.Value, GraphicsStyleType.Projection);
                        changed = true;
                    }
                }
                if (style.LineColor != null)
                {
                    var target = ToColor(style.LineColor.Value);
                    var current = c.LineColor;
                    if (current == null || !current.IsValid || current.Red != target.Red || current.Green != target.Green || current.Blue != target.Blue)
                    {
                        if (!dryRun) c.LineColor = target;
                        changed = true;
                    }
                }
                if (changed)
                {
                    count++;
                    names.Add(c.Name);
                }
            }
            return count;
        }

        private static JArray ApplyProjectObjectStyles(Document doc, IEnumerable<long> categoryIds, Style style, bool dryRun, List<string> notes)
        {
            var report = new JArray();
            Transaction transaction = null;
            try
            {
                if (!dryRun)
                {
                    transaction = new Transaction(doc, "MCP: Tag object styles");
                    transaction.Start();
                }
                foreach (var id in categoryIds)
                {
                    Category category = null;
                    foreach (Category c in doc.Settings.Categories)
                        if (c.Id.GetValue() == id)
                        {
                            category = c;
                            break;
                        }
                    if (category == null)
                    {
                        try
                        {
                            category = Category.GetCategory(doc, id.ToRevitElementId());
                        }
                        catch (Exception)
                        {
                            // not available
                        }
                    }
                    if (category == null) continue;
                    ApplyLines(category, style, dryRun, out var names);
                    report.Add(new JObject { ["category"] = category.Name, ["changed"] = new JArray(names) });
                }
                transaction?.Commit();
            }
            catch (Exception ex)
            {
                if (transaction != null && transaction.HasStarted() && !transaction.HasEnded()) transaction.RollBack();
                notes.Add($"Project object styles not changed: {ex.Message}");
            }
            finally
            {
                transaction?.Dispose();
            }
            return report;
        }

        private static JObject StyleJson(Style style)
        {
            var json = new JObject();
            if (style.Font != null) json["font"] = style.Font;
            if (style.SizeFeet != null) json["textSizeMm"] = Math.Round(style.SizeFeet.Value * 304.8, 3);
            if (style.WidthFactor != null) json["widthFactor"] = Math.Round(style.WidthFactor.Value, 3);
            if (style.Bold != null) json["bold"] = style.Bold == 1;
            if (style.Italic != null) json["italic"] = style.Italic == 1;
            if (style.Underline != null) json["underline"] = style.Underline == 1;
            if (style.Color != null) json["color"] = ColorText(style.Color.Value);
            if (style.LineWeight != null) json["lineWeight"] = style.LineWeight;
            if (style.LineColor != null) json["lineColor"] = ColorText(style.LineColor.Value);
            if (style.Background != null) json["background"] = style.Background == 1 ? "transparent" : "opaque";
            return json;
        }
    }
}
