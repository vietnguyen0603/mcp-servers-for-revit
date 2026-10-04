using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Creates/updates line patterns, fill patterns, line styles and object
    ///     styles in one transaction (one sub-transaction per item so a failure
    ///     does not discard the others) and optionally lists the existing
    ///     standards. Sections run in the order linePatterns, fillPatterns,
    ///     lineStyles, objectStyles so styles can reference patterns created in
    ///     the same call. Line pattern and drafting fill sizes are paper mm,
    ///     model fill sizes are model mm; Revit stores both in feet.
    ///     The line weight (pen width) tables have no Revit API: a note is returned.
    /// </summary>
    public class ManageGraphicsStandardsEventHandler : JsonParameterEventHandler
    {
        private const string LineWeightsNote =
            "lineWeights not applied: the Revit API does not expose the Line Weights tables (model, perspective, annotation pen widths). " +
            "Set them in Manage > Additional Settings > Line Weights, or use Manage > Transfer Project Standards > Line Weights from an office template.";

        private static readonly string[] ListSections = { "linePatterns", "lineStyles", "fillPatterns", "objectStyles" };

        public override string GetName() => "Manage Graphics Standards";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var sections = new (string Key, Func<Document, JObject, JObject> Action)[]
            {
                ("linePatterns", ApplyLinePattern),
                ("fillPatterns", ApplyFillPattern),
                ("lineStyles", ApplyLineStyle),
                ("objectStyles", ApplyObjectStyle)
            };

            var response = new JObject();
            var notes = new JArray();
            var all = new List<JObject>();
            var hasWork = sections.Any(s => parameters[s.Key] is JArray a && a.Count > 0);
            var list = parameters.Value<bool?>("list") ?? false;

            if (parameters["lineWeights"] is JObject)
                notes.Add(LineWeightsNote);

            if (!hasWork && !list && notes.Count == 0)
                return Fail("Nothing to do: give linePatterns, lineStyles, fillPatterns, objectStyles, lineWeights or list=true.");

            if (hasWork)
            {
                if (doc.IsReadOnly)
                    return Fail("The document is read-only.");

                using (var transaction = DocumentationUtils.StartTransaction(doc, "MCP: Graphics Standards"))
                {
                    foreach (var (key, action) in sections)
                    {
                        if (!(parameters[key] is JArray items) || items.Count == 0)
                            continue;

                        var results = new JArray();
                        for (var index = 0; index < items.Count; index++)
                        {
                            var item = items[index] as JObject;
                            var result = RunItem(doc, item, action);
                            result.AddFirst(new JProperty("index", index));
                            results.Add(result);
                            all.Add(result);
                        }

                        response[key] = results;
                    }

                    var status = transaction.Commit();
                    if (status != TransactionStatus.Committed)
                    {
                        foreach (var result in all)
                        {
                            result["success"] = false;
                            result["action"] = "failed";
                            result["message"] = $"Transaction was not committed ({status}).";
                        }
                    }
                }
            }

            var succeeded = all.Count(r => r.Value<bool>("success"));
            var failed = all.Count - succeeded;
            response.AddFirst(new JProperty("failed", failed));
            response.AddFirst(new JProperty("succeeded", succeeded));
            if (notes.Count > 0)
                response["notes"] = notes;

            if (list)
                response["existing"] = BuildList(doc, parameters["listOptions"] as JObject);

            var message = hasWork
                ? $"Graphics standards: {Count(all, "created")} created, {Count(all, "updated")} updated, " +
                  $"{Count(all, "skipped")} skipped, {failed} failed."
                : list
                    ? "Listed graphics standards."
                    : "No changes made.";
            if (notes.Count > 0)
                message += " " + string.Join(" ", notes.Select(n => n.ToString()));
            return Ok(message, response);
        }

        private static int Count(IEnumerable<JObject> results, string action) =>
            results.Count(r => r.Value<string>("action") == action);

        private static JObject RunItem(Document doc, JObject item, Func<Document, JObject, JObject> action)
        {
            var name = item?.Value<string>("name") ?? item?.Value<string>("category");
            var subTransaction = new SubTransaction(doc);
            subTransaction.Start();
            try
            {
                if (item == null)
                    throw new ArgumentException("Item must be an object.");
                var result = action(doc, item);
                if (result.Value<string>("action") == "skipped")
                    subTransaction.RollBack();
                else
                    subTransaction.Commit();
                result.AddFirst(new JProperty("success", true));
                return result;
            }
            catch (Exception ex)
            {
                if (subTransaction.HasStarted())
                    subTransaction.RollBack();
                return new JObject
                {
                    ["success"] = false,
                    ["name"] = name,
                    ["action"] = "failed",
                    ["message"] = ex.Message
                };
            }
        }

        // ---------------------------------------------------------------- line patterns

        private static JObject ApplyLinePattern(Document doc, JObject item)
        {
            var name = RequireName(item, "name");
            if (IsSolidName(name))
                throw new ArgumentException("'Solid' is reserved for the built-in solid line pattern.");

            var segments = ReadLineSegments(item["segments"] as JArray);
            var existing = FindLinePattern(doc, name);
            if (existing != null)
            {
                var skip = HandleExisting(item, $"Line pattern '{existing.Name}'", existing.Id);
                if (skip != null)
                    return skip;

                var pattern = existing.GetLinePattern();
                pattern.SetSegments(segments);
                existing.SetLinePattern(pattern);
                return Done("updated", existing.Name, existing.Id);
            }

            var created = new LinePattern(name);
            created.SetSegments(segments);
            var element = LinePatternElement.Create(doc, created);
            return Done("created", element.Name, element.Id);
        }

        private static List<LinePatternSegment> ReadLineSegments(JArray array)
        {
            if (array == null || array.Count < 2 || array.Count % 2 != 0)
                throw new ArgumentException("'segments' must be an even number (>= 2) of alternating dash/dot and space segments.");

            var segments = new List<LinePatternSegment>();
            for (var i = 0; i < array.Count; i++)
            {
                var kind = array[i].Value<string>("kind")?.Trim().ToLowerInvariant();
                var lengthMm = array[i].Value<double?>("lengthMm") ?? 0;
                var isSpace = kind == "space";
                if (isSpace != (i % 2 == 1))
                    throw new ArgumentException($"segments[{i}]: segments must alternate dash/dot, space, dash/dot, space ...");

                LinePatternSegmentType type;
                switch (kind)
                {
                    case "dash":
                        type = LinePatternSegmentType.Dash;
                        break;
                    case "dot":
                        type = LinePatternSegmentType.Dot;
                        lengthMm = 0;
                        break;
                    case "space":
                        type = LinePatternSegmentType.Space;
                        break;
                    default:
                        throw new ArgumentException($"segments[{i}]: unknown kind '{kind}' (dash, space or dot).");
                }

                if (type != LinePatternSegmentType.Dot && !(lengthMm > 0))
                    throw new ArgumentException($"segments[{i}]: a {kind} needs lengthMm > 0.");
                segments.Add(new LinePatternSegment(type, DocumentationUtils.MmToFeet(lengthMm)));
            }

            return segments;
        }

        // ---------------------------------------------------------------- fill patterns

        private static JObject ApplyFillPattern(Document doc, JObject item)
        {
            var name = RequireName(item, "name");
            var targetText = item.Value<string>("target") ?? "drafting";
            FillPatternTarget target;
            if (string.Equals(targetText, "drafting", StringComparison.OrdinalIgnoreCase))
                target = FillPatternTarget.Drafting;
            else if (string.Equals(targetText, "model", StringComparison.OrdinalIgnoreCase))
                target = FillPatternTarget.Model;
            else
                throw new ArgumentException($"Unknown target '{targetText}' (drafting or model).");

            var orientation = ReadOrientation(item.Value<string>("hostOrientation"), target);
            var solid = item.Value<bool?>("solid") == true;
            var simple = item["simple"] as JObject;
            var grids = item["grids"] as JArray;
            if ((solid ? 1 : 0) + (simple != null ? 1 : 0) + (grids != null ? 1 : 0) != 1)
                throw new ArgumentException("Give exactly one of solid, simple or grids.");

            var patterns = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>().ToList();
            var existing = patterns.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.GetFillPattern().Target == target);

            if (solid)
                return ApplySolidFill(doc, item, name, target, orientation, patterns, existing);

            var pattern = simple != null
                ? BuildSimplePattern(name, target, orientation, simple)
                : BuildGridPattern(name, target, orientation, grids);

            var warnings = new JArray();
            var other = patterns.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.GetFillPattern().Target != target);
            if (existing == null && other != null)
                warnings.Add($"A {TargetName(other.GetFillPattern().Target)} pattern named '{other.Name}' also exists; this one is {TargetName(target)}.");

            JObject result;
            if (existing != null)
            {
                var skip = HandleExisting(item, $"Fill pattern '{existing.Name}' ({TargetName(target)})", existing.Id);
                if (skip != null)
                    return skip;
                if (existing.GetFillPattern().IsSolidFill)
                    throw new InvalidOperationException($"'{existing.Name}' is the solid fill pattern and cannot be redefined.");

                pattern.Name = existing.Name;
                existing.SetFillPattern(pattern);
                result = Done("updated", existing.Name, existing.Id);
            }
            else
            {
                var element = FillPatternElement.Create(doc, pattern);
                result = Done("created", element.Name, element.Id);
            }

            result["target"] = TargetName(target);
            result["gridCount"] = pattern.GridCount;
            if (warnings.Count > 0)
                result["warnings"] = warnings;
            return result;
        }

        /// <summary>
        ///     Revit projects have one built-in solid fill; reuse it instead of
        ///     creating a duplicate (solid fills are coloured via the filled
        ///     region type or override, not the pattern).
        /// </summary>
        private static JObject ApplySolidFill(Document doc, JObject item, string name, FillPatternTarget target,
            FillPatternHostOrientation orientation, List<FillPatternElement> patterns, FillPatternElement existing)
        {
            if (existing != null)
            {
                if (!existing.GetFillPattern().IsSolidFill)
                {
                    var skip = HandleExisting(item, $"Fill pattern '{existing.Name}' ({TargetName(target)})", existing.Id);
                    if (skip != null)
                        return skip;
                    throw new InvalidOperationException(
                        $"'{existing.Name}' is a hatch pattern; it cannot be turned into a solid fill. Use the built-in solid fill instead.");
                }

                var same = Done("skipped", existing.Name, existing.Id);
                same["reason"] = "Already a solid fill.";
                same["target"] = TargetName(target);
                return same;
            }

            var builtIn = patterns.FirstOrDefault(p => p.GetFillPattern().IsSolidFill);
            if (builtIn != null)
            {
                var reuse = Done("skipped", builtIn.Name, builtIn.Id);
                reuse["reason"] = $"Revit has a single built-in solid fill '{builtIn.Name}' usable for drafting and model; use it (with a colour on the filled region type / override) instead of '{name}'.";
                reuse["target"] = TargetName(builtIn.GetFillPattern().Target);
                return reuse;
            }

            var pattern = new FillPattern(name, target, orientation);
            var element = FillPatternElement.Create(doc, pattern);
            var created = Done("created", element.Name, element.Id);
            created["target"] = TargetName(target);
            if (!element.GetFillPattern().IsSolidFill)
                created["warnings"] = new JArray("Revit did not report the new pattern as a solid fill; check it in Manage > Fill Patterns.");
            return created;
        }

        private static FillPattern BuildSimplePattern(string name, FillPatternTarget target,
            FillPatternHostOrientation orientation, JObject simple)
        {
            var angle = DegToRad(simple.Value<double?>("angleDeg") ?? 0);
            var spacing = ReadPositiveMm(simple, "spacingMm");
            if (simple.Value<bool?>("crosshatch") == true)
            {
                var cross = simple["crossSpacingMm"] != null ? ReadPositiveMm(simple, "crossSpacingMm") : spacing;
                return new FillPattern(name, target, orientation, angle, DocumentationUtils.MmToFeet(spacing),
                    DocumentationUtils.MmToFeet(cross));
            }

            return new FillPattern(name, target, orientation, angle, DocumentationUtils.MmToFeet(spacing));
        }

        private static FillPattern BuildGridPattern(string name, FillPatternTarget target,
            FillPatternHostOrientation orientation, JArray grids)
        {
            if (grids.Count == 0)
                throw new ArgumentException("'grids' must not be empty.");

            var fillGrids = new List<FillGrid>();
            for (var i = 0; i < grids.Count; i++)
            {
                if (!(grids[i] is JObject g))
                    throw new ArgumentException($"grids[{i}] must be an object.");

                var origin = g["originMm"] as JObject;
                var segments = (g["segments"] as JArray)?.Select(v => v.Value<double>()).ToList() ?? new List<double>();
                if (segments.Count % 2 != 0 || segments.Any(v => !(v > 0)))
                    throw new ArgumentException($"grids[{i}].segments must be an even number of positive dash/space lengths.");

                var grid = new FillGrid
                {
                    Angle = DegToRad(g.Value<double?>("angleDeg") ?? 0),
                    Origin = new UV(DocumentationUtils.MmToFeet(origin?.Value<double?>("x") ?? 0),
                        DocumentationUtils.MmToFeet(origin?.Value<double?>("y") ?? 0)),
                    Offset = DocumentationUtils.MmToFeet(ReadPositiveMm(g, "offsetMm")),
                    Shift = DocumentationUtils.MmToFeet(g.Value<double?>("shiftMm") ?? 0)
                };
                if (segments.Count > 0)
                    grid.SetSegments(segments.Select(DocumentationUtils.MmToFeet).ToList());
                fillGrids.Add(grid);
            }

            var pattern = new FillPattern(name, target, orientation);
            pattern.SetFillGrids(fillGrids);
            return pattern;
        }

        private static FillPatternHostOrientation ReadOrientation(string value, FillPatternTarget target)
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case null:
                case "":
                    return target == FillPatternTarget.Drafting ? FillPatternHostOrientation.ToView : FillPatternHostOrientation.ToHost;
                case "toview":
                    return FillPatternHostOrientation.ToView;
                case "tohost":
                    return FillPatternHostOrientation.ToHost;
                case "astext":
                    return FillPatternHostOrientation.AsText;
                default:
                    throw new ArgumentException($"Unknown hostOrientation '{value}' (toView, toHost or asText).");
            }
        }

        // ---------------------------------------------------------------- line styles

        private static JObject ApplyLineStyle(Document doc, JObject item)
        {
            var name = RequireName(item, "name");
            var lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines)
                        ?? throw new InvalidOperationException("The Lines category is not available in this document.");

            // Resolve inputs before creating anything.
            var weight = ReadPen(item, "weight");
            var color = ReadColor(item, "color");
            var patternName = item.Value<string>("pattern");
            var patternId = patternName != null ? ResolveLinePattern(doc, patternName) : null;

            var existing = lines.SubCategories.Cast<Category>()
                .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            string action;
            Category category;
            if (existing != null)
            {
                var styleId = existing.GetGraphicsStyle(GraphicsStyleType.Projection)?.Id ?? existing.Id;
                var skip = HandleExisting(item, $"Line style '{existing.Name}'", styleId);
                if (skip != null)
                    return skip;
                category = existing;
                action = "updated";
            }
            else
            {
                if (!lines.CanAddSubcategory)
                    throw new InvalidOperationException("Line styles cannot be added to this document.");
                category = doc.Settings.Categories.NewSubcategory(lines, name);
                action = "created";
            }

            if (weight != null)
                category.SetLineWeight(weight.Value, GraphicsStyleType.Projection);
            if (color != null)
                category.LineColor = color;
            if (patternId != null)
                category.SetLinePatternId(patternId, GraphicsStyleType.Projection);

            var style = category.GetGraphicsStyle(GraphicsStyleType.Projection);
            var result = Done(action, category.Name, style?.Id ?? category.Id);
            AddStyleValues(doc, category, result, false);
            return result;
        }

        // ---------------------------------------------------------------- object styles

        private static JObject ApplyObjectStyle(Document doc, JObject item)
        {
            var path = RequireName(item, "category");
            var category = CategoryNameUtils.ResolveCategoryPath(doc, path)
                           ?? throw new ArgumentException(
                               $"Category '{path}' not found.{CategoryNameUtils.SuggestCategory(doc, path)} Use a category name or OST_ name, or 'Category/Subcategory' for a subcategory (subcategories are not created here; '<>' around names is optional).");

            var projection = ReadPen(item, "projectionWeight");
            var cut = ReadPen(item, "cutWeight");
            var color = ReadColor(item, "color");
            var patternName = item.Value<string>("pattern");
            var patternId = patternName != null ? ResolveLinePattern(doc, patternName) : null;
            var materialName = item.Value<string>("material")?.Trim();
            Material material = null;
            if (!string.IsNullOrEmpty(materialName))
            {
                material = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                               .FirstOrDefault(m => string.Equals(m.Name, materialName, StringComparison.OrdinalIgnoreCase))
                           ?? throw new ArgumentException($"Material '{materialName}' not found.");
            }

            var warnings = new JArray();
            if (projection != null)
                category.SetLineWeight(projection.Value, GraphicsStyleType.Projection);
            if (cut != null)
            {
                if (!IsCuttable(category))
                    warnings.Add($"'{category.Name}' is not a cuttable category; the cut weight may have no effect.");
                try
                {
                    category.SetLineWeight(cut.Value, GraphicsStyleType.Cut);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Cut weight not set: {ex.Message}");
                }
            }

            if (color != null)
                category.LineColor = color;
            if (patternId != null)
                category.SetLinePatternId(patternId, GraphicsStyleType.Projection);
            if (material != null)
                category.Material = material;

            var result = Done("updated", category.Parent != null ? $"{category.Parent.Name}/{category.Name}" : category.Name,
                category.Id);
            AddStyleValues(doc, category, result, true);
            if (warnings.Count > 0)
                result["warnings"] = warnings;
            return result;
        }

        private static bool IsCuttable(Category category)
        {
            try
            {
                return category.IsCuttable || (category.Parent?.IsCuttable ?? false);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- list

        private static JObject BuildList(Document doc, JObject options)
        {
            var requested = options?["sections"] is JArray s && s.Count > 0
                ? s.Select(t => t.ToString()).ToList()
                : ListSections.ToList();
            var unknown = requested.Where(k => !ListSections.Contains(k)).ToList();
            if (unknown.Count > 0)
                throw new ArgumentException($"Unknown list sections: {string.Join(", ", unknown)}.");

            var nameContains = options?.Value<string>("nameContains")?.Trim();
            bool Matches(string name) => string.IsNullOrEmpty(nameContains)
                                         || (name != null && name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0);

            var result = new JObject();
            if (requested.Contains("linePatterns"))
            {
                result["linePatterns"] = new JArray(new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement))
                    .Cast<LinePatternElement>()
                    .Where(p => Matches(p.Name))
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new JObject
                    {
                        ["id"] = p.Id.GetValue(),
                        ["name"] = p.Name,
                        ["segments"] = new JArray(p.GetLinePattern().GetSegments().Select(seg => new JObject
                        {
                            ["kind"] = seg.Type.ToString().ToLowerInvariant(),
                            ["lengthMm"] = Math.Round(seg.Length * DocumentationUtils.MmPerFoot, 3)
                        }))
                    }));
            }

            if (requested.Contains("lineStyles"))
            {
                var lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                result["lineStyles"] = lines == null
                    ? new JArray()
                    : new JArray(lines.SubCategories.Cast<Category>()
                        .Where(c => Matches(c.Name))
                        .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(c =>
                        {
                            var entry = new JObject
                            {
                                ["id"] = c.GetGraphicsStyle(GraphicsStyleType.Projection)?.Id.GetValue(),
                                ["name"] = c.Name
                            };
                            AddStyleValues(doc, c, entry, false);
                            return entry;
                        }));
            }

            if (requested.Contains("fillPatterns"))
            {
                result["fillPatterns"] = new JArray(new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement))
                    .Cast<FillPatternElement>()
                    .Where(p => Matches(p.Name))
                    .Select(p => new { Element = p, Pattern = p.GetFillPattern() })
                    .OrderBy(p => p.Pattern.Target)
                    .ThenBy(p => p.Element.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new JObject
                    {
                        ["id"] = p.Element.Id.GetValue(),
                        ["name"] = p.Element.Name,
                        ["target"] = TargetName(p.Pattern.Target),
                        ["solid"] = p.Pattern.IsSolidFill,
                        ["gridCount"] = p.Pattern.GridCount,
                        ["hostOrientation"] = p.Pattern.HostOrientation.ToString()
                    }));
            }

            if (requested.Contains("objectStyles"))
            {
                var includeSubs = options?.Value<bool?>("includeSubcategories") ?? false;
                var includeAnnotation = options?.Value<bool?>("includeAnnotationCategories") ?? false;
                var styles = new JArray();
                foreach (var category in doc.Settings.Categories.Cast<Category>()
                             .Where(c => c.CategoryType == CategoryType.Model ||
                                         (includeAnnotation && c.CategoryType == CategoryType.Annotation))
                             .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var subs = includeSubs
                        ? category.SubCategories.Cast<Category>()
                            .Where(c => Matches(c.Name) || Matches(category.Name))
                            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList()
                        : new List<Category>();
                    if (!Matches(category.Name) && subs.Count == 0)
                        continue;

                    var entry = new JObject
                    {
                        ["id"] = category.Id.GetValue(),
                        ["name"] = category.Name,
                        ["type"] = category.CategoryType.ToString()
                    };
                    var builtIn = BuiltInName(category);
                    if (builtIn != null)
                        entry["builtIn"] = builtIn;
                    AddStyleValues(doc, category, entry, true);
                    if (includeSubs)
                    {
                        entry["subcategories"] = new JArray(subs.Select(sub =>
                        {
                            var subEntry = new JObject { ["id"] = sub.Id.GetValue(), ["name"] = sub.Name };
                            AddStyleValues(doc, sub, subEntry, true);
                            return subEntry;
                        }));
                    }

                    styles.Add(entry);
                }

                result["objectStyles"] = styles;
            }

            var counts = new JObject();
            foreach (var property in result.Properties().ToList())
                counts[property.Name] = (property.Value as JArray)?.Count ?? 0;
            result.AddFirst(new JProperty("counts", counts));
            return result;
        }

        private static string BuiltInName(Category category)
        {
            var value = category.Id.GetValue();
            if (value >= 0)
                return null;
            var bic = (BuiltInCategory)value;
            return Enum.IsDefined(typeof(BuiltInCategory), bic) ? bic.ToString() : null;
        }

        /// <summary>Adds weight(s), colour, pattern and material of a category to a JSON entry.</summary>
        private static void AddStyleValues(Document doc, Category category, JObject entry, bool objectStyle)
        {
            try
            {
                if (objectStyle)
                {
                    entry["projectionWeight"] = category.GetLineWeight(GraphicsStyleType.Projection);
                    if (IsCuttable(category))
                        entry["cutWeight"] = category.GetLineWeight(GraphicsStyleType.Cut);
                }
                else
                {
                    entry["weight"] = category.GetLineWeight(GraphicsStyleType.Projection);
                }

                var color = category.LineColor;
                if (color != null && color.IsValid)
                    entry["color"] = new JArray(color.Red, color.Green, color.Blue);

                entry["pattern"] = LinePatternName(doc, category.GetLinePatternId(GraphicsStyleType.Projection));

                if (objectStyle && category.Material != null)
                    entry["material"] = category.Material.Name;
            }
            catch (Exception)
            {
                // Some internal categories do not expose every style value; keep what was read.
            }
        }

        private static string LinePatternName(Document doc, ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId || id == LinePatternElement.GetSolidPatternId())
                return "Solid";
            return doc.GetElement(id)?.Name ?? "Solid";
        }

        // ---------------------------------------------------------------- helpers

        private static JObject HandleExisting(JObject item, string label, ElementId id)
        {
            var mode = (item.Value<string>("ifExists") ?? "update").Trim().ToLowerInvariant();
            switch (mode)
            {
                case "update":
                    return null;
                case "skip":
                    var skipped = Done("skipped", item.Value<string>("name"), id);
                    skipped["reason"] = $"{label} already exists.";
                    return skipped;
                case "error":
                    throw new InvalidOperationException($"{label} already exists (ifExists = error).");
                default:
                    throw new ArgumentException($"Unknown ifExists '{mode}' (update, skip or error).");
            }
        }

        private static JObject Done(string action, string name, ElementId id) => new JObject
        {
            ["name"] = name,
            ["action"] = action,
            ["id"] = id?.GetValue()
        };

        private static string RequireName(JObject item, string key)
        {
            var name = item.Value<string>(key)?.Trim();
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException($"'{key}' is required.");
            return name;
        }

        private static bool IsSolidName(string name) =>
            string.Equals(name.Trim(), "Solid", StringComparison.OrdinalIgnoreCase);

        private static LinePatternElement FindLinePattern(Document doc, string name) =>
            new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>()
                .FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

        private static ElementId ResolveLinePattern(Document doc, string name)
        {
            if (IsSolidName(name))
                return LinePatternElement.GetSolidPatternId();
            return FindLinePattern(doc, name)?.Id
                   ?? throw new ArgumentException($"Line pattern '{name}' not found (create it in linePatterns in the same call).");
        }

        private static int? ReadPen(JObject item, string key)
        {
            var value = item.Value<int?>(key);
            if (value != null && (value < 1 || value > 16))
                throw new ArgumentException($"'{key}' must be a pen number between 1 and 16.");
            return value;
        }

        private static Color ReadColor(JObject item, string key)
        {
            if (item[key] == null || item[key].Type == JTokenType.Null)
                return null;
            if (!(item[key] is JArray rgb) || rgb.Count != 3)
                throw new ArgumentException($"'{key}' must be [r, g, b].");
            var values = rgb.Select(v => v.Value<int>()).ToArray();
            if (values.Any(v => v < 0 || v > 255))
                throw new ArgumentException($"'{key}' components must be between 0 and 255.");
            return new Color((byte)values[0], (byte)values[1], (byte)values[2]);
        }

        private static double ReadPositiveMm(JObject item, string key)
        {
            var value = item.Value<double?>(key);
            if (value == null || !(value > 0) || double.IsInfinity(value.Value))
                throw new ArgumentException($"'{key}' must be a positive length in mm.");
            return value.Value;
        }

        private static double DegToRad(double degrees) => degrees * Math.PI / 180.0;

        private static string TargetName(FillPatternTarget target) =>
            target == FillPatternTarget.Drafting ? "drafting" : "model";
    }
}
