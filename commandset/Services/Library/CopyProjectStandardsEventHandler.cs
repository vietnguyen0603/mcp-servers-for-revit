using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Library
{
    /// <summary>
    ///     Transfer-Project-Standards equivalent (which has no API): copies view templates, filters,
    ///     annotation/datum/viewport types, arrowheads, line/fill patterns, materials, line styles,
    ///     object styles, wall/floor types and annotation families from another project into the
    ///     active one. Elements are copied with ElementTransformUtils.CopyElements, line styles are
    ///     recreated as Lines subcategories, object styles by setting the category values, and
    ///     families through the family editor. The source is never modified or saved; a source
    ///     opened from a path is closed unsaved. All active-document changes except family loads
    ///     happen in one transaction, with a sub-transaction per item.
    /// </summary>
    public class CopyProjectStandardsEventHandler : JsonParameterEventHandler
    {
        private const int MaxListed = 300;
        private const int MaxReported = 200;

        public override string GetName() => "Copy Project Standards";

        private class Request
        {
            public string Kind;
            public List<string> Names = new List<string>();
            public List<string> Categories = new List<string>();
        }

        private class KindReport
        {
            public readonly JArray Copied = new JArray();
            public readonly JArray Overwritten = new JArray();
            public readonly JArray Skipped = new JArray();
            public readonly JArray Renamed = new JArray();
            public readonly JArray Failed = new JArray();
            public readonly List<string> Notes = new List<string>();

            public JObject ToJson()
            {
                var result = new JObject
                {
                    ["copied"] = Copied.Count,
                    ["overwritten"] = Overwritten.Count,
                    ["skipped"] = Skipped.Count,
                    ["renamed"] = Renamed.Count,
                    ["failed"] = Failed.Count
                };
                AddList(result, "copiedItems", Copied);
                AddList(result, "overwrittenItems", Overwritten);
                AddList(result, "skippedItems", Skipped);
                AddList(result, "renamedItems", Renamed);
                AddList(result, "failedItems", Failed);
                if (Notes.Count > 0)
                    result["notes"] = new JArray(Notes.Distinct().Take(50));
                return result;
            }

            private static void AddList(JObject target, string name, JArray items)
            {
                if (items.Count > 0)
                    target[name] = new JArray(items.Take(MaxReported));
            }
        }

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var target = uiDoc.Document;
            var listOnly = parameters.Value<bool?>("listOnly") == true;
            var requests = ParseRequests(parameters["kinds"] as JArray, listOnly);
            var mode = (parameters.Value<string>("onDuplicate") ?? "useDestination").Trim();
            if (!new[] { "useDestination", "overwrite", "skip" }.Contains(mode, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("onDuplicate must be useDestination, overwrite or skip.");
            mode = mode.ToLowerInvariant();

            var source = SourceDocumentUtils.Open(target, parameters.Value<string>("sourceDocument"), parameters.Value<string>("sourcePath"), out var openedHere);
            try
            {
                if (source.Equals(target))
                    throw new ArgumentException("The source is the active document; give another document.");
                if (source.IsFamilyDocument)
                    throw new ArgumentException("The source must be a project or project template, not a family.");

                if (listOnly)
                    return Ok($"Listed project standards of '{source.Title}'.", List(source, target, requests));

                if (target.IsFamilyDocument)
                    throw new ArgumentException("The active document is a family; open the project that should receive the standards.");
                if (target.IsModifiable)
                    throw new InvalidOperationException("The active document has an open transaction; finish it first.");

                var before = Snapshot(target);
                var reported = new HashSet<long>();
                var reports = new Dictionary<string, KindReport>();

                // Families first: the family editor load runs outside our transaction.
                foreach (var request in requests.Where(r => ProjectStyleUtils.FamilyKinds.Contains(r.Kind)))
                    CopyFamilies(source, target, request, mode, Report(reports, request.Kind), reported);

                var elementRequests = requests.Where(r => !ProjectStyleUtils.FamilyKinds.Contains(r.Kind)).ToList();
                if (elementRequests.Count > 0)
                {
                    using (var transaction = DocumentationUtils.StartTransaction(target, "MCP: Copy Project Standards"))
                    {
                        foreach (var request in elementRequests)
                        {
                            var report = Report(reports, request.Kind);
                            switch (request.Kind)
                            {
                                case "lineStyles":
                                    CopyLineStyles(source, target, request, mode, report);
                                    break;
                                case "objectStyles":
                                    ApplyObjectStyles(source, target, request, report);
                                    break;
                                default:
                                    CopyElementKind(source, target, request, mode, report, reported);
                                    break;
                            }
                        }

                        var status = transaction.Commit();
                        if (status != TransactionStatus.Committed)
                            throw new InvalidOperationException($"Revit did not commit the copy ({status}); nothing was changed except loaded families.");
                    }
                }

                var alsoCreated = Snapshot(target)
                    .Where(id => !before.Contains(id) && !reported.Contains(id))
                    .Select(id => target.GetElement(id.ToRevitElementId()))
                    .Where(e => e != null)
                    .Select(e => new JObject { ["name"] = e.Name, ["id"] = e.Id.GetValue(), ["class"] = e.GetType().Name })
                    .Take(MaxReported).ToList();

                var kindsJson = new JObject();
                foreach (var kind in ProjectStyleUtils.Kinds.Where(reports.ContainsKey))
                    kindsJson[kind] = reports[kind].ToJson();
                var summary = new JObject
                {
                    ["copied"] = reports.Values.Sum(r => r.Copied.Count),
                    ["overwritten"] = reports.Values.Sum(r => r.Overwritten.Count),
                    ["renamed"] = reports.Values.Sum(r => r.Renamed.Count),
                    ["skipped"] = reports.Values.Sum(r => r.Skipped.Count),
                    ["failed"] = reports.Values.Sum(r => r.Failed.Count)
                };
                return Ok($"Copied project standards from '{source.Title}': {summary["copied"]} copied, {summary["overwritten"]} overwritten, " +
                          $"{summary["skipped"]} skipped, {summary["failed"]} failed.",
                    new JObject
                    {
                        ["source"] = source.Title,
                        ["closedAfterCopy"] = openedHere,
                        ["onDuplicate"] = mode,
                        ["summary"] = summary,
                        ["kinds"] = kindsJson,
                        ["alsoCreated"] = new JArray(alsoCreated),
                        ["alsoCreatedNote"] = alsoCreated.Count > 0
                            ? "Elements brought in as dependencies (e.g. filters of a copied view template, patterns or arrowheads of a type). A name ending in a number may be a renamed duplicate."
                            : null
                    });
            }
            finally
            {
                if (openedHere)
                    source.Close(false);
            }
        }

        private static KindReport Report(Dictionary<string, KindReport> reports, string kind)
        {
            if (!reports.TryGetValue(kind, out var report))
                reports[kind] = report = new KindReport();
            return report;
        }

        private static List<Request> ParseRequests(JArray kinds, bool listOnly)
        {
            var requests = new List<Request>();
            if (kinds == null || kinds.Count == 0)
            {
                if (!listOnly)
                    throw new ArgumentException("Give kinds to copy, e.g. [\"viewTemplates\", {\"kind\":\"textTypes\",\"names\":[\"2.5mm*\"]}] (or listOnly:true).");
                return ProjectStyleUtils.Kinds.Select(k => new Request { Kind = k }).ToList();
            }

            foreach (var token in kinds)
            {
                var request = new Request();
                if (token.Type == JTokenType.String)
                {
                    request.Kind = ProjectStyleUtils.NormalizeKind(token.ToString());
                }
                else if (token is JObject item)
                {
                    request.Kind = ProjectStyleUtils.NormalizeKind(item.Value<string>("kind"));
                    request.Names = (item["names"] as JArray)?.Values<string>().Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>();
                    request.Categories = (item["categories"] as JArray)?.Values<string>().Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>();
                }
                else
                {
                    throw new ArgumentException("Each kinds item is a kind name or { kind, names?, categories? }.");
                }

                var existing = requests.FirstOrDefault(r => r.Kind == request.Kind);
                if (existing == null)
                {
                    requests.Add(request);
                }
                else if (existing.Names.Count > 0 && request.Names.Count > 0)
                {
                    existing.Names.AddRange(request.Names);
                    existing.Categories.AddRange(request.Categories);
                }
                else
                {
                    existing.Names.Clear(); // one unfiltered request wins
                    existing.Categories.AddRange(request.Categories);
                }
            }

            // Dependency order regardless of the order given.
            return requests.OrderBy(r => Array.IndexOf(ProjectStyleUtils.Kinds, r.Kind)).ToList();
        }

        // ---------------------------------------------------------------- list

        private static JObject List(Document source, Document target, List<Request> requests)
        {
            var kinds = new JObject();
            foreach (var request in requests)
            {
                var items = new JArray();
                var total = 0;
                var inTarget = 0;
                if (request.Kind == "lineStyles" || request.Kind == "objectStyles")
                {
                    var categories = request.Kind == "lineStyles"
                        ? ProjectStyleUtils.LineStyles(source, request.Names)
                        : ProjectStyleUtils.ObjectStyleCategories(source, request.Names);
                    foreach (var category in categories)
                    {
                        total++;
                        var exists = ProjectStyleUtils.FindCategory(target, category) != null;
                        if (exists)
                            inTarget++;
                        if (items.Count >= MaxListed)
                            continue;
                        var entry = ProjectStyleUtils.CategoryGraphics(source, category);
                        entry.AddFirst(new JProperty("existsInTarget", exists));
                        entry.AddFirst(new JProperty("name", category.Name));
                        if (request.Kind == "objectStyles" && category.SubCategories.Size > 0)
                            entry["subcategories"] = category.SubCategories.Size;
                        items.Add(entry);
                    }
                }
                else if (ProjectStyleUtils.FamilyKinds.Contains(request.Kind))
                {
                    foreach (var family in ProjectStyleUtils.Collect(source, request.Kind, request.Names, request.Categories).Cast<Family>())
                    {
                        total++;
                        var exists = SourceDocumentUtils.FindFamily(target, family.Name) != null;
                        if (exists)
                            inTarget++;
                        if (items.Count < MaxListed)
                            items.Add(new JObject
                            {
                                ["name"] = family.Name,
                                ["category"] = family.FamilyCategory?.Name,
                                ["typeCount"] = family.GetFamilySymbolIds().Count,
                                ["existsInTarget"] = exists
                            });
                    }
                }
                else
                {
                    var index = ProjectStyleUtils.Index(target, request.Kind);
                    foreach (var element in ProjectStyleUtils.Collect(source, request.Kind, request.Names, request.Categories))
                    {
                        total++;
                        var exists = index.ContainsKey(ProjectStyleUtils.Key(element));
                        if (exists)
                            inTarget++;
                        if (items.Count >= MaxListed)
                            continue;
                        var entry = new JObject { ["name"] = element.Name, ["existsInTarget"] = exists };
                        if (element is ElementType type && !(element is WallType) && !(element is FloorType))
                            entry["family"] = type.FamilyName;
                        if (element is View view)
                            entry["viewType"] = view.ViewType.ToString();
                        if (element is FillPatternElement fill)
                            entry["target"] = fill.GetFillPattern().Target.ToString();
                        items.Add(entry);
                    }
                }

                kinds[request.Kind] = new JObject { ["count"] = total, ["existInTarget"] = inTarget, ["truncated"] = total > items.Count, ["items"] = items };
            }

            return new JObject { ["source"] = source.Title, ["kinds"] = kinds };
        }

        // ---------------------------------------------------------------- element kinds

        private static void CopyElementKind(Document source, Document target, Request request, string mode, KindReport report, HashSet<long> reported)
        {
            var items = ProjectStyleUtils.Collect(source, request.Kind, request.Names, request.Categories);
            ReportUnmatched(request, items.Select(i => i.Name), report);
            if (items.Count == 0)
                return;

            var index = ProjectStyleUtils.Index(target, request.Kind);
            var options = new CopyPasteOptions();
            options.SetDuplicateTypeNamesHandler(mode == "skip"
                ? (IDuplicateTypeNamesHandler)new SourceDocumentUtils.AbortOnDuplicateTypes()
                : new SourceDocumentUtils.UseDestinationTypes());

            foreach (var item in items)
            {
                index.TryGetValue(ProjectStyleUtils.Key(item), out var existing);
                if (existing != null && mode != "overwrite")
                {
                    report.Skipped.Add(new JObject { ["name"] = item.Name, ["reason"] = "exists in the active project", ["id"] = existing.Id.GetValue() });
                    continue;
                }

                using (var sub = new SubTransaction(target))
                {
                    sub.Start();
                    try
                    {
                        if (existing != null)
                        {
                            var replacement = Overwrite(source, item, target, existing, options, report);
                            sub.Commit();
                            index[ProjectStyleUtils.Key(replacement.element)] = replacement.element;
                            reported.Add(replacement.element.Id.GetValue());
                            report.Overwritten.Add(new JObject { ["name"] = item.Name, ["id"] = replacement.element.Id.GetValue(), ["how"] = replacement.how });
                        }
                        else
                        {
                            var copy = CopyOne(source, item, target, options);
                            sub.Commit();
                            index[ProjectStyleUtils.Key(copy)] = copy;
                            reported.Add(copy.Id.GetValue());
                            if (string.Equals(copy.Name, item.Name, StringComparison.Ordinal))
                                report.Copied.Add(new JObject { ["name"] = copy.Name, ["id"] = copy.Id.GetValue() });
                            else
                                report.Renamed.Add(new JObject { ["name"] = item.Name, ["newName"] = copy.Name, ["id"] = copy.Id.GetValue() });
                        }
                    }
                    catch (Exception ex)
                    {
                        if (sub.GetStatus() == TransactionStatus.Started)
                            sub.RollBack();
                        if (mode == "skip" && existing == null)
                            report.Skipped.Add(new JObject { ["name"] = item.Name, ["reason"] = "copying it would bring types whose names already exist (onDuplicate=skip): " + ex.Message });
                        else
                            report.Failed.Add(new JObject { ["name"] = item.Name, ["reason"] = ex.Message });
                    }
                }
            }
        }

        private static void ReportUnmatched(Request request, IEnumerable<string> names, KindReport report)
        {
            var list = names.ToList();
            foreach (var pattern in request.Names.Where(p => !list.Any(n => SourceDocumentUtils.Like(n, p))))
                report.Failed.Add(new JObject { ["name"] = pattern, ["reason"] = $"no {request.Kind} item matching '{pattern}' in the source (use listOnly)" });
        }

        /// <summary>Copies one element across documents and returns the copy of that element.</summary>
        private static Element CopyOne(Document source, Element item, Document target, CopyPasteOptions options)
        {
            var ids = ElementTransformUtils.CopyElements(source, new List<ElementId> { item.Id }, target, Transform.Identity, options);
            var copies = ids.Select(target.GetElement).Where(e => e != null).ToList();
            return copies.FirstOrDefault(e => e.GetType() == item.GetType() && string.Equals(e.Name, item.Name, StringComparison.OrdinalIgnoreCase))
                   ?? copies.FirstOrDefault(e => e.GetType() == item.GetType())
                   ?? copies.FirstOrDefault()
                   ?? throw new InvalidOperationException("Revit copied nothing.");
        }

        private static (Element element, string how) Overwrite(Document source, Element item, Document target, Element existing,
            CopyPasteOptions options, KindReport report)
        {
            switch (item)
            {
                case LinePatternElement linePattern:
                    ((LinePatternElement)existing).SetLinePattern(linePattern.GetLinePattern());
                    return (existing, "segments updated");
                case FillPatternElement fillPattern:
                    ((FillPatternElement)existing).SetFillPattern(fillPattern.GetFillPattern());
                    return (existing, "pattern updated");
                case Material material:
                    OverwriteMaterial(source, material, target, (Material)existing, report);
                    return (existing, $"graphics and {CopyParameters(source, item, target, existing, report)} parameters updated");
                case View _:
                case ParameterFilterElement _:
                    return Replace(source, item, target, existing, options);
                case HostObjAttributes host:
                    var count = CopyParameters(source, item, target, existing, report);
                    var structure = CopyStructure(source, host, target, (HostObjAttributes)existing, report);
                    return (existing, $"{count} parameters updated{structure}");
                default:
                    return (existing, $"{CopyParameters(source, item, target, existing, report)} parameters updated");
            }
        }

        /// <summary>
        ///     View templates and filters cannot be edited into another's state, so the source copy
        ///     replaces the existing one: the old element is renamed, the copy takes its name, views
        ///     (and view types' default templates) are re-pointed, and the old element is deleted.
        /// </summary>
        private static (Element element, string how) Replace(Document source, Element item, Document target, Element existing, CopyPasteOptions options)
        {
            var originalName = existing.Name;
            existing.Name = originalName + " (replaced " + DateTime.Now.Ticks + ")";
            var copy = CopyOne(source, item, target, options);
            if (!string.Equals(copy.Name, originalName, StringComparison.Ordinal))
                copy.Name = originalName;

            var repointed = 0;
            var problems = 0;
            var views = new FilteredElementCollector(target).OfClass(typeof(View)).Cast<View>().Where(v => v.Id != copy.Id).ToList();
            if (existing is View)
            {
                foreach (var view in views.Where(v => v.ViewTemplateId == existing.Id))
                {
                    view.ViewTemplateId = copy.Id;
                    repointed++;
                }
                foreach (var viewType in new FilteredElementCollector(target).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                             .Where(t => t.DefaultTemplateId == existing.Id))
                    viewType.DefaultTemplateId = copy.Id;
            }
            else
            {
                foreach (var view in views)
                {
                    try
                    {
                        if (!view.AreGraphicsOverridesAllowed() || !view.GetFilters().Contains(existing.Id))
                            continue;
                        var overrides = view.GetFilterOverrides(existing.Id);
                        var visible = view.GetFilterVisibility(existing.Id);
#if REVIT2022_OR_GREATER
                        var enabled = view.GetIsFilterEnabled(existing.Id);
#endif
                        view.AddFilter(copy.Id);
                        view.SetFilterOverrides(copy.Id, overrides);
                        view.SetFilterVisibility(copy.Id, visible);
#if REVIT2022_OR_GREATER
                        view.SetIsFilterEnabled(copy.Id, enabled);
#endif
                        view.RemoveFilter(existing.Id);
                        repointed++;
                    }
                    catch (Exception)
                    {
                        problems++;
                    }
                }
            }

            target.Delete(existing.Id);
            var how = $"replaced by the source version; {repointed} views re-pointed";
            if (existing is ParameterFilterElement)
                how += " (the filter moved to the end of each view's filter list)";
            if (problems > 0)
                how += $"; {problems} views could not be updated";
            return (copy, how);
        }

        private static readonly HashSet<BuiltInParameter> SkippedParameters = new HashSet<BuiltInParameter>
        {
            BuiltInParameter.SYMBOL_NAME_PARAM, BuiltInParameter.ALL_MODEL_TYPE_NAME, BuiltInParameter.ELEM_TYPE_PARAM,
            BuiltInParameter.ELEM_FAMILY_PARAM, BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM
        };

        /// <summary>Copies writable parameter values; element-id values are matched by name (copied when missing).</summary>
        private static int CopyParameters(Document source, Element from, Document target, Element to, KindReport report)
        {
            var count = 0;
            foreach (Parameter parameter in from.Parameters)
            {
                if (parameter.IsReadOnly || parameter.Definition == null || !parameter.HasValue)
                    continue;
                var bip = ProjectStyleUtils.BuiltIn(parameter);
                if (SkippedParameters.Contains(bip))
                    continue;
                var destination = bip != BuiltInParameter.INVALID
                    ? to.get_Parameter(bip)
                    : parameter.IsShared ? to.get_Parameter(parameter.GUID) : to.LookupParameter(parameter.Definition.Name);
                if (destination == null || destination.IsReadOnly || destination.StorageType != parameter.StorageType)
                    continue;

                try
                {
                    switch (parameter.StorageType)
                    {
                        case StorageType.Double:
                            destination.Set(parameter.AsDouble());
                            break;
                        case StorageType.Integer:
                            destination.Set(parameter.AsInteger());
                            break;
                        case StorageType.String:
                            destination.Set(parameter.AsString() ?? string.Empty);
                            break;
                        case StorageType.ElementId:
                            var mapped = MapId(source, parameter.AsElementId(), target, report);
                            if (mapped == null)
                            {
                                report.Notes.Add($"{to.Name}: '{parameter.Definition.Name}' kept (no matching element in the active project).");
                                continue;
                            }
                            destination.Set(mapped);
                            break;
                        default:
                            continue;
                    }
                    count++;
                }
                catch (Exception)
                {
                    report.Notes.Add($"{to.Name}: '{parameter.Definition.Name}' could not be set.");
                }
            }
            return count;
        }

        /// <summary>
        ///     The active-project id of the element a source id refers to, matched by class and name
        ///     (family symbols by family + type). Missing patterns, materials and types are copied.
        ///     Built-in/negative ids map to themselves. Null when nothing can be matched.
        /// </summary>
        private static ElementId MapId(Document source, ElementId id, Document target, KindReport report)
        {
            if (id == null || id == ElementId.InvalidElementId || id.GetValue() < 0)
                return id;
            var element = source.GetElement(id);
            if (element == null)
                return null;

            var match = new FilteredElementCollector(target).OfClass(element.GetType()).ToElements()
                .FirstOrDefault(e => string.Equals(e.Name, element.Name, StringComparison.OrdinalIgnoreCase)
                                     && (!(element is ElementType type) || string.Equals(((ElementType)e).FamilyName, type.FamilyName, StringComparison.OrdinalIgnoreCase)));
            if (match != null)
                return match.Id;

            if (element is ElementType || element is LinePatternElement || element is FillPatternElement || element is Material)
            {
                var options = new CopyPasteOptions();
                options.SetDuplicateTypeNamesHandler(new SourceDocumentUtils.UseDestinationTypes());
                var copy = CopyOne(source, element, target, options);
                report.Notes.Add($"Also copied '{element.Name}' ({element.GetType().Name}) needed by an overwritten item.");
                return copy.Id;
            }
            return null;
        }

        private static string CopyStructure(Document source, HostObjAttributes from, Document target, HostObjAttributes to, KindReport report)
        {
            if (from is WallType fromWall && to is WallType toWall && fromWall.Kind != toWall.Kind)
                return "; layers kept (different wall kind)";
            try
            {
                var structure = from.GetCompoundStructure();
                if (structure == null)
                    return string.Empty;
                for (var i = 0; i < structure.LayerCount; i++)
                    structure.SetMaterialId(i, MapId(source, structure.GetMaterialId(i), target, report) ?? ElementId.InvalidElementId);
                to.SetCompoundStructure(structure);
                return $"; {structure.LayerCount} layers replaced";
            }
            catch (Exception ex)
            {
                return "; layers kept (" + ex.Message + ")";
            }
        }

        private static void OverwriteMaterial(Document source, Material from, Document target, Material to, KindReport report)
        {
            to.Color = from.Color;
            to.Transparency = from.Transparency;
            to.Shininess = from.Shininess;
            to.Smoothness = from.Smoothness;
            to.UseRenderAppearanceForShading = from.UseRenderAppearanceForShading;
            to.MaterialClass = from.MaterialClass;
            to.MaterialCategory = from.MaterialCategory;
            to.SurfaceForegroundPatternId = MapId(source, from.SurfaceForegroundPatternId, target, report) ?? ElementId.InvalidElementId;
            to.SurfaceForegroundPatternColor = from.SurfaceForegroundPatternColor;
            to.SurfaceBackgroundPatternId = MapId(source, from.SurfaceBackgroundPatternId, target, report) ?? ElementId.InvalidElementId;
            to.SurfaceBackgroundPatternColor = from.SurfaceBackgroundPatternColor;
            to.CutForegroundPatternId = MapId(source, from.CutForegroundPatternId, target, report) ?? ElementId.InvalidElementId;
            to.CutForegroundPatternColor = from.CutForegroundPatternColor;
            to.CutBackgroundPatternId = MapId(source, from.CutBackgroundPatternId, target, report) ?? ElementId.InvalidElementId;
            to.CutBackgroundPatternColor = from.CutBackgroundPatternColor;
            if (from.AppearanceAssetId != ElementId.InvalidElementId)
            {
                try
                {
                    var asset = MapAppearance(source, from.AppearanceAssetId, target);
                    if (asset != null)
                        to.AppearanceAssetId = asset;
                }
                catch (Exception)
                {
                    report.Notes.Add($"{to.Name}: render appearance kept.");
                }
            }
        }

        private static ElementId MapAppearance(Document source, ElementId id, Document target)
        {
            var asset = source.GetElement(id);
            if (asset == null)
                return null;
            var match = new FilteredElementCollector(target).OfClass(typeof(AppearanceAssetElement)).ToElements()
                .FirstOrDefault(e => string.Equals(e.Name, asset.Name, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match.Id;
            var options = new CopyPasteOptions();
            options.SetDuplicateTypeNamesHandler(new SourceDocumentUtils.UseDestinationTypes());
            return CopyOne(source, asset, target, options).Id;
        }

        // ---------------------------------------------------------------- line and object styles

        private static void CopyLineStyles(Document source, Document target, Request request, string mode, KindReport report)
        {
            var styles = ProjectStyleUtils.LineStyles(source, request.Names);
            ReportUnmatched(request, styles.Select(s => s.Name), report);
            var lines = target.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);

            foreach (var style in styles)
            {
                var existing = lines.SubCategories.Cast<Category>().FirstOrDefault(c => string.Equals(c.Name, style.Name, StringComparison.OrdinalIgnoreCase));
                if (existing != null && mode != "overwrite")
                {
                    report.Skipped.Add(new JObject { ["name"] = style.Name, ["reason"] = "line style exists in the active project" });
                    continue;
                }

                using (var sub = new SubTransaction(target))
                {
                    sub.Start();
                    try
                    {
                        var category = existing ?? target.Settings.Categories.NewSubcategory(lines, style.Name);
                        var problems = ApplyGraphics(source, style, target, category, report);
                        sub.Commit();
                        var entry = ProjectStyleUtils.CategoryGraphics(target, category);
                        entry.AddFirst(new JProperty("name", category.Name));
                        if (problems.Count > 0)
                            entry["warnings"] = new JArray(problems);
                        (existing == null ? report.Copied : report.Overwritten).Add(entry);
                    }
                    catch (Exception ex)
                    {
                        if (sub.GetStatus() == TransactionStatus.Started)
                            sub.RollBack();
                        report.Failed.Add(new JObject { ["name"] = style.Name, ["reason"] = ex.Message });
                    }
                }
            }
        }

        /// <summary>
        ///     Object styles always exist in both projects, so their values are applied (onDuplicate
        ///     does not apply). Missing subcategories are created where Revit allows it.
        /// </summary>
        private static void ApplyObjectStyles(Document source, Document target, Request request, KindReport report)
        {
            var categories = ProjectStyleUtils.ObjectStyleCategories(source, request.Names);
            ReportUnmatched(request, categories.Select(c => c.Name).Concat(categories.Select(c => ProjectStyleUtils.BicOf(c)?.ToString() ?? "")), report);
            report.Notes.Add("objectStyles: category values are always applied; onDuplicate does not apply.");

            foreach (var category in categories)
            {
                var targetCategory = ProjectStyleUtils.FindCategory(target, category);
                if (targetCategory == null)
                {
                    report.Skipped.Add(new JObject { ["name"] = category.Name, ["reason"] = "category not in the active project" });
                    continue;
                }

                using (var sub = new SubTransaction(target))
                {
                    sub.Start();
                    try
                    {
                        var problems = ApplyGraphics(source, category, target, targetCategory, report);
                        var created = new JArray();
                        var missing = new JArray();
                        var updated = 0;
                        foreach (Category subcategory in category.SubCategories)
                        {
                            var targetSub = ProjectStyleUtils.FindCategory(target, subcategory);
                            if (targetSub == null)
                            {
                                if (!targetCategory.CanAddSubcategory)
                                {
                                    missing.Add(subcategory.Name);
                                    continue;
                                }
                                targetSub = target.Settings.Categories.NewSubcategory(targetCategory, subcategory.Name);
                                created.Add(subcategory.Name);
                            }
                            problems.AddRange(ApplyGraphics(source, subcategory, target, targetSub, report).Select(p => subcategory.Name + ": " + p));
                            updated++;
                        }
                        sub.Commit();

                        var entry = new JObject { ["name"] = category.Name, ["subcategoriesUpdated"] = updated };
                        if (created.Count > 0)
                            entry["subcategoriesCreated"] = created;
                        if (missing.Count > 0)
                            entry["subcategoriesNotInProject"] = missing;
                        if (problems.Count > 0)
                            entry["warnings"] = new JArray(problems.Take(20));
                        report.Overwritten.Add(entry);
                    }
                    catch (Exception ex)
                    {
                        if (sub.GetStatus() == TransactionStatus.Started)
                            sub.RollBack();
                        report.Failed.Add(new JObject { ["name"] = category.Name, ["reason"] = ex.Message });
                    }
                }
            }
        }

        /// <summary>Copies line weights, colour, line pattern and material of a category. Returns what could not be set.</summary>
        private static List<string> ApplyGraphics(Document source, Category from, Document target, Category to, KindReport report)
        {
            var problems = new List<string>();
            void Try(string what, Action action)
            {
                try
                {
                    action();
                }
                catch (Exception)
                {
                    problems.Add(what);
                }
            }

            var weight = SafeWeight(from, GraphicsStyleType.Projection);
            if (weight.HasValue)
                Try("weight", () => to.SetLineWeight(weight.Value, GraphicsStyleType.Projection));
            var cutWeight = SafeWeight(from, GraphicsStyleType.Cut);
            if (cutWeight.HasValue && SafeWeight(to, GraphicsStyleType.Cut).HasValue)
                Try("cutWeight", () => to.SetLineWeight(cutWeight.Value, GraphicsStyleType.Cut));
            Try("color", () =>
            {
                var color = from.LineColor;
                if (color != null && color.IsValid)
                    to.LineColor = new Color(color.Red, color.Green, color.Blue);
            });
            Try("pattern", () =>
            {
                var pattern = from.GetLinePatternId(GraphicsStyleType.Projection);
                if (pattern == null || pattern == ElementId.InvalidElementId)
                    return;
                var mapped = pattern == LinePatternElement.GetSolidPatternId() ? pattern : MapId(source, pattern, target, report);
                if (mapped != null)
                    to.SetLinePatternId(mapped, GraphicsStyleType.Projection);
            });
            if (from.Material != null)
            {
                Try("material", () =>
                {
                    var mapped = MapId(source, from.Material.Id, target, report);
                    if (mapped != null && target.GetElement(mapped) is Material material)
                        to.Material = material;
                });
            }
            return problems;
        }

        private static int? SafeWeight(Category category, GraphicsStyleType type)
        {
            try
            {
                return category.GetLineWeight(type);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- families

        private static void CopyFamilies(Document source, Document target, Request request, string mode, KindReport report, HashSet<long> reported)
        {
            var families = ProjectStyleUtils.Collect(source, request.Kind, request.Names, request.Categories).Cast<Family>().ToList();
            ReportUnmatched(request, families.Select(f => f.Name), report);
            foreach (var family in families)
            {
                try
                {
                    if (mode != "overwrite" && SourceDocumentUtils.FindFamily(target, family.Name) is Family present)
                    {
                        report.Skipped.Add(new JObject { ["name"] = family.Name, ["reason"] = "family exists in the active project", ["id"] = present.Id.GetValue() });
                        continue;
                    }

                    var result = SourceDocumentUtils.CopyFamily(source, target, family, mode == "overwrite", null);
                    reported.Add(result.Family.Id.GetValue());
                    var entry = new JObject
                    {
                        ["name"] = result.Family.Name,
                        ["id"] = result.Family.Id.GetValue(),
                        ["category"] = result.Family.FamilyCategory?.Name,
                        ["types"] = result.Family.GetFamilySymbolIds().Count
                    };
                    (result.AlreadyInProject ? report.Overwritten : report.Copied).Add(entry);
                }
                catch (Exception ex)
                {
                    report.Failed.Add(new JObject { ["name"] = family.Name, ["reason"] = ex.Message });
                }
            }
        }

        // ---------------------------------------------------------------- snapshot

        /// <summary>Ids of the standards-like elements, to report what came along as dependencies.</summary>
        private static HashSet<long> Snapshot(Document doc)
        {
            var ids = new HashSet<long>();
            foreach (var type in new FilteredElementCollector(doc).WhereElementIsElementType().ToElements())
                if (!(type is FamilySymbol))
                    ids.Add(type.Id.GetValue());
            foreach (var cls in new[] { typeof(Family), typeof(LinePatternElement), typeof(FillPatternElement), typeof(Material), typeof(ParameterFilterElement), typeof(SelectionFilterElement) })
                foreach (var id in new FilteredElementCollector(doc).OfClass(cls).ToElementIds())
                    ids.Add(id.GetValue());
            foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate))
                ids.Add(view.Id.GetValue());
            return ids;
        }
    }
}
