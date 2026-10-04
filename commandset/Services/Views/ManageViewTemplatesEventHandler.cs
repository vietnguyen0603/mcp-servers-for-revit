using System.Reflection;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Lists, creates, modifies and applies view templates (and modifies
    ///     ordinary views) in one call. Actions run in order inside one
    ///     transaction, each in its own sub-transaction. Within an action every
    ///     setting is applied independently: unknown names and unsupported
    ///     settings become warnings rather than failing the whole action.
    /// </summary>
    public class ManageViewTemplatesEventHandler : JsonParameterEventHandler
    {
        private static readonly MethodInfo SetIsFilterEnabledMethod =
            typeof(View).GetMethod("SetIsFilterEnabled", new[] { typeof(ElementId), typeof(bool) });

        private static readonly MethodInfo GetIsFilterEnabledMethod =
            typeof(View).GetMethod("GetIsFilterEnabled", new[] { typeof(ElementId) });

        private static readonly string[] OverrideKeys =
        {
            "halftone", "transparency", "projectionLine", "cutLine", "surfaceFill", "cutFill", "detailLevel", "reset"
        };

        public override string GetName() => "Manage View Templates";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var actions = DocumentationUtils.RequireArray(parameters, "actions");

            List<JObject> results;
            if (actions.All(a => string.Equals(a.Value<string>("action"), "list", StringComparison.OrdinalIgnoreCase)))
            {
                // Read-only call: no transaction.
                results = new List<JObject>();
                for (var i = 0; i < actions.Count; i++)
                {
                    try
                    {
                        var result = JObject.FromObject(Execute(doc, (JObject)actions[i]));
                        result.AddFirst(new JProperty("success", true));
                        result.AddFirst(new JProperty("index", i));
                        results.Add(result);
                    }
                    catch (Exception ex)
                    {
                        results.Add(new JObject { ["index"] = i, ["success"] = false, ["message"] = ex.Message });
                    }
                }
            }
            else
            {
                results = DocumentationUtils.RunBatch(doc, "MCP: Manage View Templates", actions,
                    action => Execute(doc, (JObject)action));
            }

            for (var i = 0; i < results.Count; i++)
                if (results[i]["action"] == null)
                    results[i]["action"] = actions[i].Value<string>("action");

            return Ok($"{results.Count(r => r.Value<bool>("success"))} of {results.Count} actions succeeded.",
                DocumentationUtils.Summarize(results));
        }

        private static object Execute(Document doc, JObject action)
        {
            switch ((action.Value<string>("action") ?? "").Trim().ToLowerInvariant())
            {
                case "list": return List(doc, action);
                case "create": return Create(doc, action);
                case "modify": return Modify(doc, action);
                case "apply": return Apply(doc, action);
                default: throw new ArgumentException("'action' must be list, create, modify or apply.");
            }
        }

        #region list

        private static object List(Document doc, JObject action)
        {
            var nameContains = action.Value<string>("nameContains");
            var ids = action["templateIds"] is JArray idArray
                ? new HashSet<long>(idArray.Select(t => t.Value<long>()))
                : null;
            var verbose = action.Value<bool?>("verbose") ?? false;

            var allViews = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
            var usage = allViews.Where(v => !v.IsTemplate && v.ViewTemplateId != ElementId.InvalidElementId)
                .GroupBy(v => v.ViewTemplateId.GetValue())
                .ToDictionary(g => g.Key, g => g.Count());
            var templates = allViews.Where(v => v.IsTemplate)
                .Where(v => string.IsNullOrEmpty(nameContains)
                            || v.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(v => ids == null || ids.Contains(v.Id.GetValue()))
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var categories = AllCategories(doc);

            return new
            {
                action = "list",
                count = templates.Count,
                templates = templates.Select(t => Describe(doc, t, categories, usage, verbose)).ToList()
            };
        }

        private static object Describe(Document doc, View t, List<(Category category, string path, Category parent)> categories,
            Dictionary<long, int> usage, bool verbose)
        {
            var templateParams = SafeGet(() => t.GetTemplateParameterIds().ToList(), new List<ElementId>());
            var nonControlled = new HashSet<long>(SafeGet(() => t.GetNonControlledTemplateParameterIds().Select(id => id.GetValue()).ToList(),
                new List<long>()));

            var filters = SafeGet(() => t.GetFilters().ToList(), new List<ElementId>()).Select(id => new
            {
                id = id.GetValue(),
                name = doc.GetElement(id)?.Name,
                visible = SafeGet(() => (bool?)t.GetFilterVisibility(id), null),
                enabled = GetFilterEnabled(t, id),
                overrides = SafeGet(() => DescribeOverrides(doc, t.GetFilterOverrides(id)), null)
            }).ToList();

            var hidden = new List<string>();
            var overridden = new List<object>();
            var hiddenIds = new HashSet<long>();
            foreach (var (category, path, parent) in categories)
            {
                if (parent != null && hiddenIds.Contains(parent.Id.GetValue()))
                    continue;
                if (SafeGet(() => t.GetCategoryHidden(category.Id), false))
                {
                    hidden.Add(path);
                    hiddenIds.Add(category.Id.GetValue());
                    continue;
                }

                var description = SafeGet(() => DescribeOverrides(doc, t.GetCategoryOverrides(category.Id)), null);
                if (description != null)
                {
                    if (verbose)
                    {
                        description.AddFirst(new JProperty("category", path));
                        overridden.Add(description);
                    }
                    else
                    {
                        overridden.Add(path);
                    }
                }
            }

            return new
            {
                id = t.Id.GetValue(),
                name = t.Name,
                viewType = t.ViewType.ToString(),
                scale = SafeGet(() => (int?)t.Scale, null),
                detailLevel = SafeGet(() => t.DetailLevel.ToString(), null),
                discipline = SafeGet(() => t.HasViewDiscipline() ? t.Discipline.ToString() : null, null),
                displayStyle = SafeGet(() => t.DisplayStyle.ToString(), null),
                showHiddenLines = t.get_Parameter(BuiltInParameter.VIEW_SHOW_HIDDEN_LINES)?.AsValueString(),
                viewsUsing = usage.TryGetValue(t.Id.GetValue(), out var count) ? count : 0,
                controlled = templateParams.Where(id => !nonControlled.Contains(id.GetValue()))
                    .Select(id => ParameterLabel(t, id)).ToList(),
                notControlled = templateParams.Where(id => nonControlled.Contains(id.GetValue()))
                    .Select(id => ParameterLabel(t, id)).ToList(),
                filters,
                hiddenCategories = hidden,
                overriddenCategories = overridden
            };
        }

        /// <summary>Non-default parts of an override, or null when nothing is overridden.</summary>
        private static JObject DescribeOverrides(Document doc, OverrideGraphicSettings s)
        {
            var o = new JObject();
            if (s.Halftone) o["halftone"] = true;
            if (s.Transparency > 0) o["transparency"] = s.Transparency;
            if (s.DetailLevel != ViewDetailLevel.Undefined) o["detailLevel"] = s.DetailLevel.ToString();
            AddLine(o, "projectionLine", s.ProjectionLineWeight, s.ProjectionLineColor, s.ProjectionLinePatternId, doc);
            AddLine(o, "cutLine", s.CutLineWeight, s.CutLineColor, s.CutLinePatternId, doc);
            AddFill(o, "surfaceFill", s.SurfaceForegroundPatternId, s.SurfaceForegroundPatternColor, s.IsSurfaceForegroundPatternVisible,
                s.SurfaceBackgroundPatternId, s.SurfaceBackgroundPatternColor, s.IsSurfaceBackgroundPatternVisible, doc);
            AddFill(o, "cutFill", s.CutForegroundPatternId, s.CutForegroundPatternColor, s.IsCutForegroundPatternVisible,
                s.CutBackgroundPatternId, s.CutBackgroundPatternColor, s.IsCutBackgroundPatternVisible, doc);
            return o.Count == 0 ? null : o;
        }

        private static void AddLine(JObject o, string key, int weight, Color color, ElementId pattern, Document doc)
        {
            var line = new JObject();
            if (weight != OverrideGraphicSettings.InvalidPenNumber && weight > 0) line["weight"] = weight;
            if (color != null && color.IsValid) line["color"] = ColorJson(color);
            if (pattern != null && pattern != ElementId.InvalidElementId)
                line["pattern"] = pattern == LinePatternElement.GetSolidPatternId() ? "Solid" : doc.GetElement(pattern)?.Name;
            if (line.Count > 0) o[key] = line;
        }

        private static void AddFill(JObject o, string key, ElementId fg, Color fgColor, bool fgVisible,
            ElementId bg, Color bgColor, bool bgVisible, Document doc)
        {
            var fill = DescribeLayer(fg, fgColor, fgVisible, doc);
            var background = DescribeLayer(bg, bgColor, bgVisible, doc);
            if (background != null)
            {
                fill = fill ?? new JObject();
                fill["background"] = background;
            }

            if (fill != null) o[key] = fill;
        }

        private static JObject DescribeLayer(ElementId pattern, Color color, bool visible, Document doc)
        {
            var layer = new JObject();
            if (pattern != null && pattern != ElementId.InvalidElementId) layer["pattern"] = doc.GetElement(pattern)?.Name;
            if (color != null && color.IsValid) layer["color"] = ColorJson(color);
            if (!visible) layer["visible"] = false;
            return layer.Count == 0 ? null : layer;
        }

        private static JArray ColorJson(Color c) => new JArray(c.Red, c.Green, c.Blue);

        #endregion

        #region create

        private static object Create(Document doc, JObject action)
        {
            var name = (action.Value<string>("name") ?? "").Trim();
            if (name.Length == 0)
                throw new ArgumentException("'name' is required.");

            var existing = FindTemplateByName(doc, name);
            if (existing != null)
            {
                if (string.Equals(action.Value<string>("ifExists"), "reuse", StringComparison.OrdinalIgnoreCase))
                    return new
                    {
                        action = "create", templateId = existing.Id.GetValue(), name = existing.Name,
                        viewType = existing.ViewType.ToString(), reused = true
                    };
                throw new InvalidOperationException(
                    $"A view template named '{existing.Name}' already exists (id {existing.Id.GetValue()}). Use ifExists:'reuse' or another name.");
            }

            var warnings = new List<string>();
            View template;
            string source;
            var fromViewId = DocumentationUtils.ReadId(action, "fromViewId");
            var fromTemplate = action["fromTemplate"];
            if (fromTemplate != null && fromTemplate.Type != JTokenType.Null)
            {
                var src = fromTemplate.Type == JTokenType.Integer
                    ? DocumentationUtils.GetElement<View>(doc, fromTemplate.Value<long>())
                    : FindTemplateByName(doc, fromTemplate.ToString());
                if (src == null || !src.IsTemplate)
                    throw new ArgumentException($"fromTemplate '{fromTemplate}' is not a view template.{SuggestTemplates(doc, fromTemplate.ToString())}");
                template = DuplicateTemplate(doc, src);
                source = $"duplicated '{src.Name}'";
            }
            else if (fromViewId != null)
            {
                var view = DocumentationUtils.GetElement<View>(doc, fromViewId)
                           ?? throw new ArgumentException($"fromViewId {fromViewId} is not a view.");
                if (view.IsTemplate)
                    throw new ArgumentException("fromViewId is a template; use fromTemplate to duplicate it.");
                template = view.CreateViewTemplate();
                source = $"from view '{view.Name}'";
            }
            else
            {
                var viewType = action.Value<string>("viewType")
                               ?? throw new ArgumentException("Provide viewType, fromViewId or fromTemplate.");
                var temporary = CreateTemporaryView(doc, viewType, out var cleanup);
                template = temporary.CreateViewTemplate();
                foreach (var id in cleanup)
                {
                    try
                    {
                        if (doc.GetElement(id) != null)
                            doc.Delete(id);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Could not delete temporary element {id.GetValue()}: {ex.Message}");
                    }
                }

                source = $"temporary {viewType} view";
            }

            if (template == null)
                throw new InvalidOperationException("Revit did not create the view template.");
            template.Name = name;

            return new
            {
                action = "create",
                templateId = template.Id.GetValue(),
                name = template.Name,
                viewType = template.ViewType.ToString(),
                source,
                warnings
            };
        }

        private static View DuplicateTemplate(Document doc, View src)
        {
            Exception first = null;
            try
            {
                if (src.CanViewBeDuplicated(ViewDuplicateOption.Duplicate))
                {
                    var id = src.Duplicate(ViewDuplicateOption.Duplicate);
                    if (doc.GetElement(id) is View copy)
                        return copy;
                }
            }
            catch (Exception ex)
            {
                first = ex;
            }

            try
            {
                var copied = ElementTransformUtils.CopyElements(doc, new List<ElementId> { src.Id }, doc,
                    Transform.Identity, new CopyPasteOptions());
                var copy = copied.Select(id => doc.GetElement(id)).OfType<View>().FirstOrDefault(v => v.IsTemplate);
                if (copy != null)
                    return copy;
            }
            catch (Exception ex)
            {
                first = first ?? ex;
            }

            throw new InvalidOperationException(
                $"Could not duplicate template '{src.Name}'{(first != null ? ": " + first.Message : ".")} Create from a view of the same type and modify it instead.");
        }

        private static View CreateTemporaryView(Document doc, string viewType, out List<ElementId> cleanup)
        {
            cleanup = new List<ElementId>();
            var none = new JObject();
            View view;
            switch (viewType.Trim().ToLowerInvariant())
            {
                case "floorplan":
                    view = ViewPlan.Create(doc, CreateViewEventHandler.ResolveViewFamilyType(doc, none, ViewFamily.FloorPlan), LowestLevel(doc).Id);
                    break;
                case "structuralplan":
                    view = ViewPlan.Create(doc, CreateViewEventHandler.ResolveViewFamilyType(doc, none, ViewFamily.StructuralPlan), LowestLevel(doc).Id);
                    break;
                case "ceilingplan":
                    view = ViewPlan.Create(doc, CreateViewEventHandler.ResolveViewFamilyType(doc, none, ViewFamily.CeilingPlan), LowestLevel(doc).Id);
                    break;
                case "section":
                {
                    var transform = Transform.Identity;
                    transform.BasisX = XYZ.BasisX;
                    transform.BasisY = XYZ.BasisZ;
                    transform.BasisZ = XYZ.BasisY.Negate();
                    var box = new BoundingBoxXYZ
                    {
                        Transform = transform,
                        Min = new XYZ(-10, -10, 0),
                        Max = new XYZ(10, 10, 10)
                    };
                    view = ViewSection.CreateSection(doc, CreateViewEventHandler.ResolveViewFamilyType(doc, none, ViewFamily.Section), box);
                    break;
                }
                case "elevation":
                {
                    var plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                        .FirstOrDefault(p => !p.IsTemplate && p.ViewType == ViewType.FloorPlan);
                    if (plan == null)
                    {
                        plan = ViewPlan.Create(doc, CreateViewEventHandler.ResolveViewFamilyType(doc, none, ViewFamily.FloorPlan), LowestLevel(doc).Id);
                        cleanup.Add(plan.Id);
                    }

                    var marker = ElevationMarker.CreateElevationMarker(doc,
                        CreateViewEventHandler.ResolveViewFamilyType(doc, none, ViewFamily.Elevation), XYZ.Zero, 100);
                    view = marker.CreateElevation(doc, plan.Id, 0);
                    // Deleting the marker deletes its elevation views; delete it before a temporary plan.
                    cleanup.Insert(0, marker.Id);
                    return view;
                }
                case "threed":
                case "3d":
                    view = View3D.CreateIsometric(doc, CreateViewEventHandler.ResolveViewFamilyType(doc, none, ViewFamily.ThreeDimensional));
                    break;
                case "drafting":
                    view = ViewDrafting.Create(doc, CreateViewEventHandler.ResolveViewFamilyType(doc, none, ViewFamily.Drafting));
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown viewType '{viewType}'. Use FloorPlan, StructuralPlan, CeilingPlan, Section, Elevation, ThreeD or Drafting.");
            }

            cleanup.Insert(0, view.Id);
            return view;
        }

        private static Level LowestLevel(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                       .OrderBy(l => l.Elevation).FirstOrDefault()
                   ?? throw new InvalidOperationException("Plan templates need at least one level in the project (or use fromViewId).");
        }

        #endregion

        #region modify

        private sealed class Report
        {
            public readonly List<string> Changed = new List<string>();
            public readonly List<string> Warnings = new List<string>();

            /// <summary>Runs one setting; an exception becomes a warning, a returned text a change.</summary>
            public void Try(string what, Func<string> apply)
            {
                try
                {
                    var change = apply();
                    if (!string.IsNullOrEmpty(change))
                        Changed.Add(change);
                }
                catch (Exception ex)
                {
                    Warnings.Add($"{what}: {ex.Message}");
                }
            }
        }

        private static object Modify(Document doc, JObject action)
        {
            var targets = new List<View>();
            var topWarnings = new List<string>();
            if (action["viewIds"] is JArray viewIds && viewIds.Count > 0)
            {
                foreach (var token in viewIds)
                {
                    var view = DocumentationUtils.GetElement<View>(doc, token.Value<long>());
                    if (view == null)
                        topWarnings.Add($"{token} is not a view.");
                    else
                        targets.Add(view);
                }

                if (targets.Count == 0)
                    throw new ArgumentException("None of 'viewIds' are views.");
            }
            else
            {
                targets.Add(ResolveTemplate(doc, action));
            }

            var categories = new Lazy<List<(Category category, string path, Category parent)>>(() => AllCategories(doc));
            var results = targets.Select(view => ModifyView(doc, view, action, categories)).ToList();
            return new { action = "modify", warnings = topWarnings, targets = results };
        }

        private static object ModifyView(Document doc, View view, JObject a,
            Lazy<List<(Category category, string path, Category parent)>> categories)
        {
            var r = new Report();
            if (!view.IsTemplate && view.ViewTemplateId != ElementId.InvalidElementId)
                r.Warnings.Add($"View uses template '{DocumentationUtils.ViewName(doc, view.ViewTemplateId)}'; settings it controls cannot change here.");

            if (a["scale"] is JToken scaleToken && scaleToken.Type != JTokenType.Null)
                r.Try("scale", () =>
                {
                    var scale = ParseScale(scaleToken);
                    if (view.Scale == scale) return null;
                    view.Scale = scale;
                    return $"scale 1:{scale}";
                });

            if (a.Value<string>("detailLevel") is string detail)
                r.Try("detailLevel", () =>
                {
                    view.DetailLevel = DocumentationUtils.ParseEnum(detail, ViewDetailLevel.Medium);
                    return $"detail level {view.DetailLevel}";
                });

            if (a.Value<string>("discipline") is string discipline)
                r.Try("discipline", () =>
                {
                    if (!view.HasViewDiscipline())
                        throw new InvalidOperationException("this view has no discipline.");
                    view.Discipline = DocumentationUtils.ParseEnum(discipline, ViewDiscipline.Coordination);
                    return $"discipline {view.Discipline}";
                });

            if (a.Value<string>("displayStyle") is string style)
                r.Try("displayStyle", () =>
                {
                    view.DisplayStyle = ParseDisplayStyle(style);
                    return $"display style {view.DisplayStyle}";
                });

            if (a.Value<string>("showHiddenLines") is string hiddenLines)
                r.Try("showHiddenLines", () => SetShowHiddenLines(view, hiddenLines));

            if (a["underlay"] is JObject underlay)
                SetUnderlay(doc, view, underlay, r);

            if (a["viewRange"] is JObject range)
                r.Try("viewRange", () => SetViewRange(doc, view, range));

            if (a["categories"] is JArray categorySpecs)
                foreach (var spec in categorySpecs.OfType<JObject>())
                    ApplyCategory(doc, view, spec, categories.Value, r);

            if (a["filters"] is JArray filterSpecs)
                foreach (var spec in filterSpecs.OfType<JObject>())
                    ApplyFilter(doc, view, spec, r);

            if (a["controlled"] is JObject controlled)
            {
                if (!view.IsTemplate)
                    r.Warnings.Add("controlled: only applies to view templates.");
                else
                    r.Try("controlled", () => SetControlled(view, controlled, r));
            }

            return new
            {
                viewId = view.Id.GetValue(),
                name = view.Name,
                isTemplate = view.IsTemplate,
                changed = r.Changed,
                warnings = r.Warnings
            };
        }

        private static int ParseScale(JToken token)
        {
            int scale;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                scale = token.Value<int>();
            }
            else
            {
                var text = token.ToString().Trim();
                var colon = text.IndexOf(':');
                if (colon >= 0)
                    text = text.Substring(colon + 1).Trim();
                if (!int.TryParse(text, out scale))
                    throw new ArgumentException($"'{token}' is not a scale; use 150 or '1:150'.");
            }

            if (scale < 1 || scale > 24000)
                throw new ArgumentException("scale must be between 1 and 24000.");
            return scale;
        }

        private static DisplayStyle ParseDisplayStyle(string value)
        {
            switch (value.Replace(" ", "").Trim().ToLowerInvariant())
            {
                case "wireframe": return DisplayStyle.Wireframe;
                case "hiddenline":
                case "hlr": return DisplayStyle.HLR;
                case "shaded":
                case "shading": return DisplayStyle.Shading;
                case "shadedwithedges":
                case "shadingwithedges": return DisplayStyle.ShadingWithEdges;
                case "consistentcolors":
                case "flatcolors": return DisplayStyle.FlatColors;
                case "realistic": return DisplayStyle.Realistic;
                default:
                    throw new ArgumentException(
                        $"Unknown display style '{value}'. Use Wireframe, HiddenLine, Shaded, ShadedWithEdges, ConsistentColors or Realistic.");
            }
        }

        private static string SetShowHiddenLines(View view, string value)
        {
            var target = DocumentationUtils.ParseEnum(value, ShowHiddenLinesValues.ByDiscipline);
            var parameter = view.get_Parameter(BuiltInParameter.VIEW_SHOW_HIDDEN_LINES);
            if (parameter == null)
                throw new InvalidOperationException("'Show Hidden Lines' is not available for this view type.");
            if (parameter.IsReadOnly)
                throw new InvalidOperationException("'Show Hidden Lines' is read-only here (controlled by a template?).");
            if (parameter.StorageType != StorageType.Integer)
                throw new InvalidOperationException($"'Show Hidden Lines' has unexpected storage {parameter.StorageType}.");
            var intValue = Convert.ToInt32(target);
            if (!parameter.Set(intValue) || parameter.AsInteger() != intValue)
                throw new InvalidOperationException($"Revit rejected value {target}.");
            return $"show hidden lines {parameter.AsValueString() ?? target.ToString()}";
        }

        private static void SetUnderlay(Document doc, View view, JObject spec, Report r)
        {
            if (!(view is ViewPlan plan))
            {
                r.Warnings.Add("underlay: only plan views have an underlay.");
                return;
            }

            if (spec.Value<string>("orientation") is string orientation)
                r.Try("underlay.orientation", () =>
                {
                    plan.SetUnderlayOrientation(DocumentationUtils.ParseEnum(orientation, UnderlayOrientation.LookingDown));
                    return $"underlay orientation {orientation}";
                });

            var baseToken = spec["baseLevel"];
            var topToken = spec["topLevel"];
            if (baseToken == null && topToken == null)
                return;
            if (plan.IsTemplate)
            {
                r.Warnings.Add("underlay: base/top levels are not template settings; set them on the plan views.");
                return;
            }

            r.Try("underlay", () =>
            {
                var baseId = baseToken != null ? ResolveUnderlayLevel(doc, baseToken, "None") : plan.GetUnderlayBaseLevel();
                if (topToken != null)
                {
                    plan.SetUnderlayRange(baseId, ResolveUnderlayLevel(doc, topToken, "Unbounded"));
                    return $"underlay {LevelLabel(doc, baseId)} to {LevelLabel(doc, plan.GetUnderlayTopLevel())}";
                }

                plan.SetUnderlayBaseLevel(baseId);
                return $"underlay base {LevelLabel(doc, baseId)}";
            });
        }

        private static ElementId ResolveUnderlayLevel(Document doc, JToken token, string noneWord)
        {
            if (token.Type == JTokenType.Integer)
                return (doc.GetElement(token.Value<long>().ToRevitElementId()) as Level)?.Id
                       ?? throw new ArgumentException($"{token} is not a level.");
            var text = token.ToString().Trim();
            if (text.Equals(noneWord, StringComparison.OrdinalIgnoreCase) || text.Equals("None", StringComparison.OrdinalIgnoreCase))
                return ElementId.InvalidElementId;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            var level = levels.FirstOrDefault(l => string.Equals(l.Name, text, StringComparison.OrdinalIgnoreCase));
            return level?.Id ?? throw new ArgumentException(
                $"Level '{text}' not found.{Suggest(text, levels.Select(l => l.Name))}");
        }

        private static string LevelLabel(Document doc, ElementId id) =>
            id == ElementId.InvalidElementId ? "None" : doc.GetElement(id)?.Name ?? id.GetValue().ToString();

        private static readonly (PlanViewPlane plane, string offset, string level)[] RangePlanes =
        {
            (PlanViewPlane.TopClipPlane, "topMm", "topLevelId"),
            (PlanViewPlane.CutPlane, "cutPlaneMm", null),
            (PlanViewPlane.BottomClipPlane, "bottomMm", "bottomLevelId"),
            (PlanViewPlane.ViewDepthPlane, "viewDepthMm", "viewDepthLevelId")
        };

        private static string SetViewRange(Document doc, View view, JObject spec)
        {
            if (!(view is ViewPlan plan))
                throw new InvalidOperationException("only plan views have a view range.");
            var range = plan.GetViewRange();
            var parts = new List<string>();
            foreach (var (plane, offsetKey, levelKey) in RangePlanes)
            {
                if (levelKey != null && spec[levelKey] != null && spec[levelKey].Type != JTokenType.Null)
                    range.SetLevelId(plane, SetViewRangeEventHandler.ResolveLevel(doc, spec[levelKey], levelKey));
                if (spec.Value<double?>(offsetKey) is double offset)
                {
                    range.SetOffset(plane, DocumentationUtils.MmToFeet(offset));
                    parts.Add($"{offsetKey}={offset}");
                }
            }

            plan.SetViewRange(range);
            return $"view range {string.Join(", ", parts)}".TrimEnd();
        }

        private static bool HasOverrides(JObject spec) => OverrideKeys.Any(k => spec[k] != null);

        private static void ApplyCategory(Document doc, View view, JObject spec,
            List<(Category category, string path, Category parent)> all, Report r)
        {
            var name = spec.Value<string>("category") ?? "";
            var category = ResolveCategoryPath(doc, name);
            if (category == null)
            {
                r.Warnings.Add($"Unknown category '{name}'.{Suggest(name, all.Select(c => c.path))}");
                return;
            }

            var label = category.Parent != null ? $"{category.Parent.Name}/{category.Name}" : category.Name;

            if (spec.Value<bool?>("visible") is bool visible)
                r.Try($"{label} visibility", () =>
                {
                    if (!view.CanCategoryBeHidden(category.Id))
                        throw new InvalidOperationException("visibility cannot be changed in this view.");
                    view.SetCategoryHidden(category.Id, !visible);
                    return $"{label} {(visible ? "visible" : "hidden")}";
                });

            if (HasOverrides(spec))
                r.Try($"{label} overrides", () =>
                {
                    if (!view.IsCategoryOverridable(category.Id))
                        throw new InvalidOperationException("graphics cannot be overridden in this view.");
                    var settings = BuildOverrides(doc, spec, view.GetCategoryOverrides(category.Id), r, label);
                    view.SetCategoryOverrides(category.Id, settings);
                    return $"{label} overrides";
                });
        }

        private static void ApplyFilter(Document doc, View view, JObject spec, Report r)
        {
            var name = (spec.Value<string>("name") ?? "").Trim();
            var filters = new FilteredElementCollector(doc).OfClass(typeof(FilterElement)).Cast<FilterElement>().ToList();
            var filter = filters.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (filter == null)
            {
                r.Warnings.Add($"Unknown filter '{name}' (create it with create_view_filter).{Suggest(name, filters.Select(f => f.Name))}");
                return;
            }

            var label = $"filter '{filter.Name}'";
            if (spec.Value<bool?>("remove") == true)
            {
                r.Try(label, () =>
                {
                    if (!view.IsFilterApplied(filter.Id)) return null;
                    view.RemoveFilter(filter.Id);
                    return $"{label} removed";
                });
                return;
            }

            r.Try(label, () =>
            {
                if (view.IsFilterApplied(filter.Id)) return null;
                view.AddFilter(filter.Id);
                return $"{label} added";
            });
            if (!view.IsFilterApplied(filter.Id))
                return;

            if (spec.Value<bool?>("visible") is bool visible)
                r.Try($"{label} visibility", () =>
                {
                    view.SetFilterVisibility(filter.Id, visible);
                    return $"{label} {(visible ? "visible" : "hidden")}";
                });

            if (spec.Value<bool?>("enabled") is bool enabled)
                r.Try($"{label} enabled", () =>
                {
                    if (SetIsFilterEnabledMethod == null)
                        throw new NotSupportedException("enabling/disabling filters requires Revit 2021+.");
                    try
                    {
                        SetIsFilterEnabledMethod.Invoke(view, new object[] { filter.Id, enabled });
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException != null)
                    {
                        throw ex.InnerException;
                    }

                    return $"{label} {(enabled ? "enabled" : "disabled")}";
                });

            if (spec["overrides"] is JObject overrides && overrides.Count > 0)
                r.Try($"{label} overrides", () =>
                {
                    view.SetFilterOverrides(filter.Id, BuildOverrides(doc, overrides, view.GetFilterOverrides(filter.Id), r, label));
                    return $"{label} overrides";
                });
        }

        private static bool? GetFilterEnabled(View view, ElementId filterId)
        {
            if (GetIsFilterEnabledMethod == null)
                return null;
            try
            {
                return (bool)GetIsFilterEnabledMethod.Invoke(view, new object[] { filterId });
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion

        #region overrides

        private static OverrideGraphicSettings BuildOverrides(Document doc, JObject spec, OverrideGraphicSettings existing,
            Report r, string label)
        {
            var s = spec.Value<bool?>("reset") == true || existing == null
                ? new OverrideGraphicSettings()
                : new OverrideGraphicSettings(existing);

            if (spec.Value<bool?>("halftone") is bool halftone)
                s.SetHalftone(halftone);
            if (spec.Value<int?>("transparency") is int transparency)
                s.SetSurfaceTransparency(Math.Max(0, Math.Min(100, transparency)));
            if (spec.Value<string>("detailLevel") is string detail)
                s.SetDetailLevel(string.Equals(detail, "ByView", StringComparison.OrdinalIgnoreCase)
                    ? ViewDetailLevel.Undefined
                    : DocumentationUtils.ParseEnum(detail, ViewDetailLevel.Medium));

            if (spec["projectionLine"] is JObject projection)
            {
                if (ReadWeight(projection) is int w) s.SetProjectionLineWeight(w);
                if (ReadColor(projection) is Color c) s.SetProjectionLineColor(c);
                if (projection.Value<string>("pattern") is string p && ResolveLinePattern(doc, p, r, label) is ElementId id)
                    s.SetProjectionLinePatternId(id);
            }

            if (spec["cutLine"] is JObject cut)
            {
                if (ReadWeight(cut) is int w) s.SetCutLineWeight(w);
                if (ReadColor(cut) is Color c) s.SetCutLineColor(c);
                if (cut.Value<string>("pattern") is string p && ResolveLinePattern(doc, p, r, label) is ElementId id)
                    s.SetCutLinePatternId(id);
            }

            if (spec["surfaceFill"] is JObject surface)
            {
                ApplyFillLayer(doc, surface, s.SurfaceForegroundPatternId, r, label,
                    s.SetSurfaceForegroundPatternId, s.SetSurfaceForegroundPatternColor, s.SetSurfaceForegroundPatternVisible);
                if (surface["background"] is JObject background)
                    ApplyFillLayer(doc, background, s.SurfaceBackgroundPatternId, r, label,
                        s.SetSurfaceBackgroundPatternId, s.SetSurfaceBackgroundPatternColor, s.SetSurfaceBackgroundPatternVisible);
            }

            if (spec["cutFill"] is JObject cutFill)
            {
                ApplyFillLayer(doc, cutFill, s.CutForegroundPatternId, r, label,
                    s.SetCutForegroundPatternId, s.SetCutForegroundPatternColor, s.SetCutForegroundPatternVisible);
                if (cutFill["background"] is JObject background)
                    ApplyFillLayer(doc, background, s.CutBackgroundPatternId, r, label,
                        s.SetCutBackgroundPatternId, s.SetCutBackgroundPatternColor, s.SetCutBackgroundPatternVisible);
            }

            return s;
        }

        private static void ApplyFillLayer(Document doc, JObject layer, ElementId currentPattern, Report r, string label,
            Func<ElementId, OverrideGraphicSettings> setPattern, Func<Color, OverrideGraphicSettings> setColor,
            Func<bool, OverrideGraphicSettings> setVisible)
        {
            var patternName = layer.Value<string>("pattern");
            var color = ReadColor(layer);
            var patternSet = false;
            if (patternName != null && ResolveFillPattern(doc, patternName, r, label) is ElementId patternId)
            {
                setPattern(patternId);
                patternSet = true;
            }
            else if (patternName == null && color != null
                     && (currentPattern == null || currentPattern == ElementId.InvalidElementId)
                     && SolidFill(doc) is ElementId solid)
            {
                // A colour alone implies solid fill.
                setPattern(solid);
                patternSet = true;
            }

            if (color != null)
                setColor(color);

            if (layer.Value<bool?>("visible") is bool visible)
                setVisible(visible);
            else if (patternSet || color != null)
                setVisible(true);
        }

        private static int? ReadWeight(JObject o)
        {
            var weight = o.Value<int?>("weight");
            if (weight != null && (weight < 1 || weight > 16))
                throw new ArgumentException("line weight must be between 1 and 16.");
            return weight;
        }

        private static Color ReadColor(JObject o)
        {
            if (!(o["color"] is JArray rgb))
                return null;
            if (rgb.Count != 3)
                throw new ArgumentException("color must be [r, g, b].");
            var v = rgb.Select(t => Math.Max(0, Math.Min(255, t.Value<int>()))).ToArray();
            return new Color((byte)v[0], (byte)v[1], (byte)v[2]);
        }

        private static string NormalizePatternName(string name) =>
            (name ?? "").Trim().Trim('<', '>').Trim().ToLowerInvariant();

        private static ElementId ResolveLinePattern(Document doc, string name, Report r, string label)
        {
            var key = NormalizePatternName(name);
            if (key == "solid")
                return LinePatternElement.GetSolidPatternId();
            var patterns = new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>().ToList();
            var match = patterns.FirstOrDefault(p => NormalizePatternName(p.Name) == key);
            if (match != null)
                return match.Id;
            r.Warnings.Add($"{label}: unknown line pattern '{name}'.{Suggest(name, new[] { "Solid" }.Concat(patterns.Select(p => p.Name)))}");
            return null;
        }

        private static ElementId ResolveFillPattern(Document doc, string name, Report r, string label)
        {
            var key = NormalizePatternName(name);
            var patterns = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().ToList();
            var match = patterns.Where(p => NormalizePatternName(p.Name) == key)
                .OrderBy(p => p.GetFillPattern().Target == FillPatternTarget.Drafting ? 0 : 1)
                .FirstOrDefault();
            if (match != null)
                return match.Id;
            if (key == "solid" || key == "solid fill")
            {
                var solid = SolidFill(doc);
                if (solid != null)
                    return solid;
            }

            r.Warnings.Add($"{label}: unknown fill pattern '{name}'.{Suggest(name, patterns.Select(p => p.Name).Distinct())}");
            return null;
        }

        private static ElementId SolidFill(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(p => p.GetFillPattern().IsSolidFill)?.Id;

        #endregion

        #region controlled parameters

        private static readonly Dictionary<string, string> ParameterAliases = new Dictionary<string, string>
        {
            ["filters"] = "vgoverridesfilters",
            ["viewfilters"] = "vgoverridesfilters",
            ["scale"] = "viewscale",
            ["model"] = "vgoverridesmodel",
            ["modelcategories"] = "vgoverridesmodel",
            ["vgmodel"] = "vgoverridesmodel",
            ["annotation"] = "vgoverridesannotation",
            ["annotationcategories"] = "vgoverridesannotation",
            ["vgannotation"] = "vgoverridesannotation",
            ["import"] = "vgoverridesimport",
            ["importcategories"] = "vgoverridesimport",
            ["analyticalmodel"] = "vgoverridesanalyticalmodel",
            ["worksets"] = "vgoverridesworksets",
            ["revitlinks"] = "vgoverridesrvtlinks",
            ["links"] = "vgoverridesrvtlinks",
            ["visualstyle"] = "modeldisplay",
            ["displaystyle"] = "modeldisplay",
            ["hiddenlines"] = "showhiddenlines",
            ["viewrangeplan"] = "viewrange",
            ["phase"] = "phasefilter"
        };

        private static string NormalizeKey(string text) =>
            new string((text ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        private static string SetControlled(View template, JObject spec, Report r)
        {
            var ids = template.GetTemplateParameterIds().ToList();
            var entries = ids.Select(id => (id, label: ParameterLabel(template, id), bip: BuiltInName(id))).ToList();
            var nonControlled = new HashSet<long>(template.GetNonControlledTemplateParameterIds().Select(id => id.GetValue()));
            var changes = new List<string>();

            void Apply(JToken names, bool include)
            {
                if (!(names is JArray array))
                    return;
                foreach (var name in array.Select(t => t.ToString()))
                {
                    var matches = MatchParameters(name, entries);
                    if (matches.Count == 0)
                    {
                        r.Warnings.Add($"controlled: unknown template parameter '{name}'.{Suggest(name, entries.Select(e => e.label))}");
                        continue;
                    }

                    foreach (var (id, label, _) in matches)
                    {
                        var changed = include ? nonControlled.Remove(id.GetValue()) : nonControlled.Add(id.GetValue());
                        if (changed)
                            changes.Add($"{(include ? "+" : "-")}{label}");
                    }
                }
            }

            Apply(spec["include"], true);
            Apply(spec["exclude"], false);
            if (changes.Count == 0)
                return null;

            template.SetNonControlledTemplateParameterIds(nonControlled.Select(v => v.ToRevitElementId()).ToList());
            return $"controlled {string.Join(", ", changes)}";
        }

        private static List<(ElementId id, string label, string bip)> MatchParameters(string name,
            List<(ElementId id, string label, string bip)> entries)
        {
            var key = NormalizeKey(name);
            if (key == "all")
                return entries;
            var exact = entries.Where(e => NormalizeKey(e.label) == key || NormalizeKey(e.bip) == key).ToList();
            if (exact.Count > 0)
                return exact;
            if (ParameterAliases.TryGetValue(key, out var alias))
            {
                var aliased = entries.Where(e => NormalizeKey(e.label) == alias).ToList();
                if (aliased.Count > 0)
                    return aliased;
            }

            var contains = entries.Where(e => NormalizeKey(e.label).Contains(key)).ToList();
            return contains.Count == 1 ? contains : new List<(ElementId, string, string)>();
        }

        private static string BuiltInName(ElementId id)
        {
            var value = id.GetValue();
            if (value >= 0)
                return null;
            try
            {
                var bip = Enum.ToObject(typeof(BuiltInParameter), value);
                return Enum.IsDefined(typeof(BuiltInParameter), bip) ? bip.ToString() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ParameterLabel(View view, ElementId id)
        {
            var value = id.GetValue();
            if (value < 0)
            {
                try
                {
                    var bip = (BuiltInParameter)Enum.ToObject(typeof(BuiltInParameter), value);
                    var label = LabelUtils.GetLabelFor(bip);
                    if (!string.IsNullOrWhiteSpace(label))
                        return label;
                }
                catch (Exception)
                {
                    // fall back to the parameter definition
                }
            }

            foreach (Parameter parameter in view.Parameters)
                if (parameter.Id == id)
                    return parameter.Definition?.Name ?? value.ToString();
            return BuiltInName(id) ?? value.ToString();
        }

        #endregion

        #region apply

        private static object Apply(Document doc, JObject action)
        {
            var template = ResolveTemplate(doc, action);
            var propertiesOnly = action.Value<bool?>("applyPropertiesOnly") ?? false;
            var views = new List<object>();
            var warnings = new List<string>();
            foreach (var token in DocumentationUtils.RequireArray(action, "viewIds"))
            {
                var id = token.Value<long>();
                try
                {
                    var view = DocumentationUtils.GetElement<View>(doc, id)
                               ?? throw new ArgumentException("not a view.");
                    if (view.IsTemplate)
                        throw new ArgumentException($"'{view.Name}' is itself a view template.");
                    if (propertiesOnly)
                    {
                        view.ApplyViewTemplateParameters(template);
                    }
                    else
                    {
                        if (!view.IsValidViewTemplate(template.Id))
                            throw new InvalidOperationException(
                                $"template '{template.Name}' ({template.ViewType}) is not valid for {view.ViewType} view '{view.Name}'.");
                        view.ViewTemplateId = template.Id;
                    }

                    views.Add(new { viewId = id, name = view.Name });
                }
                catch (Exception ex)
                {
                    warnings.Add($"view {id}: {ex.Message}");
                }
            }

            if (views.Count == 0)
                throw new InvalidOperationException($"Template not applied to any view: {string.Join("; ", warnings)}");

            return new
            {
                action = "apply",
                templateId = template.Id.GetValue(),
                templateName = template.Name,
                mode = propertiesOnly ? "propertiesOnly" : "assigned",
                views,
                warnings
            };
        }

        #endregion

        #region lookup helpers

        private static View ResolveTemplate(Document doc, JObject action)
        {
            var id = DocumentationUtils.ReadId(action, "templateId");
            if (id != null)
            {
                var byId = DocumentationUtils.GetElement<View>(doc, id);
                if (byId == null || !byId.IsTemplate)
                    throw new ArgumentException($"templateId {id} is not a view template.");
                return byId;
            }

            var name = action.Value<string>("templateName");
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Provide templateId or templateName.");
            return FindTemplateByName(doc, name)
                   ?? throw new ArgumentException($"View template '{name}' not found.{SuggestTemplates(doc, name)}");
        }

        private static View FindTemplateByName(Document doc, string name)
        {
            var trimmed = name.Trim();
            return new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => v.IsTemplate && string.Equals(v.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        }

        private static string SuggestTemplates(Document doc, string name) =>
            Suggest(name, new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => v.IsTemplate).Select(v => v.Name));

        /// <summary>
        ///     Resolves "Category" or "Category/Subcategory"; the full text is tried
        ///     first because a few category names contain '/'.
        /// </summary>
        private static Category ResolveCategoryPath(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            var direct = DocumentationUtils.ResolveCategory(doc, name);
            if (direct != null)
                return direct;

            for (var slash = name.IndexOf('/'); slash > 0; slash = name.IndexOf('/', slash + 1))
            {
                var parent = DocumentationUtils.ResolveCategory(doc, name.Substring(0, slash));
                if (parent == null)
                    continue;
                var subName = name.Substring(slash + 1).Trim();
                foreach (Category sub in parent.SubCategories)
                    if (string.Equals(sub.Name, subName, StringComparison.OrdinalIgnoreCase))
                        return sub;
            }

            return null;
        }

        /// <summary>Model and annotation categories plus their subcategories, sorted by path.</summary>
        private static List<(Category category, string path, Category parent)> AllCategories(Document doc)
        {
            var list = new List<(Category, string, Category)>();
            foreach (Category category in doc.Settings.Categories)
            {
                if (category.CategoryType != CategoryType.Model && category.CategoryType != CategoryType.Annotation)
                    continue;
                list.Add((category, category.Name, null));
                foreach (Category sub in category.SubCategories)
                    list.Add((sub, $"{category.Name}/{sub.Name}", category));
            }

            return list.OrderBy(c => c.Item2, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>" Did you mean: a, b, c?" for the closest candidates, or "".</summary>
        private static string Suggest(string query, IEnumerable<string> candidates)
        {
            var key = NormalizeKey(query);
            if (key.Length == 0)
                return "";
            var ranked = candidates.Where(c => !string.IsNullOrEmpty(c)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(c =>
                {
                    var ck = NormalizeKey(c);
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

        private static T SafeGet<T>(Func<T> get, T fallback)
        {
            try
            {
                return get();
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        #endregion
    }
}
