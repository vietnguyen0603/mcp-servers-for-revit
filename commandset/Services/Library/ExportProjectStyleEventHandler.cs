using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Library
{
    /// <summary>
    ///     Read-only: writes the drawing standards of the active document (or another open project /
    ///     a .rvt/.rte opened in the background) to a JSON "project style profile" (see
    ///     docs/project-style-profile.md): line/fill patterns, line styles, object styles, arrowheads,
    ///     text/dimension/grid/level/viewport types, filters, view templates with their overrides,
    ///     annotation families and default types. Lengths in mm, angles in degrees, colours [r,g,b].
    /// </summary>
    public class ExportProjectStyleEventHandler : JsonParameterEventHandler
    {
        public const string SchemaId = "revit-mcp.project-style/1";

        private static readonly string[] DefaultSections =
        {
            "linePatterns", "fillPatterns", "lineStyles", "objectStyles", "arrowheads", "textTypes", "dimensionTypes",
            "gridTypes", "levelTypes", "viewportTypes", "viewFilters", "viewTemplates", "tagFamilies", "titleBlocks",
            "annotationSymbols", "otherAnnotationFamilies", "defaultTypes"
        };

        private static readonly string[] OptionalSections = { "materials", "wallTypes", "floorTypes" };

        public override string GetName() => "Export Project Style";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var active = uiDoc.Document;
            var sections = ParseSections(parameters["sections"] as JArray);
            var includeParameters = parameters.Value<bool?>("includeParameters") ?? true;
            var returnProfile = parameters.Value<bool?>("returnProfile") ?? false;

            var doc = SourceDocumentUtils.Open(active, parameters.Value<string>("sourceDocument"), parameters.Value<string>("sourcePath"), out var openedHere, allowActive: true);
            try
            {
                if (doc.IsFamilyDocument)
                    throw new ArgumentException("Export a project or project template, not a family document.");

                var profile = new JObject
                {
                    ["schema"] = SchemaId,
                    ["units"] = new JObject { ["length"] = "mm", ["angle"] = "deg", ["color"] = "[r,g,b] 0-255", ["lineWeight"] = "pen number 1-16" },
                    ["source"] = new JObject
                    {
                        ["title"] = doc.Title,
                        ["path"] = string.IsNullOrEmpty(doc.PathName) ? null : doc.PathName,
                        ["revitVersion"] = doc.Application.VersionNumber,
                        ["exportedAt"] = DateTime.UtcNow.ToString("o")
                    }
                };

                foreach (var section in sections)
                    profile[section] = Export(doc, section, includeParameters);

                var outFile = ResolveOutFile(parameters.Value<string>("outFile"), doc);
                var json = profile.ToString(Formatting.Indented);
                System.IO.File.WriteAllText(outFile, json);

                var counts = new JObject();
                foreach (var section in sections)
                    counts[section] = profile[section] is JArray array ? array.Count : 0;

                var response = new JObject
                {
                    ["outFile"] = outFile,
                    ["sizeKb"] = Math.Round(json.Length / 1024.0, 1),
                    ["schema"] = SchemaId,
                    ["source"] = doc.Title,
                    ["closedAfterExport"] = openedHere,
                    ["counts"] = counts,
                    ["viewTemplates"] = Names(profile["viewTemplates"], 40),
                    ["textTypes"] = Names(profile["textTypes"], 40),
                    ["dimensionTypes"] = Names(profile["dimensionTypes"], 40),
                    ["lineStyles"] = Names(profile["lineStyles"], 60)
                };
                if (returnProfile)
                    response["profile"] = profile;
                return Ok($"Exported the drawing style of '{doc.Title}' to {outFile}.", response);
            }
            finally
            {
                if (openedHere)
                    doc.Close(false);
            }
        }

        private static List<string> ParseSections(JArray requested)
        {
            if (requested == null || requested.Count == 0)
                return DefaultSections.ToList();
            var all = DefaultSections.Concat(OptionalSections).ToList();
            var sections = new List<string>();
            foreach (var token in requested)
            {
                var name = all.FirstOrDefault(s => string.Equals(s, token.ToString().Trim(), StringComparison.OrdinalIgnoreCase))
                           ?? throw new ArgumentException($"Unknown section '{token}'. Sections: {string.Join(", ", all)}.");
                if (!sections.Contains(name))
                    sections.Add(name);
            }
            return sections;
        }

        private static string ResolveOutFile(string outFile, Document doc)
        {
            if (string.IsNullOrWhiteSpace(outFile))
            {
                var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "revit-mcp");
                outFile = System.IO.Path.Combine(folder, SourceDocumentUtils.SafeFileName(System.IO.Path.GetFileNameWithoutExtension(doc.Title)) + "-style.json");
            }
            else if (!outFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                outFile += ".json";
            }
            outFile = System.IO.Path.GetFullPath(outFile);
            var directory = System.IO.Path.GetDirectoryName(outFile);
            if (!string.IsNullOrEmpty(directory))
                System.IO.Directory.CreateDirectory(directory);
            return outFile;
        }

        private static JArray Names(JToken section, int max)
        {
            if (!(section is JArray array))
                return null;
            var names = array.OfType<JObject>().Select(o => o.Value<string>("name")).Where(n => n != null).ToList();
            var result = new JArray(names.Take(max));
            if (names.Count > max)
                result.Add($"... {names.Count - max} more");
            return result;
        }

        private static JArray Export(Document doc, string section, bool includeParameters)
        {
            switch (section)
            {
                case "linePatterns": return LinePatterns(doc);
                case "fillPatterns": return FillPatterns(doc);
                case "lineStyles": return LineStyles(doc);
                case "objectStyles": return ObjectStyles(doc);
                case "materials": return Materials(doc);
                case "arrowheads": return Types(doc, section, includeParameters, Arrowhead);
                case "textTypes": return Types(doc, section, includeParameters, TextType);
                case "dimensionTypes": return Types(doc, section, includeParameters, DimensionTypeEntry);
                case "gridTypes": return Types(doc, section, includeParameters, GridTypeEntry);
                case "levelTypes": return Types(doc, section, includeParameters, LevelTypeEntry);
                case "viewportTypes": return Types(doc, section, includeParameters, ViewportTypeEntry);
                case "wallTypes":
                case "floorTypes": return Types(doc, section, includeParameters, HostType);
                case "viewFilters": return Filters(doc);
                case "viewTemplates": return ViewTemplates(doc, includeParameters);
                case "tagFamilies": return FamiliesByCategory(doc, f => ProjectStyleUtils.IsTagCategory(f.FamilyCategory));
                case "titleBlocks": return FamilyList(doc, "titleBlocks");
                case "annotationSymbols": return FamilyList(doc, "annotationSymbols");
                case "otherAnnotationFamilies":
                    return FamiliesByCategory(doc, f => f.FamilyCategory != null && f.FamilyCategory.CategoryType == CategoryType.Annotation
                                                        && !ProjectStyleUtils.IsTagCategory(f.FamilyCategory)
                                                        && ProjectStyleUtils.BicOf(f.FamilyCategory) != BuiltInCategory.OST_TitleBlocks
                                                        && ProjectStyleUtils.BicOf(f.FamilyCategory) != BuiltInCategory.OST_GenericAnnotation);
                case "defaultTypes": return DefaultTypes(doc);
                default: return new JArray();
            }
        }

        // ---------------------------------------------------------------- patterns and styles

        private static JArray LinePatterns(Document doc)
        {
            var result = new JArray();
            foreach (var element in ProjectStyleUtils.Collect(doc, "linePatterns").Cast<LinePatternElement>())
            {
                var segments = new JArray(element.GetLinePattern().GetSegments().Select(s => new JObject
                {
                    ["type"] = s.Type.ToString().ToLowerInvariant(),
                    ["lengthMm"] = DocumentationUtils.FeetToMm(s.Length)
                }));
                result.Add(new JObject { ["name"] = element.Name, ["segments"] = segments });
            }
            return result;
        }

        private static JArray FillPatterns(Document doc)
        {
            var result = new JArray();
            foreach (var element in ProjectStyleUtils.Collect(doc, "fillPatterns").Cast<FillPatternElement>())
            {
                var pattern = element.GetFillPattern();
                var entry = new JObject
                {
                    ["name"] = element.Name,
                    ["target"] = pattern.Target == FillPatternTarget.Model ? "model" : "drafting",
                    ["solid"] = pattern.IsSolidFill
                };
                if (!pattern.IsSolidFill)
                {
                    entry["hostOrientation"] = pattern.HostOrientation.ToString();
                    entry["grids"] = new JArray(pattern.GetFillGrids().Select(g => new JObject
                    {
                        ["angleDeg"] = Math.Round(g.Angle * 180.0 / Math.PI, 4),
                        ["originMm"] = new JArray(DocumentationUtils.FeetToMm(g.Origin.U), DocumentationUtils.FeetToMm(g.Origin.V)),
                        ["offsetMm"] = DocumentationUtils.FeetToMm(g.Offset),
                        ["shiftMm"] = DocumentationUtils.FeetToMm(g.Shift),
                        ["segmentsMm"] = new JArray(g.GetSegments().Select(DocumentationUtils.FeetToMm))
                    }));
                }
                result.Add(entry);
            }
            return result;
        }

        private static JArray LineStyles(Document doc)
        {
            var result = new JArray();
            foreach (var style in ProjectStyleUtils.LineStyles(doc))
            {
                var entry = ProjectStyleUtils.CategoryGraphics(doc, style);
                entry.Remove("cutWeight");
                entry.AddFirst(new JProperty("name", style.Name));
                if (style.Id.GetValue() < 0)
                    entry["builtIn"] = true;
                result.Add(entry);
            }
            return result;
        }

        private static JArray ObjectStyles(Document doc)
        {
            var result = new JArray();
            foreach (var category in ProjectStyleUtils.ObjectStyleCategories(doc))
            {
                var entry = ProjectStyleUtils.CategoryGraphics(doc, category);
                entry.AddFirst(new JProperty("type", category.CategoryType == CategoryType.Model ? "model" : "annotation"));
                if (ProjectStyleUtils.BicOf(category) is BuiltInCategory bic)
                    entry.AddFirst(new JProperty("builtInCategory", bic.ToString()));
                entry.AddFirst(new JProperty("category", category.Name));
                var subcategories = new JArray();
                foreach (var sub in category.SubCategories.Cast<Category>().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var subEntry = ProjectStyleUtils.CategoryGraphics(doc, sub);
                    subEntry.AddFirst(new JProperty("name", sub.Name));
                    subcategories.Add(subEntry);
                }
                if (subcategories.Count > 0)
                    entry["subcategories"] = subcategories;
                result.Add(entry);
            }
            return result;
        }

        private static JArray Materials(Document doc)
        {
            var result = new JArray();
            foreach (var material in ProjectStyleUtils.Collect(doc, "materials").Cast<Material>())
            {
                var entry = new JObject
                {
                    ["name"] = material.Name,
                    ["class"] = material.MaterialClass,
                    ["color"] = ProjectStyleUtils.Rgb(material.Color),
                    ["transparency"] = material.Transparency
                };
                AddFill(doc, entry, "surfaceForeground", material.SurfaceForegroundPatternId, material.SurfaceForegroundPatternColor);
                AddFill(doc, entry, "surfaceBackground", material.SurfaceBackgroundPatternId, material.SurfaceBackgroundPatternColor);
                AddFill(doc, entry, "cutForeground", material.CutForegroundPatternId, material.CutForegroundPatternColor);
                AddFill(doc, entry, "cutBackground", material.CutBackgroundPatternId, material.CutBackgroundPatternColor);
                result.Add(entry);
            }
            return result;
        }

        private static void AddFill(Document doc, JObject entry, string prefix, ElementId patternId, Color color)
        {
            if (ProjectStyleUtils.ElementName(doc, patternId) is string pattern)
            {
                entry[prefix + "Pattern"] = pattern;
                if (ProjectStyleUtils.Rgb(color) is JArray rgb)
                    entry[prefix + "Color"] = rgb;
            }
        }

        // ---------------------------------------------------------------- types

        private static JArray Types(Document doc, string kind, bool includeParameters, Action<Document, ElementType, JObject> describe)
        {
            var result = new JArray();
            foreach (var type in ProjectStyleUtils.Collect(doc, kind).OfType<ElementType>())
            {
                var entry = new JObject { ["name"] = type.Name };
                try
                {
                    describe(doc, type, entry);
                }
                catch (Exception ex)
                {
                    entry["readError"] = ex.Message;
                }
                if (includeParameters)
                    entry["parameters"] = ProjectStyleUtils.WritableParameters(doc, type);
                result.Add(entry);
            }
            return result;
        }

        private static void Put(JObject entry, string name, JToken value)
        {
            if (value != null && value.Type != JTokenType.Null)
                entry[name] = value;
        }

        private static void PutBip(Document doc, ElementType type, JObject entry, string name, BuiltInParameter bip)
        {
            Put(entry, name, ProjectStyleUtils.BuiltInValue(doc, type, bip));
        }

        private static void PutText(ElementType type, JObject entry, string name, BuiltInParameter bip)
        {
            var parameter = type.get_Parameter(bip);
            if (parameter != null && parameter.HasValue)
                Put(entry, name, parameter.AsValueString() ?? parameter.AsString());
        }

        private static void PutBool(ElementType type, JObject entry, string name, BuiltInParameter bip)
        {
            var parameter = type.get_Parameter(bip);
            if (parameter != null && parameter.HasValue && parameter.StorageType == StorageType.Integer)
                entry[name] = parameter.AsInteger() != 0;
        }

        private static void PutColor(ElementType type, JObject entry, string name, BuiltInParameter bip)
        {
            var parameter = type.get_Parameter(bip);
            if (parameter != null && parameter.HasValue && parameter.StorageType == StorageType.Integer)
                entry[name] = ProjectStyleUtils.RgbFromInt(parameter.AsInteger());
        }

        private static void Arrowhead(Document doc, ElementType type, JObject entry)
        {
            PutText(type, entry, "style", BuiltInParameter.ARROW_TYPE);
            PutBip(doc, type, entry, "tickSizeMm", BuiltInParameter.ARROW_SIZE);
            PutBool(type, entry, "filled", BuiltInParameter.ARROW_FILLED);
        }

        private static void TextType(Document doc, ElementType type, JObject entry)
        {
            PutBip(doc, type, entry, "font", BuiltInParameter.TEXT_FONT);
            PutBip(doc, type, entry, "textSizeMm", BuiltInParameter.TEXT_SIZE);
            PutBool(type, entry, "bold", BuiltInParameter.TEXT_STYLE_BOLD);
            PutBool(type, entry, "italic", BuiltInParameter.TEXT_STYLE_ITALIC);
            PutBool(type, entry, "underline", BuiltInParameter.TEXT_STYLE_UNDERLINE);
            PutBip(doc, type, entry, "widthFactor", BuiltInParameter.TEXT_WIDTH_SCALE);
            PutColor(type, entry, "color", BuiltInParameter.LINE_COLOR);
            PutBip(doc, type, entry, "weight", BuiltInParameter.LINE_PEN);
            var background = type.get_Parameter(BuiltInParameter.TEXT_BACKGROUND);
            if (background != null && background.HasValue)
                entry["background"] = background.AsInteger() == 0 ? "opaque" : "transparent";
            PutBool(type, entry, "showBorder", BuiltInParameter.TEXT_BOX_VISIBILITY);
            PutBip(doc, type, entry, "leaderArrowhead", BuiltInParameter.LEADER_ARROWHEAD);
            PutBip(doc, type, entry, "tabSizeMm", BuiltInParameter.TEXT_TAB_SIZE);
            PutBip(doc, type, entry, "leaderBorderOffsetMm", BuiltInParameter.LEADER_OFFSET_SHEET);
        }

        private static void DimensionTypeEntry(Document doc, ElementType type, JObject entry)
        {
            var dimensionType = (DimensionType)type;
            entry["styleType"] = dimensionType.StyleType.ToString();
            entry["family"] = type.FamilyName;
            PutBip(doc, type, entry, "font", BuiltInParameter.TEXT_FONT);
            PutBip(doc, type, entry, "textSizeMm", BuiltInParameter.TEXT_SIZE);
            PutBip(doc, type, entry, "textOffsetMm", BuiltInParameter.TEXT_DIST_TO_LINE);
            PutBip(doc, type, entry, "widthFactor", BuiltInParameter.TEXT_WIDTH_SCALE);
            PutBool(type, entry, "bold", BuiltInParameter.TEXT_STYLE_BOLD);
            PutColor(type, entry, "color", BuiltInParameter.LINE_COLOR);
            PutBip(doc, type, entry, "weight", BuiltInParameter.LINE_PEN);
            PutBip(doc, type, entry, "tickMark", BuiltInParameter.DIM_LEADER_ARROWHEAD);
            PutBip(doc, type, entry, "tickMarkWeight", BuiltInParameter.TICK_MARK_PEN);
            PutBip(doc, type, entry, "interiorTickMark", BuiltInParameter.DIM_STYLE_INTERIOR_TICK_MARK);
            PutBip(doc, type, entry, "witnessLineGapMm", BuiltInParameter.WITNS_LINE_GAP_TO_ELT);
            PutBip(doc, type, entry, "witnessLineExtensionMm", BuiltInParameter.WITNS_LINE_EXTENSION);
            PutBip(doc, type, entry, "dimLineSnapDistanceMm", BuiltInParameter.DIM_STYLE_DIM_LINE_SNAP_DIST);
            var background = type.get_Parameter(BuiltInParameter.DIM_TEXT_BACKGROUND);
            if (background != null && background.HasValue)
                entry["textBackground"] = background.AsInteger() == 0 ? "opaque" : "transparent";
            try
            {
                var format = dimensionType.GetUnitsFormatOptions();
                var units = new JObject { ["useProjectSettings"] = format.UseDefault };
                if (!format.UseDefault)
                {
#if REVIT2022_OR_GREATER
                    units["unit"] = format.GetUnitTypeId().TypeId;
#else
                    units["unit"] = format.DisplayUnits.ToString();
#endif
                    units["accuracy"] = format.Accuracy;
                }
                entry["units"] = units;
            }
            catch (Exception)
            {
                // Spot slope / some styles have no linear units.
            }
        }

        private static void GridTypeEntry(Document doc, ElementType type, JObject entry)
        {
            PutBip(doc, type, entry, "symbol", BuiltInParameter.GRID_HEAD_TAG);
            PutBool(type, entry, "bubbleEnd1", BuiltInParameter.GRID_BUBBLE_END_1);
            PutBool(type, entry, "bubbleEnd2", BuiltInParameter.GRID_BUBBLE_END_2);
            PutText(type, entry, "centerSegment", BuiltInParameter.GRID_CENTER_SEGMENT_STYLE);
            PutBip(doc, type, entry, "centerWeight", BuiltInParameter.GRID_CENTER_SEGMENT_WEIGHT);
            PutColor(type, entry, "centerColor", BuiltInParameter.GRID_CENTER_SEGMENT_COLOR);
            PutBip(doc, type, entry, "centerPattern", BuiltInParameter.GRID_CENTER_SEGMENT_PATTERN);
            PutBip(doc, type, entry, "endWeight", BuiltInParameter.GRID_END_SEGMENT_WEIGHT);
            PutColor(type, entry, "endColor", BuiltInParameter.GRID_END_SEGMENT_COLOR);
            PutBip(doc, type, entry, "endPattern", BuiltInParameter.GRID_END_SEGMENT_PATTERN);
            PutBip(doc, type, entry, "endSegmentLengthMm", BuiltInParameter.GRID_END_SEGMENTS_LENGTH);
        }

        private static void LevelTypeEntry(Document doc, ElementType type, JObject entry)
        {
            PutBip(doc, type, entry, "symbol", BuiltInParameter.LEVEL_HEAD_TAG);
            PutBip(doc, type, entry, "weight", BuiltInParameter.LINE_PEN);
            PutColor(type, entry, "color", BuiltInParameter.LINE_COLOR);
            PutBip(doc, type, entry, "pattern", BuiltInParameter.LINE_PATTERN);
        }

        private static void ViewportTypeEntry(Document doc, ElementType type, JObject entry)
        {
            PutBip(doc, type, entry, "title", BuiltInParameter.VIEWPORT_ATTR_LABEL_TAG);
            PutText(type, entry, "showTitle", BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL);
            PutBool(type, entry, "showExtensionLine", BuiltInParameter.VIEWPORT_ATTR_SHOW_EXTENSION_LINE);
            PutBip(doc, type, entry, "weight", BuiltInParameter.LINE_PEN);
            PutColor(type, entry, "color", BuiltInParameter.LINE_COLOR);
            PutBip(doc, type, entry, "pattern", BuiltInParameter.LINE_PATTERN);
        }

        private static void HostType(Document doc, ElementType type, JObject entry)
        {
            if (type is WallType wall)
                entry["kind"] = wall.Kind.ToString();
            var structure = (type as HostObjAttributes)?.GetCompoundStructure();
            if (structure == null)
                return;
            entry["thicknessMm"] = DocumentationUtils.FeetToMm(structure.GetWidth());
            entry["layers"] = new JArray(structure.GetLayers().Select(l => new JObject
            {
                ["function"] = l.Function.ToString(),
                ["material"] = ProjectStyleUtils.ElementName(doc, l.MaterialId),
                ["thicknessMm"] = DocumentationUtils.FeetToMm(l.Width)
            }));
        }

        // ---------------------------------------------------------------- filters and templates

        private static JArray Filters(Document doc)
        {
            var result = new JArray();
            var all = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<FilterElement>()
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(SelectionFilterElement)).Cast<FilterElement>());
            foreach (var filter in all.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (filter is ParameterFilterElement parameterFilter)
                {
                    var categoryIds = parameterFilter.GetCategories();
                    var entry = new JObject
                    {
                        ["name"] = filter.Name,
                        ["type"] = "rule",
                        ["categories"] = new JArray(categoryIds.Select(id => CategoryName(doc, id)).OrderBy(n => n))
                    };
                    try
                    {
                        var rules = ProjectStyleUtils.DescribeFilter(doc, parameterFilter.GetElementFilter(), categoryIds);
                        if (rules != null)
                        {
                            entry["logic"] = rules["logic"];
                            entry["rules"] = rules["rules"];
                            if (rules["groups"] != null)
                                entry["groups"] = rules["groups"];
                        }
                        else
                        {
                            entry["rules"] = new JArray();
                        }
                    }
                    catch (Exception ex)
                    {
                        entry["readError"] = ex.Message;
                    }
                    result.Add(entry);
                }
                else if (filter is SelectionFilterElement selection)
                {
                    result.Add(new JObject { ["name"] = filter.Name, ["type"] = "selection", ["elementCount"] = selection.GetElementIds().Count });
                }
            }
            return result;
        }

        private static string CategoryName(Document doc, ElementId id)
        {
            var category = Category.GetCategory(doc, id);
            if (category == null)
                return id.GetValue().ToString();
            return ProjectStyleUtils.BicOf(category)?.ToString() ?? category.Name;
        }

        private static JArray ViewTemplates(Document doc, bool includeParameters)
        {
            var result = new JArray();
            var categories = ObjectStyleAndSubcategories(doc);
            foreach (var view in ProjectStyleUtils.Collect(doc, "viewTemplates").Cast<View>())
            {
                var entry = new JObject { ["name"] = view.Name, ["viewType"] = view.ViewType.ToString() };
                Try(() => entry["scale"] = view.Scale);
                Try(() => entry["detailLevel"] = view.DetailLevel.ToString());
                Try(() => entry["discipline"] = view.Discipline.ToString());
                Try(() => entry["displayStyle"] = view.DisplayStyle.ToString());
                Try(() => entry["partsVisibility"] = view.PartsVisibility.ToString());
                Try(() => Put(entry, "phaseFilter", ProjectStyleUtils.ElementName(doc, view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId())));
                if (view is ViewPlan plan)
                    Try(() => entry["viewRange"] = ViewRange(doc, plan.GetViewRange()));

                Try(() =>
                {
                    var all = view.GetTemplateParameterIds();
                    var notControlled = new HashSet<long>(view.GetNonControlledTemplateParameterIds().Select(id => id.GetValue()));
                    entry["controlledParameters"] = new JArray(all.Where(id => !notControlled.Contains(id.GetValue()))
                        .Select(id => ProjectStyleUtils.ParameterName(doc, id)).Where(n => n != null).OrderBy(n => n));
                    entry["notControlledParameters"] = new JArray(all.Where(id => notControlled.Contains(id.GetValue()))
                        .Select(id => ProjectStyleUtils.ParameterName(doc, id)).Where(n => n != null).OrderBy(n => n));
                });

                if (view.AreGraphicsOverridesAllowed())
                {
                    entry["categoryOverrides"] = CategoryOverrides(doc, view, categories);
                    Try(() => entry["filters"] = ViewFilters(doc, view));
                }

                if (includeParameters)
                    entry["parameters"] = ProjectStyleUtils.WritableParameters(doc, view);
                result.Add(entry);
            }
            return result;
        }

        private static void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception)
            {
                // Not applicable to this view type.
            }
        }

        private static JObject ViewRange(Document doc, PlanViewRange range)
        {
            var result = new JObject();
            foreach (var (name, plane) in new[]
                     {
                         ("top", PlanViewPlane.TopClipPlane), ("cut", PlanViewPlane.CutPlane),
                         ("bottom", PlanViewPlane.BottomClipPlane), ("viewDepth", PlanViewPlane.ViewDepthPlane)
                     })
            {
                var levelId = range.GetLevelId(plane);
                string level;
                if (levelId == PlanViewRange.Current)
                    level = "associatedLevel";
                else if (levelId == PlanViewRange.LevelAbove)
                    level = "levelAbove";
                else if (levelId == PlanViewRange.LevelBelow)
                    level = "levelBelow";
                else if (levelId == PlanViewRange.Unlimited)
                    level = "unlimited";
                else
                    level = ProjectStyleUtils.ElementName(doc, levelId);
                var planeEntry = new JObject { ["level"] = level };
                if (levelId != PlanViewRange.Unlimited)
                    planeEntry["offsetMm"] = DocumentationUtils.FeetToMm(range.GetOffset(plane));
                result[name] = planeEntry;
            }
            return result;
        }

        private static List<(Category category, string name)> ObjectStyleAndSubcategories(Document doc)
        {
            var list = new List<(Category, string)>();
            foreach (var category in ProjectStyleUtils.ObjectStyleCategories(doc))
            {
                list.Add((category, category.Name));
                foreach (Category sub in category.SubCategories)
                    list.Add((sub, category.Name + ": " + sub.Name));
            }
            return list;
        }

        private static JArray CategoryOverrides(Document doc, View view, List<(Category category, string name)> categories)
        {
            var result = new JArray();
            foreach (var (category, name) in categories)
            {
                try
                {
                    var hidden = view.CanCategoryBeHidden(category.Id) && view.GetCategoryHidden(category.Id);
                    var overrides = ProjectStyleUtils.Overrides(doc, view.GetCategoryOverrides(category.Id));
                    if (!hidden && overrides.Count == 0)
                        continue;
                    var entry = new JObject { ["category"] = name };
                    if (category.Parent == null && ProjectStyleUtils.BicOf(category) is BuiltInCategory bic)
                        entry["builtInCategory"] = bic.ToString();
                    if (hidden)
                        entry["visible"] = false;
                    foreach (var property in overrides.Properties())
                        entry[property.Name] = property.Value;
                    result.Add(entry);
                }
                catch (Exception)
                {
                    // Category not applicable to this view type.
                }
            }
            return result;
        }

        private static JArray ViewFilters(Document doc, View view)
        {
            var result = new JArray();
#if REVIT2022_OR_GREATER
            var ids = view.GetOrderedFilters();
#else
            var ids = view.GetFilters();
#endif
            foreach (var id in ids)
            {
                var entry = new JObject
                {
                    ["name"] = doc.GetElement(id)?.Name,
                    ["visible"] = view.GetFilterVisibility(id)
                };
#if REVIT2022_OR_GREATER
                entry["enabled"] = view.GetIsFilterEnabled(id);
#endif
                var overrides = ProjectStyleUtils.Overrides(doc, view.GetFilterOverrides(id));
                if (overrides.Count > 0)
                    entry["overrides"] = overrides;
                result.Add(entry);
            }
            return result;
        }

        // ---------------------------------------------------------------- families and defaults

        private static JObject FamilyEntry(Document doc, Family family)
        {
            return new JObject
            {
                ["name"] = family.Name,
                ["types"] = new JArray(family.GetFamilySymbolIds().Select(doc.GetElement).Where(e => e != null)
                    .Select(e => e.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            };
        }

        private static JArray FamilyList(Document doc, string kind)
        {
            return new JArray(ProjectStyleUtils.Collect(doc, kind).Cast<Family>().Select(f => FamilyEntry(doc, f)));
        }

        private static JArray FamiliesByCategory(Document doc, Func<Family, bool> predicate)
        {
            var result = new JArray();
            var families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .Where(f => !f.IsInPlace && f.FamilyCategory != null && predicate(f));
            foreach (var group in families.GroupBy(f => f.FamilyCategory.Id.GetValue()).OrderBy(g => g.First().FamilyCategory.Name))
            {
                var category = group.First().FamilyCategory;
                var entry = new JObject { ["category"] = category.Name };
                if (ProjectStyleUtils.BicOf(category) is BuiltInCategory bic)
                    entry["builtInCategory"] = bic.ToString();
                try
                {
                    Put(entry, "defaultType", ProjectStyleUtils.ElementName(doc, doc.GetDefaultFamilyTypeId(category.Id)));
                }
                catch (Exception)
                {
                    // No default family type for this category.
                }
                entry["families"] = new JArray(group.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => FamilyEntry(doc, f)));
                result.Add(entry);
            }
            return result;
        }

        private static JArray DefaultTypes(Document doc)
        {
            var result = new JArray();
            foreach (ElementTypeGroup group in Enum.GetValues(typeof(ElementTypeGroup)))
            {
                try
                {
                    var id = doc.GetDefaultElementTypeId(group);
                    if (id == null || id == ElementId.InvalidElementId || !(doc.GetElement(id) is ElementType type))
                        continue;
                    result.Add(new JObject { ["group"] = group.ToString(), ["family"] = type.FamilyName, ["type"] = type.Name });
                }
                catch (Exception)
                {
                    // Group not applicable to projects.
                }
            }
            return result;
        }
    }
}
