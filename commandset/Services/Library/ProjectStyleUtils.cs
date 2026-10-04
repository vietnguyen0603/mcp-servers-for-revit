using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Library
{
    /// <summary>
    ///     The "project standards" kinds shared by copy_project_standards and export_project_style:
    ///     how to collect each kind from a document, how to match an item between documents, and how
    ///     to read graphics (colours, line weights, patterns, overrides, filter rules, type
    ///     parameters) into the JSON vocabulary used by the MCP tools (mm, degrees, [r,g,b], names).
    /// </summary>
    internal static class ProjectStyleUtils
    {
        /// <summary>All kinds in dependency order (patterns before the types and templates using them).</summary>
        public static readonly string[] Kinds =
        {
            "linePatterns", "fillPatterns", "materials", "lineStyles", "objectStyles", "arrowheads", "textTypes",
            "dimensionTypes", "gridTypes", "levelTypes", "viewportTypes", "wallTypes", "floorTypes", "viewFilters",
            "viewTemplates", "tagFamilies", "titleBlocks", "annotationSymbols"
        };

        public static readonly HashSet<string> FamilyKinds = new HashSet<string> { "tagFamilies", "titleBlocks", "annotationSymbols" };
        public static readonly HashSet<string> CategoryKinds = new HashSet<string> { "lineStyles", "objectStyles" };

        public static string NormalizeKind(string kind)
        {
            var match = Kinds.FirstOrDefault(k => string.Equals(k, kind?.Trim(), StringComparison.OrdinalIgnoreCase));
            return match ?? throw new ArgumentException($"Unknown kind '{kind}'. Kinds: {string.Join(", ", Kinds)}.");
        }

        // ---------------------------------------------------------------- collection

        /// <summary>Elements of an element kind (not lineStyles/objectStyles), filtered by name and category, sorted by name.</summary>
        public static List<Element> Collect(Document doc, string kind, IList<string> names = null, IList<string> categories = null)
        {
            IEnumerable<Element> items;
            switch (kind)
            {
                case "linePatterns": items = OfClass<LinePatternElement>(doc); break;
                case "fillPatterns": items = OfClass<FillPatternElement>(doc); break;
                case "materials": items = OfClass<Material>(doc); break;
                case "arrowheads": items = Types(doc).Where(IsArrowhead); break;
                case "textTypes": items = OfClass<TextNoteType>(doc); break;
                case "dimensionTypes": items = OfClass<DimensionType>(doc); break;
                case "gridTypes": items = OfClass<GridType>(doc); break;
                case "levelTypes": items = OfClass<LevelType>(doc); break;
                case "viewportTypes": items = Types(doc).Where(IsViewportType); break;
                case "wallTypes": items = OfClass<WallType>(doc); break;
                case "floorTypes": items = OfClass<FloorType>(doc); break;
                case "viewFilters": items = OfClass<ParameterFilterElement>(doc); break;
                case "viewTemplates": items = OfClass<View>(doc).Where(v => ((View)v).IsTemplate); break;
                case "tagFamilies": items = Families(doc).Where(f => IsTagCategory(f.FamilyCategory)); break;
                case "titleBlocks": items = Families(doc).Where(f => BicOf(f.FamilyCategory) == BuiltInCategory.OST_TitleBlocks); break;
                case "annotationSymbols": items = Families(doc).Where(f => BicOf(f.FamilyCategory) == BuiltInCategory.OST_GenericAnnotation); break;
                default: return new List<Element>();
            }

            return items
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .Where(e => SourceDocumentUtils.LikeAny(e.Name, names))
                .Where(e => categories == null || categories.Count == 0 ||
                            SourceDocumentUtils.InCategories(e is Family f ? f.FamilyCategory : e.Category, categories))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<Element> OfClass<T>(Document doc) where T : Element
        {
            return new FilteredElementCollector(doc).OfClass(typeof(T)).ToElements();
        }

        private static IEnumerable<ElementType> Types(Document doc)
        {
            return new FilteredElementCollector(doc).WhereElementIsElementType().OfType<ElementType>();
        }

        private static IEnumerable<Family> Families(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().Where(f => !f.IsInPlace);
        }

        private static bool IsArrowhead(ElementType type)
        {
            return type.get_Parameter(BuiltInParameter.ARROW_TYPE) != null;
        }

        private static bool IsViewportType(ElementType type)
        {
            return BicOf(type.Category) == BuiltInCategory.OST_Viewports
                   || type.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL) != null;
        }

        public static bool IsTagCategory(Category category)
        {
            var bic = BicOf(category);
            return category != null && category.CategoryType == CategoryType.Annotation && bic.HasValue &&
                   bic.Value.ToString().EndsWith("Tags", StringComparison.Ordinal);
        }

        /// <summary>Line styles = subcategories of the Lines category.</summary>
        public static List<Category> LineStyles(Document doc, IList<string> names = null)
        {
            var lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            return lines.SubCategories.Cast<Category>()
                .Where(c => SourceDocumentUtils.LikeAny(c.Name, names))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Top-level model and annotation categories (object styles), filtered by name or OST_ name.</summary>
        public static List<Category> ObjectStyleCategories(Document doc, IList<string> names = null)
        {
            return doc.Settings.Categories.Cast<Category>()
                .Where(c => c.CategoryType == CategoryType.Model || c.CategoryType == CategoryType.Annotation)
                .Where(c => names == null || names.Count == 0 || names.Any(n =>
                    SourceDocumentUtils.Like(c.Name, n) || (BicOf(c)?.ToString() is string bic && SourceDocumentUtils.Like(bic, n))))
                .OrderBy(c => c.CategoryType).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The built-in category of a category, or null for user/family subcategories.</summary>
        public static BuiltInCategory? BicOf(Category category)
        {
            if (category == null)
                return null;
            var value = category.Id.GetValue();
            if (value >= 0)
                return null;
#if REVIT2024_OR_GREATER
            var bic = (BuiltInCategory)value;
#else
            var bic = (BuiltInCategory)(int)value;
#endif
            return Enum.IsDefined(typeof(BuiltInCategory), bic) ? bic : (BuiltInCategory?)null;
        }

        /// <summary>The same category (by built-in id, else by name) in another document; subcategories by parent + name.</summary>
        public static Category FindCategory(Document doc, Category like)
        {
            if (like.Parent == null)
            {
                var bic = BicOf(like);
                if (bic.HasValue)
                {
                    try
                    {
                        return doc.Settings.Categories.get_Item(bic.Value);
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }
                return doc.Settings.Categories.Cast<Category>().FirstOrDefault(c => string.Equals(c.Name, like.Name, StringComparison.OrdinalIgnoreCase));
            }

            var parent = FindCategory(doc, like.Parent);
            return parent?.SubCategories.Cast<Category>().FirstOrDefault(c => string.Equals(c.Name, like.Name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Identity of an item across documents: types by family name + name, fill patterns by target + name.</summary>
        public static string Key(Element element)
        {
            switch (element)
            {
                case FillPatternElement fill:
                    return "fill|" + fill.GetFillPattern().Target + "|" + fill.Name.ToUpperInvariant();
                case View view:
                    return "template|" + view.Name.ToUpperInvariant();
                case ElementType type:
                    return "type|" + type.FamilyName + "|" + type.Name.ToUpperInvariant();
                default:
                    return element.GetType().Name + "|" + element.Name.ToUpperInvariant();
            }
        }

        public static Dictionary<string, Element> Index(Document doc, string kind)
        {
            var index = new Dictionary<string, Element>();
            foreach (var element in Collect(doc, kind))
            {
                var key = Key(element);
                if (!index.ContainsKey(key))
                    index[key] = element;
            }
            return index;
        }

        // ---------------------------------------------------------------- graphics values

        public static JArray Rgb(Color color)
        {
            return color != null && color.IsValid ? new JArray(color.Red, color.Green, color.Blue) : null;
        }

        /// <summary>Revit stores colour parameters as R + G*256 + B*65536.</summary>
        public static JArray RgbFromInt(int value)
        {
            return new JArray(value & 0xFF, (value >> 8) & 0xFF, (value >> 16) & 0xFF);
        }

        public static int IntFromRgb(JArray rgb)
        {
            return rgb.Value<int>(0) | (rgb.Value<int>(1) << 8) | (rgb.Value<int>(2) << 16);
        }

        public static string LinePatternName(Document doc, ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId)
                return null;
            if (id == LinePatternElement.GetSolidPatternId())
                return "Solid";
            return doc.GetElement(id)?.Name;
        }

        public static string ElementName(Document doc, ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId)
                return null;
            var element = doc.GetElement(id);
            if (element is FamilySymbol symbol)
                return symbol.FamilyName + " : " + symbol.Name;
            return element?.Name;
        }

        /// <summary>The graphics of a category or subcategory as stored in Object Styles / Line Styles.</summary>
        public static JObject CategoryGraphics(Document doc, Category category)
        {
            var result = new JObject();
            try
            {
                var weight = category.GetLineWeight(GraphicsStyleType.Projection);
                if (weight.HasValue)
                    result["weight"] = weight.Value;
                var cutWeight = category.GetLineWeight(GraphicsStyleType.Cut);
                if (cutWeight.HasValue)
                    result["cutWeight"] = cutWeight.Value;
                if (Rgb(category.LineColor) is JArray color)
                    result["color"] = color;
                if (LinePatternName(doc, category.GetLinePatternId(GraphicsStyleType.Projection)) is string pattern)
                    result["pattern"] = pattern;
                if (category.Material != null)
                    result["material"] = category.Material.Name;
            }
            catch (Exception)
            {
                // Some internal categories reject graphics queries; report what was read.
            }
            return result;
        }

        /// <summary>Non-default graphic overrides, using the override_graphics field names.</summary>
        public static JObject Overrides(Document doc, OverrideGraphicSettings o)
        {
            var result = new JObject();
            if (o == null)
                return result;
            if (o.Halftone)
                result["halftone"] = true;
            if (o.Transparency > 0)
                result["transparency"] = o.Transparency;
            AddColor(result, "projectionLineColor", o.ProjectionLineColor);
            if (o.ProjectionLineWeight != OverrideGraphicSettings.InvalidPenNumber)
                result["projectionLineWeight"] = o.ProjectionLineWeight;
            AddName(result, "projectionLinePattern", LinePatternName(doc, o.ProjectionLinePatternId));
            AddColor(result, "cutLineColor", o.CutLineColor);
            if (o.CutLineWeight != OverrideGraphicSettings.InvalidPenNumber)
                result["cutLineWeight"] = o.CutLineWeight;
            AddName(result, "cutLinePattern", LinePatternName(doc, o.CutLinePatternId));
            AddFill(doc, result, "surfaceForeground", o.SurfaceForegroundPatternId, o.SurfaceForegroundPatternColor, o.IsSurfaceForegroundPatternVisible);
            AddFill(doc, result, "surfaceBackground", o.SurfaceBackgroundPatternId, o.SurfaceBackgroundPatternColor, o.IsSurfaceBackgroundPatternVisible);
            AddFill(doc, result, "cutForeground", o.CutForegroundPatternId, o.CutForegroundPatternColor, o.IsCutForegroundPatternVisible);
            AddFill(doc, result, "cutBackground", o.CutBackgroundPatternId, o.CutBackgroundPatternColor, o.IsCutBackgroundPatternVisible);
            if (o.DetailLevel != ViewDetailLevel.Undefined)
                result["detailLevel"] = o.DetailLevel.ToString();
            return result;
        }

        private static void AddColor(JObject target, string name, Color color)
        {
            if (Rgb(color) is JArray rgb)
                target[name] = rgb;
        }

        private static void AddName(JObject target, string name, string value)
        {
            if (value != null)
                target[name] = value;
        }

        private static void AddFill(Document doc, JObject target, string prefix, ElementId patternId, Color color, bool visible)
        {
            AddName(target, prefix + "Pattern", ElementName(doc, patternId));
            AddColor(target, prefix + "Color", color);
            if (!visible)
                target[prefix + "Visible"] = false;
        }

        // ---------------------------------------------------------------- parameters

        public static bool IsLength(Definition definition)
        {
            try
            {
#if REVIT2022_OR_GREATER
                return definition.GetDataType() == SpecTypeId.Length;
#else
                return definition.ParameterType == ParameterType.Length;
#endif
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsAngle(Definition definition)
        {
            try
            {
#if REVIT2022_OR_GREATER
                return definition.GetDataType() == SpecTypeId.Angle;
#else
                return definition.ParameterType == ParameterType.Angle;
#endif
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsYesNo(Definition definition)
        {
            try
            {
#if REVIT2022_OR_GREATER
                return definition.GetDataType() == SpecTypeId.Boolean.YesNo;
#else
                return definition.ParameterType == ParameterType.YesNo;
#endif
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static BuiltInParameter BuiltIn(Parameter parameter)
        {
            return (parameter.Definition as InternalDefinition)?.BuiltInParameter ?? BuiltInParameter.INVALID;
        }

        /// <summary>
        ///     A parameter value in tool units: lengths in mm, angles in degrees, Yes/No as bool,
        ///     colour integers as [r,g,b], element ids as the referenced element's name.
        /// </summary>
        public static JToken Value(Document doc, Parameter parameter)
        {
            if (parameter == null || !parameter.HasValue)
                return null;
            switch (parameter.StorageType)
            {
                case StorageType.Double:
                    var number = parameter.AsDouble();
                    if (IsLength(parameter.Definition))
                        return DocumentationUtils.FeetToMm(number);
                    if (IsAngle(parameter.Definition))
                        return Math.Round(number * 180.0 / Math.PI, 4);
                    return Math.Round(number, 6);
                case StorageType.Integer:
                    var integer = parameter.AsInteger();
                    if (IsYesNo(parameter.Definition))
                        return integer != 0;
                    if (BuiltIn(parameter).ToString().Contains("COLOR"))
                        return RgbFromInt(integer);
                    return integer;
                case StorageType.String:
                    return parameter.AsString();
                case StorageType.ElementId:
                    var id = parameter.AsElementId();
                    if (id == ElementId.InvalidElementId)
                        return null;
                    if (doc.GetElement(id) is LinePatternElement || (id == LinePatternElement.GetSolidPatternId() && BuiltIn(parameter).ToString().Contains("PATTERN")))
                        return LinePatternName(doc, id);
                    return ElementName(doc, id) ?? (JToken)id.GetValue();
                default:
                    return null;
            }
        }

        public static JToken BuiltInValue(Document doc, Element element, BuiltInParameter bip)
        {
            return Value(doc, element.get_Parameter(bip));
        }

        /// <summary>All writable parameters by display name (tool units). Read-only and empty values are omitted.</summary>
        public static JObject WritableParameters(Document doc, Element element)
        {
            var result = new JObject();
            foreach (Parameter parameter in element.Parameters)
            {
                if (parameter.IsReadOnly || parameter.Definition == null)
                    continue;
                var name = parameter.Definition.Name;
                if (string.IsNullOrEmpty(name) || result.ContainsKey(name))
                    continue;
                var value = Value(doc, parameter);
                if (value != null && !(value.Type == JTokenType.String && string.IsNullOrEmpty(value.ToString())))
                    result[name] = value;
            }
            return Sorted(result);
        }

        private static JObject Sorted(JObject source)
        {
            return new JObject(source.Properties().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase));
        }

        public static string ParameterName(Document doc, ElementId id)
        {
            var value = id.GetValue();
            if (value < 0)
            {
                try
                {
#if REVIT2024_OR_GREATER
                    return LabelUtils.GetLabelFor((BuiltInParameter)value);
#else
                    return LabelUtils.GetLabelFor((BuiltInParameter)(int)value);
#endif
                }
                catch (Exception)
                {
                    return value.ToString();
                }
            }
            var element = doc.GetElement(id);
            return (element as ParameterElement)?.GetDefinition()?.Name ?? element?.Name;
        }

        public static string BuiltInParameterName(ElementId id)
        {
            var value = id.GetValue();
            if (value >= 0)
                return null;
#if REVIT2024_OR_GREATER
            var bip = (BuiltInParameter)value;
#else
            var bip = (BuiltInParameter)(int)value;
#endif
            return Enum.IsDefined(typeof(BuiltInParameter), bip) ? bip.ToString() : null;
        }

        // ---------------------------------------------------------------- filters

        /// <summary>
        ///     A view filter's rules in create_view_filter vocabulary:
        ///     { logic: "And"|"Or", rules: [{ parameter, builtInParameter, operator, value }], groups: [nested] }.
        /// </summary>
        public static JObject DescribeFilter(Document doc, ElementFilter filter, ICollection<ElementId> categoryIds)
        {
            if (filter == null)
                return null;
            var result = new JObject();
            var rules = new JArray();
            var groups = new JArray();
            IList<ElementFilter> children;
            switch (filter)
            {
                case LogicalAndFilter and:
                    result["logic"] = "And";
                    children = and.GetFilters();
                    break;
                case LogicalOrFilter or:
                    result["logic"] = "Or";
                    children = or.GetFilters();
                    break;
                default:
                    result["logic"] = "And";
                    children = new List<ElementFilter> { filter };
                    break;
            }

            foreach (var child in children)
            {
                if (child is ElementParameterFilter parameterFilter)
                {
                    var childRules = parameterFilter.GetRules();
                    if (childRules.Count == 1 || result.Value<string>("logic") == "And")
                    {
                        foreach (var rule in childRules)
                            rules.Add(DescribeRule(doc, rule, parameterFilter.Inverted, categoryIds));
                    }
                    else
                    {
                        groups.Add(new JObject
                        {
                            ["logic"] = "And",
                            ["rules"] = new JArray(childRules.Select(r => DescribeRule(doc, r, parameterFilter.Inverted, categoryIds)))
                        });
                    }
                }
                else if (child is LogicalAndFilter || child is LogicalOrFilter)
                {
                    groups.Add(DescribeFilter(doc, child, categoryIds));
                }
                else
                {
                    rules.Add(new JObject { ["operator"] = child.GetType().Name });
                }
            }

            result["rules"] = rules;
            if (groups.Count > 0)
                result["groups"] = groups;
            return result;
        }

        private static readonly Dictionary<string, string> Inverse = new Dictionary<string, string>
        {
            ["Equals"] = "NotEquals", ["NotEquals"] = "Equals", ["Contains"] = "NotContains", ["NotContains"] = "Contains",
            ["BeginsWith"] = "NotBeginsWith", ["NotBeginsWith"] = "BeginsWith", ["EndsWith"] = "NotEndsWith",
            ["NotEndsWith"] = "EndsWith", ["Greater"] = "LessOrEqual", ["LessOrEqual"] = "Greater",
            ["GreaterOrEqual"] = "Less", ["Less"] = "GreaterOrEqual", ["HasValue"] = "HasNoValue", ["HasNoValue"] = "HasValue"
        };

        private static JObject DescribeRule(Document doc, FilterRule rule, bool inverted, ICollection<ElementId> categoryIds)
        {
            if (rule is FilterInverseRule inverse)
                return DescribeRule(doc, inverse.GetInnerRule(), !inverted, categoryIds);

            var result = new JObject();
            ElementId parameterId = null;
            try
            {
                parameterId = rule.GetRuleParameter();
            }
            catch (Exception)
            {
                // Category / shared-parameter-applicable rules have no single parameter.
            }
            if (parameterId != null && parameterId != ElementId.InvalidElementId)
            {
                result["parameter"] = ParameterName(doc, parameterId);
                if (BuiltInParameterName(parameterId) is string bip)
                    result["builtInParameter"] = bip;
            }

            string op;
            JToken value = null;
            switch (rule)
            {
                case HasValueFilterRule _:
                    op = "HasValue";
                    break;
                case HasNoValueFilterRule _:
                    op = "HasNoValue";
                    break;
                case FilterStringRule s:
                    op = Operator(s.GetEvaluator()?.GetType().Name);
                    value = s.RuleString;
                    break;
                case FilterDoubleRule d:
                    op = Operator(d.GetEvaluator()?.GetType().Name);
                    if (IsLengthParameter(doc, parameterId, categoryIds))
                    {
                        value = DocumentationUtils.FeetToMm(d.RuleValue);
                        result["unit"] = "mm";
                    }
                    else
                    {
                        value = d.RuleValue;
                    }
                    break;
                case FilterIntegerRule i:
                    op = Operator(i.GetEvaluator()?.GetType().Name);
                    value = i.RuleValue;
                    break;
                case FilterElementIdRule e:
                    op = Operator(e.GetEvaluator()?.GetType().Name);
                    value = e.RuleValue.GetValue();
                    if (ElementName(doc, e.RuleValue) is string referenced)
                        result["valueName"] = referenced;
                    break;
                case SharedParameterApplicableRule shared:
                    op = "ParameterExists";
                    result["parameter"] = shared.ParameterName;
                    break;
                default:
                    op = rule.GetType().Name;
                    break;
            }

            if (inverted && Inverse.TryGetValue(op, out var flipped))
                op = flipped;
            else if (inverted)
                result["inverted"] = true;
            result["operator"] = op;
            if (value != null)
                result["value"] = value;
            return result;
        }

        private static string Operator(string evaluator)
        {
            if (string.IsNullOrEmpty(evaluator))
                return "Unknown";
            foreach (var prefix in new[] { "FilterString", "FilterNumeric" })
                if (evaluator.StartsWith(prefix, StringComparison.Ordinal))
                    return evaluator.Substring(prefix.Length);
            return evaluator;
        }

        private static bool IsLengthParameter(Document doc, ElementId parameterId, ICollection<ElementId> categoryIds)
        {
            if (parameterId == null)
                return false;
            if (doc.GetElement(parameterId) is ParameterElement parameterElement)
                return IsLength(parameterElement.GetDefinition());

            // Built-in parameter: sample an element of the filter's categories that has it.
            var value = parameterId.GetValue();
#if REVIT2024_OR_GREATER
            var bip = (BuiltInParameter)value;
#else
            var bip = (BuiltInParameter)(int)value;
#endif
            foreach (var categoryId in categoryIds ?? new List<ElementId>())
            {
                var sample = new FilteredElementCollector(doc).OfCategoryId(categoryId).Cast<Element>().Take(50)
                    .Select(e => e.get_Parameter(bip) ?? doc.GetElement(e.GetTypeId())?.get_Parameter(bip))
                    .FirstOrDefault(p => p != null);
                if (sample != null)
                    return IsLength(sample.Definition);
            }
            return false;
        }
    }
}
