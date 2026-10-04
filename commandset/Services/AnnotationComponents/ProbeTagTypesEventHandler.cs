using System.Globalization;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services.Modify;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     probe_tag_types: reports which parameter(s) each loaded tag type displays.
    ///     Inside a transaction that is always rolled back it tags a sample element
    ///     once per tag type, reads IndependentTag.TagText, writes distinct marker
    ///     values into the writable text parameters of the element and its type (and
    ///     renames the type / family, and optionally sets the type's length
    ///     parameters to distinct values), reads the text again and matches markers.
    /// </summary>
    public class ProbeTagTypesEventHandler : JsonParameterEventHandler
    {
        private const int MaxTextProbes = 150;
        private const int MaxLengthProbes = 20;
        private const string TypeNameLabel = "Type Name";
        private const string FamilyNameLabel = "Family Name";

        public override string GetName() => "Probe Tag Types";

        private class Probe
        {
            public string Label;
            public string Owner;
            public string Marker;
        }

        private class TagProbe
        {
            public FamilySymbol Symbol;
            public IndependentTag Tag;
            public string Error;
            public string Original;
            public string Marked;
        }

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var view = DocumentationUtils.GetElement<View>(doc, DocumentationUtils.ReadId(parameters, "viewId")) ?? uiDoc.ActiveView;
            if (view == null || view.IsTemplate || view is ViewSheet || view is ViewSchedule)
                return Fail($"View '{view?.Name}' cannot host element tags; pass a plan, section or elevation viewId.");

            // Sample element
            Element element = null;
            var elementId = DocumentationUtils.ReadId(parameters, "elementId");
            Category category = null;
            var categoryName = parameters.Value<string>("category");
            if (!string.IsNullOrWhiteSpace(categoryName))
                category = DocumentationUtils.ResolveCategory(doc, categoryName) ?? throw new ArgumentException($"Category '{categoryName}' not found.");

            if (elementId != null)
            {
                element = doc.GetElement(elementId.Value.ToRevitElementId());
                if (element == null || element is ElementType) return Fail($"Element {elementId} is not an element instance.");
                category = category ?? element.Category;
            }
            else
            {
                if (category == null) return Fail("Give 'category' or 'elementId'.");
                element = new FilteredElementCollector(doc, view.Id).OfCategoryId(category.Id).WhereElementIsNotElementType()
                    .FirstOrDefault(e => !(e is ElementType) && e.Category != null);
                if (element == null)
                    return Fail($"No '{category.Name}' element is visible in view '{view.Name}'. Pass elementId or another viewId.");
            }

            if (element.Category == null) return Fail("The sample element has no category.");
            var anchor = TagElementsEventHandler.AnchorPoint(element, view)
                         ?? throw new InvalidOperationException("The sample element has no location in this view.");

            // Tag types
            var maxTagTypes = Math.Max(1, Math.Min(parameters.Value<int?>("maxTagTypes") ?? 60, 200));
            var symbols = TagSymbols(doc, element.Category, parameters, out var tagCategories);
            var truncated = symbols.Count > maxTagTypes;
            symbols = symbols.Take(maxTagTypes).ToList();

            var elementInfo = new
            {
                id = element.Id.GetValue(),
                category = element.Category.Name,
                typeName = ModelSelectionUtils.TypeName(doc, element),
                mark = ModelSelectionUtils.Mark(element)
            };
            if (symbols.Count == 0)
                return Ok($"No tag types loaded for '{element.Category.Name}' (looked in: {string.Join(", ", tagCategories)}). Load a tag family first.",
                    new { element = elementInfo, viewId = view.Id.GetValue(), tagCategories, tagTypes = new object[0] });

            var probeLengths = parameters.Value<bool?>("probeLengths") ?? true;
            var messages = new List<string>();
            var tags = symbols.Select(s => new TagProbe { Symbol = s }).ToList();
            var probes = new List<Probe>();
            var notes = new List<string>();

            var transaction = ModelSelectionUtils.StartTransaction(doc, "MCP: Probe Tag Types (rolled back)", messages);
            try
            {
                var reference = new Reference(element);
                foreach (var probe in tags)
                {
                    try
                    {
                        if (!probe.Symbol.IsActive) probe.Symbol.Activate();
                        probe.Tag = IndependentTag.Create(doc, probe.Symbol.Id, view.Id, reference, false, TagOrientation.Horizontal, anchor);
                    }
                    catch (Exception ex)
                    {
                        probe.Error = ex.Message;
                    }
                }

                doc.Regenerate();
                foreach (var probe in tags.Where(t => t.Tag != null)) probe.Original = ReadText(probe.Tag);

                // Text markers on the element and its type, then type / family names.
                var type = doc.GetElement(element.GetTypeId()) as ElementType;
                var counter = 0;
                Func<string> nextMarker = () => "QZ" + (++counter).ToString("D3", CultureInfo.InvariantCulture) + "Q";
                SetTextMarkers(element, "instance", probes, nextMarker);
                if (type != null)
                {
                    SetTextMarkers(type, "type", probes, nextMarker);
                    TrySet(() => type.Name = "QZTYPEQ", TypeNameLabel, "type", "QZTYPEQ", probes, notes);
                    if (type is FamilySymbol symbol && symbol.Family != null && !symbol.Family.IsInPlace)
                        TrySet(() => symbol.Family.Name = "QZFAMQ", FamilyNameLabel, "type", "QZFAMQ", probes, notes);
                }

                doc.Regenerate();

                if (probeLengths && type != null)
                {
                    var sub = new SubTransaction(doc);
                    sub.Start();
                    try
                    {
                        var value = 811;
                        foreach (var parameter in WritableParameters(type)
                                     .Where(p => p.StorageType == StorageType.Double && ParameterExpression.IsLength(p))
                                     .Take(MaxLengthProbes))
                        {
                            var marker = value.ToString(CultureInfo.InvariantCulture);
                            if (tags.Any(t => t.Original != null && t.Original.Contains(marker))) { value += 2; continue; }
                            try
                            {
                                if (parameter.Set(value / DocumentationUtils.MmPerFoot))
                                    probes.Add(new Probe { Label = parameter.Definition.Name, Owner = "type", Marker = marker });
                            }
                            catch (Exception)
                            {
                                // not settable for this family; skip
                            }
                            value += 2;
                        }

                        doc.Regenerate();
                        sub.Commit();
                    }
                    catch (Exception ex)
                    {
                        if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                        probes.RemoveAll(p => p.Owner == "type" && p.Marker.All(char.IsDigit));
                        notes.Add($"Length parameters could not be probed (the family did not regenerate: {ex.Message}).");
                    }
                }

                foreach (var probe in tags.Where(t => t.Tag != null)) probe.Marked = ReadText(probe.Tag);
            }
            finally
            {
                if (transaction.HasStarted() && !transaction.HasEnded()) transaction.RollBack();
                transaction.Dispose();
            }

            var results = new List<object>();
            var summary = new List<string>();
            foreach (var probe in tags)
            {
                var detected = new List<string>();
                string template = null;
                if (probe.Marked != null)
                {
                    template = probe.Marked;
                    foreach (var marker in probes)
                    {
                        if (probe.Marked.IndexOf(marker.Marker, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (!detected.Contains(marker.Label)) detected.Add(marker.Label);
                        template = ReplaceIgnoreCase(template, marker.Marker, "{" + marker.Label + "}");
                    }
                }

                var family = probe.Symbol.FamilyName;
                var label = probe.Error != null
                    ? $"could not tag ({probe.Error})"
                    : detected.Count > 0
                        ? string.Join(" + ", detected)
                        : string.IsNullOrEmpty(probe.Original)
                            ? "nothing displayed (empty or unsupported parameter)"
                            : $"no probed parameter (shows '{probe.Original}': static text or a non-text parameter)";
                summary.Add($"{family}: {probe.Symbol.Name} -> {label}");
                results.Add(new
                {
                    family,
                    type = probe.Symbol.Name,
                    id = probe.Symbol.Id.GetValue(),
                    tagCategory = probe.Symbol.Category?.Name,
                    multiCategory = probe.Symbol.Category != null && probe.Symbol.Category.Id.GetValue() == (long)BuiltInCategory.OST_MultiCategoryTags,
                    displayedText = probe.Original,
                    template,
                    detectedParameters = detected,
                    error = probe.Error
                });
            }

            var response = new JObject
            {
                ["element"] = JObject.FromObject(elementInfo),
                ["viewId"] = view.Id.GetValue(),
                ["tagCategories"] = new JArray(tagCategories),
                ["probedParameters"] = new JArray(probes.Select(p => $"{p.Label} ({p.Owner})")),
                ["summary"] = new JArray(summary),
                ["tagTypes"] = JArray.FromObject(results)
            };
            if (truncated) notes.Add($"Only the first {maxTagTypes} tag types were probed; raise maxTagTypes or pass tagTypeIds.");
            notes.Add("Lengths are detected by marker values 811, 813, ... mm; a label showing another unit or rounding may be missed.");
            response["notes"] = new JArray(notes);
            if (messages.Count > 0) response["revitMessages"] = new JArray(messages.Distinct().Take(10));

            return Ok($"Probed {tags.Count} tag type(s) on {element.Category.Name} {element.Id.GetValue()} in '{view.Name}'. Everything was rolled back.", response);
        }

        private static string ReadText(IndependentTag tag)
        {
            try
            {
                return tag.TagText ?? string.Empty;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static IEnumerable<Parameter> WritableParameters(Element element)
        {
            foreach (Parameter parameter in element.Parameters)
            {
                if (parameter == null || parameter.IsReadOnly || parameter.Definition == null) continue;
                if (string.IsNullOrEmpty(parameter.Definition.Name)) continue;
                yield return parameter;
            }
        }

        private static void SetTextMarkers(Element element, string owner, List<Probe> probes, Func<string> nextMarker)
        {
            var used = 0;
            foreach (var parameter in WritableParameters(element).Where(p => p.StorageType == StorageType.String)
                         .OrderBy(p => p.Definition.Name, StringComparer.Ordinal))
            {
                if (used >= MaxTextProbes) break;
                var marker = nextMarker();
                try
                {
                    if (!parameter.Set(marker)) continue;
                    probes.Add(new Probe { Label = parameter.Definition.Name, Owner = owner, Marker = marker });
                    used++;
                }
                catch (Exception)
                {
                    // some text parameters refuse arbitrary values; skip them
                }
            }
        }

        private static void TrySet(Action set, string label, string owner, string marker, List<Probe> probes, List<string> notes)
        {
            try
            {
                set();
                probes.Add(new Probe { Label = label, Owner = owner, Marker = marker });
            }
            catch (Exception ex)
            {
                notes.Add($"{label} could not be probed: {ex.Message}");
            }
        }

        private static string ReplaceIgnoreCase(string text, string oldValue, string newValue)
        {
            var index = text.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                text = text.Substring(0, index) + newValue + text.Substring(index + oldValue.Length);
                index = text.IndexOf(oldValue, index + newValue.Length, StringComparison.OrdinalIgnoreCase);
            }
            return text;
        }

        /// <summary>Tag family types for the element's category (plus multi-category tags), or the given tagTypeIds.</summary>
        private static List<FamilySymbol> TagSymbols(Document doc, Category category, JObject parameters, out List<string> tagCategories)
        {
            tagCategories = new List<string>();
            if (parameters["tagTypeIds"] is JArray explicitIds && explicitIds.Count > 0)
            {
                var list = new List<FamilySymbol>();
                foreach (var token in explicitIds)
                {
                    var symbol = doc.GetElement(token.Value<long>().ToRevitElementId()) as FamilySymbol
                                 ?? throw new ArgumentException($"{token} is not a tag family type.");
                    list.Add(symbol);
                }
                tagCategories.AddRange(list.Select(s => s.Category?.Name).Where(n => n != null).Distinct());
                return list;
            }

            var ids = TagCategoryIds(doc, category);
            if (parameters.Value<bool?>("includeMultiCategory") ?? true)
                ids.Add((long)BuiltInCategory.OST_MultiCategoryTags);

            foreach (var id in ids)
            {
                Category tagCategory = null;
                try
                {
                    tagCategory = Category.GetCategory(doc, id.ToRevitElementId());
                }
                catch (Exception)
                {
                    // ignore categories without a Category object
                }
                if (tagCategory != null) tagCategories.Add(tagCategory.Name);
            }

            return new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Category != null && ids.Contains(s.Category.Id.GetValue()))
                .OrderBy(s => s.Category.Id.GetValue() == (long)BuiltInCategory.OST_MultiCategoryTags ? 1 : 0)
                .ThenBy(s => s.FamilyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static HashSet<long> TagCategoryIds(Document doc, Category category)
        {
            var ids = new HashSet<long>();
            var value = category.Id.GetValue();
            // Cast to the enum: its underlying type is Int32 before Revit 2024 and Int64 after.
            var builtIn = (BuiltInCategory)value;
            if (value < 0 && Enum.IsDefined(typeof(BuiltInCategory), builtIn))
            {
                var name = builtIn.ToString();
                var candidates = new List<string> { name + "Tags" };
                if (name.EndsWith("s", StringComparison.Ordinal)) candidates.Add(name.Substring(0, name.Length - 1) + "Tags");
                foreach (var candidate in candidates)
                {
                    if (Enum.TryParse(candidate, out BuiltInCategory tag) && Enum.IsDefined(typeof(BuiltInCategory), tag))
                        ids.Add((long)tag);
                }
            }

            var singular = category.Name.EndsWith("s", StringComparison.Ordinal) ? category.Name.Substring(0, category.Name.Length - 1) : category.Name;
            foreach (Category candidate in doc.Settings.Categories)
            {
                if (candidate.CategoryType != CategoryType.Annotation) continue;
                if (string.Equals(candidate.Name, category.Name + " Tags", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.Name, singular + " Tags", StringComparison.OrdinalIgnoreCase))
                    ids.Add(candidate.Id.GetValue());
            }

            return ids;
        }
    }
}
