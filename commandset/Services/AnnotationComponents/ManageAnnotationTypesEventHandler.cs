using System.Globalization;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Lists, creates (duplicates), updates and sets the default of the
    ///     annotation types that define a project's drawing style: text note,
    ///     dimension, spot elevation, grid, level, viewport and arrowhead types.
    ///     Friendly setting keys map to BuiltInParameters with a parameter-name
    ///     fallback; lengths are millimetres, angles degrees. Each type item is
    ///     applied atomically (all settings or none) inside one undoable
    ///     transaction. list is read-only and opens no transaction.
    /// </summary>
    public class ManageAnnotationTypesEventHandler : JsonParameterEventHandler
    {
        private static readonly string[] Kinds = { "text", "dimension", "spotElevation", "grid", "level", "viewport", "arrowhead" };

        private static readonly Dictionary<string, List<Setting>> SettingsByKind = BuildSettings();

        public override string GetName() => "Manage Annotation Types";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var action = (parameters.Value<string>("action") ?? "list").Trim();
            var kind = parameters.Value<string>("kind")?.Trim();
            if (kind != null && !Kinds.Contains(kind))
                return Fail($"Unknown kind '{kind}'. Expected one of: {string.Join(", ", Kinds)}.");

            switch (action)
            {
                case "list":
                    return List(doc, parameters, kind);
                case "create":
                case "update":
                case "setDefault":
                    if (kind == null)
                        return Fail($"'kind' is required for {action}.");
                    if (action == "setDefault" && kind == "arrowhead")
                        return Fail("Arrowhead types have no document default.");
                    var items = DocumentationUtils.RequireArray(parameters, "types");
                    var results = DocumentationUtils.RunBatch(doc, $"MCP: {action} {kind} types", items,
                        item => Apply(doc, action, kind, (JObject)item));
                    return Ok(
                        $"{action} {kind}: {results.Count(r => r.Value<bool>("success"))} of {results.Count} types succeeded.",
                        DocumentationUtils.Summarize(results));
                default:
                    return Fail("'action' must be list, create, update or setDefault.");
            }
        }

        // ------------------------------------------------------------------ list

        private static AIResult<object> List(Document doc, JObject parameters, string kind)
        {
            var nameContains = parameters.Value<string>("nameContains")?.Trim();
            var style = parameters.Value<string>("style")?.Trim();
            var includeAll = parameters.Value<bool?>("includeAllParameters") == true;
            var includeChoices = parameters.Value<bool?>("includeChoices") != false;
            var kinds = kind != null ? new[] { kind } : Kinds;

            var result = new JObject();
            var counts = new JObject();
            foreach (var k in kinds)
            {
                var types = CollectTypes(doc, k, style)
                    .Where(t => string.IsNullOrEmpty(nameContains)
                                || t.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                var list = new JArray();
                foreach (var type in types)
                    list.Add(Describe(doc, k, type, includeAll));
                result[k] = list;
                counts[k] = list.Count;
            }

            if (includeChoices)
                result["choices"] = Choices(doc, kinds);

            return Ok($"Listed annotation types ({string.Join(", ", counts.Properties().Select(p => $"{p.Name}: {p.Value}"))}).",
                result);
        }

        private static JObject Describe(Document doc, string kind, ElementType type, bool includeAll)
        {
            var item = new JObject
            {
                ["id"] = type.Id.GetValue(),
                ["name"] = type.Name
            };
            if (!string.IsNullOrEmpty(type.FamilyName))
                item["familyName"] = type.FamilyName;
            if (type is DimensionType dimensionType)
                item["style"] = dimensionType.StyleType.ToString();

            var group = DefaultGroup(kind, type);
            if (group.HasValue)
            {
                try
                {
                    item["isDefault"] = doc.GetDefaultElementTypeId(group.Value) == type.Id;
                }
                catch (Exception)
                {
                    // Some groups have no default in some documents.
                }
            }

            var values = new JObject();
            foreach (var setting in SettingsByKind[kind])
            {
                if (setting.Kind == ValueKind.Units)
                    continue;
                var parameter = FindParameter(type, setting);
                if (parameter == null)
                    continue;
                values[setting.Key] = ReadValue(doc, parameter, setting);
            }

            if (type is DimensionType dim && SettingsByKind[kind].Any(s => s.Kind == ValueKind.Units))
                values["units"] = ReadUnits(dim);

            item["values"] = values;

            if (includeAll)
            {
                var all = new JObject();
                foreach (Parameter parameter in type.Parameters)
                {
                    var name = parameter.Definition?.Name;
                    if (string.IsNullOrEmpty(name) || all[name] != null)
                        continue;
                    all[name] = parameter.StorageType == StorageType.String
                        ? parameter.AsString()
                        : parameter.AsValueString() ?? RawValue(parameter);
                }

                item["parameters"] = all;
            }

            return item;
        }

        private static JObject Choices(Document doc, IEnumerable<string> kinds)
        {
            var set = new HashSet<string>(kinds);
            var choices = new JObject();
            if (set.Overlaps(new[] { "text", "dimension", "spotElevation" }))
                choices["arrowheads"] = new JArray(ArrowheadTypes(doc).Select(t => t.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            if (set.Contains("grid"))
                choices["gridBubbles"] = new JArray(SymbolNames(doc, BuiltInCategory.OST_GridHeads));
            if (set.Contains("level"))
                choices["levelHeads"] = new JArray(SymbolNames(doc, BuiltInCategory.OST_LevelHeads));
            if (set.Contains("viewport"))
                choices["viewportTitles"] = new JArray(SymbolNames(doc, BuiltInCategory.OST_ViewportLabel));
            if (set.Contains("spotElevation"))
                choices["spotElevationSymbols"] = new JArray(SymbolNames(doc, BuiltInCategory.OST_SpotElevSymbols));
            if (set.Overlaps(new[] { "dimension", "grid", "level", "viewport" }))
            {
                var patterns = new List<string> { "Solid" };
                patterns.AddRange(new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement))
                    .Select(e => e.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(200));
                choices["linePatterns"] = new JArray(patterns);
            }

            return choices;
        }

        private static IEnumerable<string> SymbolNames(Document doc, BuiltInCategory category)
        {
            return new FilteredElementCollector(doc).OfCategory(category).WhereElementIsElementType()
                .OfType<ElementType>()
                .Select(TypeLabel)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        }

        // ----------------------------------------------------------- create/update

        private static object Apply(Document doc, string action, string kind, JObject item)
        {
            var name = item.Value<string>("name")?.Trim();
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("'name' is required.");

            var style = item.Value<string>("style")?.Trim();
            var allOfKind = CollectTypes(doc, kind, null).ToList();
            var existing = allOfKind.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal))
                           ?? allOfKind.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            string status;
            ElementType type;

            if (action == "create")
            {
                var ifExists = (item.Value<string>("ifExists") ?? "update").Trim();
                if (existing != null)
                {
                    if (ifExists == "error")
                        throw new ArgumentException($"A {kind} type named '{name}' already exists (id {existing.Id.GetValue()}).");
                    if (ifExists == "reuse")
                    {
                        return new JObject
                        {
                            ["id"] = existing.Id.GetValue(),
                            ["name"] = existing.Name,
                            ["status"] = "reused"
                        };
                    }

                    type = existing;
                    status = "updated";
                }
                else
                {
                    var source = ResolveSource(doc, kind, item.Value<string>("sourceName"), style);
                    type = source.Duplicate(name) ?? throw new InvalidOperationException($"Could not duplicate '{source.Name}'.");
                    status = "created";
                }
            }
            else
            {
                type = existing ?? throw new ArgumentException(
                    $"{kind} type '{name}' not found. Available: {string.Join(", ", allOfKind.Select(t => t.Name).Take(80))}.");
                status = action == "setDefault" ? "defaultSet" : "updated";
            }

            var applied = new JArray();
            var errors = new List<string>();
            var warnings = new JArray();

            if (action != "setDefault")
            {
                var rename = item.Value<string>("rename")?.Trim();
                if (!string.IsNullOrEmpty(rename) && rename != type.Name)
                {
                    if (allOfKind.Any(t => t.Id != type.Id && string.Equals(t.Name, rename, StringComparison.OrdinalIgnoreCase)))
                        errors.Add($"Cannot rename to '{rename}': another {kind} type has that name.");
                    else
                    {
                        type.Name = rename;
                        applied.Add("rename");
                    }
                }

                if (item["settings"] is JObject settings)
                    ApplySettings(doc, kind, type, settings, applied, errors, warnings);

                if (item["parameters"] is JObject raw)
                {
                    foreach (var property in raw.Properties())
                    {
                        var error = DocumentationUtils.SetParameterValue(type, property.Name, property.Value);
                        if (error == null)
                            applied.Add(property.Name);
                        else
                            errors.Add(error.Contains("not found") ? $"{error} {AvailableParameters(type)}" : error);
                    }
                }
            }

            if (errors.Count > 0)
                throw new ArgumentException($"'{name}': {string.Join(" | ", errors)}");

            if (action == "setDefault" || item.Value<bool?>("setAsDefault") == true)
            {
                var group = DefaultGroup(kind, type)
                            ?? throw new ArgumentException($"{kind} type '{type.Name}' has no document default group.");
                if (!doc.IsDefaultElementTypeIdValid(group, type.Id))
                    throw new ArgumentException($"'{type.Name}' cannot be the default {group}.");
                doc.SetDefaultElementTypeId(group, type.Id);
                applied.Add($"default:{group}");
            }

            var result = Describe(doc, kind, type, false);
            result["status"] = status;
            result["applied"] = applied;
            if (warnings.Count > 0)
                result["warnings"] = warnings;
            return result;
        }

        private static ElementType ResolveSource(Document doc, string kind, string sourceName, string style)
        {
            var candidates = CollectTypes(doc, kind, style).ToList();
            if (candidates.Count == 0)
                candidates = CollectTypes(doc, kind, null).ToList();
            if (candidates.Count == 0)
                throw new ArgumentException($"The document has no {kind} type to duplicate.");

            if (string.IsNullOrWhiteSpace(sourceName))
            {
                if (kind == "dimension" && string.IsNullOrEmpty(style))
                    return candidates.OfType<DimensionType>().FirstOrDefault(d => d.StyleType == DimensionStyleType.Linear)
                           ?? candidates[0];
                return candidates[0];
            }

            return candidates.FirstOrDefault(t => string.Equals(t.Name, sourceName, StringComparison.Ordinal))
                   ?? candidates.FirstOrDefault(t => string.Equals(t.Name, sourceName, StringComparison.OrdinalIgnoreCase))
                   ?? CollectTypes(doc, kind, null).FirstOrDefault(t => string.Equals(t.Name, sourceName, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException(
                       $"Source {kind} type '{sourceName}' not found. Available: {string.Join(", ", candidates.Select(t => t.Name).Take(80))}.");
        }

        private static void ApplySettings(Document doc, string kind, ElementType type, JObject settings, JArray applied,
            List<string> errors, JArray warnings)
        {
            var specs = SettingsByKind[kind];

            // "Filled Arrow" is Arrow style with Fill Tick on.
            if (kind == "arrowhead" && settings["style"]?.Type == JTokenType.String)
            {
                var style = Normalize(settings.Value<string>("style"));
                if (style == "filledarrow" || style == "closedarrow")
                {
                    settings = (JObject)settings.DeepClone();
                    settings["style"] = "Arrow";
                    if (style == "filledarrow" && settings["filled"] == null)
                        settings["filled"] = true;
                    if (style == "closedarrow" && settings["closed"] == null)
                        settings["closed"] = true;
                }
            }

            foreach (var property in settings.Properties())
            {
                var setting = specs.FirstOrDefault(s => s.Key == property.Name);
                if (setting == null)
                {
                    errors.Add($"Setting '{property.Name}' does not apply to {kind} types. Valid: {string.Join(", ", specs.Select(s => s.Key))}.");
                    continue;
                }

                try
                {
                    if (setting.Kind == ValueKind.Units)
                    {
                        if (!(type is DimensionType dimensionType))
                            throw new ArgumentException("units only apply to dimension and spot types.");
                        if (!(property.Value is JObject unitsSpec))
                            throw new ArgumentException("units must be an object.");
                        ApplyUnits(doc, dimensionType, unitsSpec, warnings);
                        applied.Add(setting.Key);
                        continue;
                    }

                    var parameter = FindParameter(type, setting);
                    if (parameter == null)
                    {
                        errors.Add($"'{setting.Key}' ({string.Join(" / ", setting.Names)}) is not a parameter of '{type.Name}'. {AvailableParameters(type)}");
                        continue;
                    }

                    if (parameter.IsReadOnly)
                    {
                        errors.Add($"'{setting.Key}' ({parameter.Definition.Name}) is read-only on '{type.Name}'.");
                        continue;
                    }

                    WriteValue(doc, parameter, setting, property.Value);
                    applied.Add(setting.Key);
                }
                catch (Exception ex)
                {
                    errors.Add($"'{setting.Key}': {ex.Message}");
                }
            }
        }

        // -------------------------------------------------------------- values

        private static void WriteValue(Document doc, Parameter parameter, Setting setting, JToken value)
        {
            bool ok;
            switch (setting.Kind)
            {
                case ValueKind.Length:
                    ok = parameter.Set(DocumentationUtils.MmToFeet(RequireNumber(value)));
                    break;
                case ValueKind.Number:
                    ok = parameter.Set(RequireNumber(value));
                    break;
                case ValueKind.Angle:
                    ok = parameter.Set(RequireNumber(value) * Math.PI / 180.0);
                    break;
                case ValueKind.Integer:
                    ok = parameter.Set((int)Math.Round(RequireNumber(value)));
                    break;
                case ValueKind.Bool:
                    ok = parameter.Set(ReadBool(value) ? 1 : 0);
                    break;
                case ValueKind.Text:
                    ok = parameter.Set(value?.Type == JTokenType.Null ? string.Empty : value?.ToString() ?? string.Empty);
                    break;
                case ValueKind.Color:
                    ok = parameter.Set(ParseColor(value));
                    break;
                case ValueKind.LinePattern:
                    ok = parameter.Set(ResolveLinePattern(doc, value));
                    break;
                case ValueKind.TypeRef:
                    ok = parameter.Set(ResolveTypeRef(doc, parameter, setting, value));
                    break;
                case ValueKind.Choice:
                    ok = SetChoice(parameter, setting, value);
                    break;
                default:
                    ok = false;
                    break;
            }

            if (!ok)
                throw new ArgumentException($"Revit rejected the value {value?.ToString(Newtonsoft.Json.Formatting.None)} for '{parameter.Definition.Name}'.");
        }

        private static JToken ReadValue(Document doc, Parameter parameter, Setting setting)
        {
            try
            {
                switch (setting.Kind)
                {
                    case ValueKind.Length:
                        return DocumentationUtils.FeetToMm(parameter.AsDouble());
                    case ValueKind.Number:
                        return Math.Round(parameter.AsDouble(), 4);
                    case ValueKind.Angle:
                        return Math.Round(parameter.AsDouble() * 180.0 / Math.PI, 3);
                    case ValueKind.Integer:
                        return parameter.AsInteger();
                    case ValueKind.Bool:
                        return parameter.AsInteger() != 0;
                    case ValueKind.Text:
                        return parameter.AsString();
                    case ValueKind.Color:
                        var c = parameter.AsInteger();
                        return $"#{c & 0xFF:X2}{(c >> 8) & 0xFF:X2}{(c >> 16) & 0xFF:X2}";
                    case ValueKind.LinePattern:
                        var patternId = parameter.AsElementId();
                        if (patternId == LinePatternElement.GetSolidPatternId())
                            return "Solid";
                        return patternId == ElementId.InvalidElementId ? null : doc.GetElement(patternId)?.Name;
                    case ValueKind.TypeRef:
                        var id = parameter.AsElementId();
                        if (id == null || id == ElementId.InvalidElementId)
                            return "none";
                        var element = doc.GetElement(id);
                        return element is ElementType et ? (setting.Arrowhead ? et.Name : TypeLabel(et)) : element?.Name;
                    case ValueKind.Choice:
                        return parameter.AsValueString() ?? parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
                    default:
                        return null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string RawValue(Parameter parameter)
        {
            switch (parameter.StorageType)
            {
                case StorageType.Integer:
                    return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
                case StorageType.Double:
                    return parameter.AsDouble().ToString(CultureInfo.InvariantCulture);
                case StorageType.ElementId:
                    return parameter.AsElementId().GetValue().ToString(CultureInfo.InvariantCulture);
                default:
                    return parameter.AsString();
            }
        }

        private static double RequireNumber(JToken value)
        {
            if (value != null && (value.Type == JTokenType.Float || value.Type == JTokenType.Integer))
                return value.Value<double>();
            if (value != null && value.Type == JTokenType.String &&
                double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            throw new ArgumentException($"expected a number, got {value?.ToString(Newtonsoft.Json.Formatting.None) ?? "nothing"}.");
        }

        private static bool ReadBool(JToken value)
        {
            if (value?.Type == JTokenType.Boolean)
                return value.Value<bool>();
            if (value?.Type == JTokenType.Integer)
                return value.Value<int>() != 0;
            var text = Normalize(value?.ToString());
            if (text == "yes" || text == "true" || text == "on")
                return true;
            if (text == "no" || text == "false" || text == "off")
                return false;
            throw new ArgumentException($"expected true/false, got {value?.ToString(Newtonsoft.Json.Formatting.None)}.");
        }

        private static int ParseColor(JToken value)
        {
            int r, g, b;
            if (value is JObject obj)
            {
                r = obj.Value<int?>("r") ?? throw new ArgumentException("color needs r, g, b.");
                g = obj.Value<int?>("g") ?? throw new ArgumentException("color needs r, g, b.");
                b = obj.Value<int?>("b") ?? throw new ArgumentException("color needs r, g, b.");
            }
            else
            {
                var text = value?.ToString().Trim() ?? string.Empty;
                var lower = text.ToLowerInvariant();
                if (lower == "black") text = "#000000";
                else if (lower == "white") text = "#FFFFFF";
                else if (lower == "red") text = "#FF0000";
                else if (lower == "blue") text = "#0000FF";
                else if (lower == "green") text = "#00FF00";

                if (text.StartsWith("#"))
                    text = text.Substring(1);
                if (text.Length == 6 && int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
                {
                    r = (hex >> 16) & 0xFF;
                    g = (hex >> 8) & 0xFF;
                    b = hex & 0xFF;
                }
                else
                {
                    var parts = text.Split(',');
                    if (parts.Length != 3 ||
                        !int.TryParse(parts[0].Trim(), out r) || !int.TryParse(parts[1].Trim(), out g) ||
                        !int.TryParse(parts[2].Trim(), out b))
                        throw new ArgumentException($"color '{value}' must be '#RRGGBB', 'r,g,b' or {{r,g,b}}.");
                }
            }

            if (r < 0 || r > 255 || g < 0 || g > 255 || b < 0 || b > 255)
                throw new ArgumentException("color components must be 0-255.");
            return r + (g << 8) + (b << 16);
        }

        private static ElementId ResolveLinePattern(Document doc, JToken value)
        {
            if (value?.Type == JTokenType.Integer)
                return value.Value<long>().ToRevitElementId();
            var name = value?.ToString().Trim();
            if (string.IsNullOrEmpty(name) || string.Equals(name, "solid", StringComparison.OrdinalIgnoreCase))
                return LinePatternElement.GetSolidPatternId();
            var patterns = new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement)).ToList();
            var match = patterns.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match.Id;
            throw new ArgumentException(
                $"Line pattern '{name}' not found. Available: Solid, {string.Join(", ", patterns.Select(p => p.Name).OrderBy(n => n).Take(80))}.");
        }

        private static ElementId ResolveTypeRef(Document doc, Parameter parameter, Setting setting, JToken value)
        {
            if (value == null || value.Type == JTokenType.Null)
                return ElementId.InvalidElementId;
            if (value.Type == JTokenType.Integer)
                return value.Value<long>().ToRevitElementId();

            var name = value.ToString().Trim();
            if (name.Length == 0 || string.Equals(name, "none", StringComparison.OrdinalIgnoreCase))
                return ElementId.InvalidElementId;

            List<ElementType> candidates;
            if (setting.Arrowhead)
                candidates = ArrowheadTypes(doc).ToList();
            else if (setting.RefCategory.HasValue)
                candidates = new FilteredElementCollector(doc).OfCategory(setting.RefCategory.Value)
                    .WhereElementIsElementType().OfType<ElementType>().ToList();
            else
            {
                var current = doc.GetElement(parameter.AsElementId());
                candidates = current?.Category != null
                    ? new FilteredElementCollector(doc).OfCategoryId(current.Category.Id).WhereElementIsElementType()
                        .OfType<ElementType>().ToList()
                    : new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfType<ElementType>().ToList();
                if (current?.Category != null && !candidates.Any(c => MatchesTypeName(c, name)))
                    candidates = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfType<ElementType>().ToList();
            }

            var match = candidates.FirstOrDefault(c => MatchesTypeName(c, name));
            if (match != null)
                return match.Id;

            var available = candidates.Select(c => setting.Arrowhead ? c.Name : TypeLabel(c))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(80);
            throw new ArgumentException($"'{name}' not found. Available: {string.Join(", ", available)}.");
        }

        private static bool MatchesTypeName(ElementType type, string name)
        {
            if (string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
            var normalized = Normalize(name);
            return Normalize(TypeLabel(type)) == normalized || Normalize(type.FamilyName + type.Name) == normalized;
        }

        private static string TypeLabel(ElementType type)
        {
            return string.IsNullOrEmpty(type.FamilyName) ? type.Name : $"{type.FamilyName} : {type.Name}";
        }

        /// <summary>
        ///     Sets an enumerated integer parameter from its display label by
        ///     probing the values Revit accepts (the integer codes are not
        ///     documented), falling back to a known code table.
        /// </summary>
        private static bool SetChoice(Parameter parameter, Setting setting, JToken value)
        {
            if (parameter.StorageType != StorageType.Integer)
                throw new ArgumentException($"'{parameter.Definition.Name}' is not an enumerated parameter.");
            if (value?.Type == JTokenType.Integer)
                return parameter.Set(value.Value<int>());

            List<string> wanted;
            if (value?.Type == JTokenType.Boolean)
                wanted = value.Value<bool>()
                    ? new List<string> { "yes", "always", "on", "true" }
                    : new List<string> { "no", "never", "off", "false", "none" };
            else
                wanted = new List<string> { Normalize(value?.ToString()) };

            var original = parameter.AsInteger();
            var options = new List<KeyValuePair<int, string>>();
            for (var code = -1; code <= 20; code++)
            {
                try
                {
                    if (!parameter.Set(code))
                        continue;
                    var label = parameter.AsValueString();
                    if (string.IsNullOrWhiteSpace(label) || label == code.ToString(CultureInfo.InvariantCulture))
                        continue;
                    if (options.All(o => o.Value != label))
                        options.Add(new KeyValuePair<int, string>(code, label));
                }
                catch (Exception)
                {
                    // Not an accepted code.
                }
            }

            try
            {
                parameter.Set(original);
            }
            catch (Exception)
            {
                // Restored below by the final Set.
            }

            foreach (var w in wanted)
            {
                var hit = options.Where(o => Normalize(o.Value) == w).Select(o => (int?)o.Key).FirstOrDefault();
                if (hit.HasValue)
                    return parameter.Set(hit.Value);
            }

            if (setting.Choices != null)
            {
                foreach (var w in wanted)
                {
                    if (setting.Choices.TryGetValue(w, out var code))
                        return parameter.Set(code);
                }
            }

            var labels = options.Count > 0
                ? string.Join(", ", options.Select(o => o.Value))
                : setting.Choices != null ? string.Join(", ", setting.Choices.Keys) : "unknown";
            throw new ArgumentException($"'{value}' is not a valid choice for '{parameter.Definition.Name}'. Options: {labels}.");
        }

        private static string Normalize(string text)
        {
            return new string((text ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        private static Parameter FindParameter(Element element, Setting setting)
        {
            foreach (var bip in setting.Bips)
            {
                Parameter parameter;
                try
                {
                    parameter = element.get_Parameter(bip);
                }
                catch (Exception)
                {
                    parameter = null;
                }

                if (parameter != null)
                    return parameter;
            }

            foreach (var name in setting.Names)
            {
                var parameter = element.LookupParameter(name);
                if (parameter != null)
                    return parameter;
            }

            return null;
        }

        private static string AvailableParameters(Element element)
        {
            var names = element.Parameters.Cast<Parameter>()
                .Where(p => p.Definition != null && !p.IsReadOnly)
                .Select(p => p.Definition.Name)
                .Distinct()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
            return $"Editable parameters of this type: {string.Join(", ", names)}.";
        }

        // --------------------------------------------------------------- units

        private static JObject ReadUnits(DimensionType type)
        {
            var result = new JObject();
            try
            {
                var options = type.GetUnitsFormatOptions();
                result["useProjectSettings"] = options.UseDefault;
                if (options.UseDefault)
                    return result;
#if REVIT2022_OR_GREATER
                result["unit"] = UnitCode(options.GetUnitTypeId());
#else
                result["unit"] = UnitCode(options.DisplayUnits);
#endif
                result["accuracy"] = options.Accuracy;
                result["rounding"] = options.RoundingMethod.ToString();
                result["suppressTrailingZeros"] = options.SuppressTrailingZeros;
                result["suppressLeadingZeros"] = options.SuppressLeadingZeros;
                result["plusPrefix"] = options.UsePlusPrefix;
                result["digitGrouping"] = options.UseDigitGrouping;
            }
            catch (Exception ex)
            {
                result["error"] = ex.Message;
            }

            return result;
        }

        private static void ApplyUnits(Document doc, DimensionType type, JObject spec, JArray warnings)
        {
            if (spec.Value<bool?>("useProjectSettings") == true)
            {
                var reset = new FormatOptions(type.GetUnitsFormatOptions()) { UseDefault = true };
                type.SetUnitsFormatOptions(reset);
                return;
            }

            var angular = type.StyleType == DimensionStyleType.Angular;
            var unit = spec.Value<string>("unit")?.Trim();
            FormatOptions options;
            if (!string.IsNullOrEmpty(unit))
            {
                options = new FormatOptions(UnitFromCode(unit));
            }
            else
            {
                var current = type.GetUnitsFormatOptions();
                if (current.UseDefault)
                {
#if REVIT2022_OR_GREATER
                    current = doc.GetUnits().GetFormatOptions(angular ? SpecTypeId.Angle : SpecTypeId.Length);
#else
                    current = doc.GetUnits().GetFormatOptions(angular ? UnitType.UT_Angle : UnitType.UT_Length);
#endif
                }

                options = new FormatOptions(current);
            }

            options.UseDefault = false;

            var accuracy = spec.Value<double?>("accuracy");
            if (accuracy.HasValue)
            {
                if (!options.IsValidAccuracy(accuracy.Value))
                    throw new ArgumentException($"accuracy {accuracy.Value} is not valid for this unit (mm: 1, 0.5, 0.1; m: 0.001; ...).");
                options.Accuracy = accuracy.Value;
            }

            var rounding = spec.Value<string>("rounding");
            if (!string.IsNullOrWhiteSpace(rounding))
                options.RoundingMethod = DocumentationUtils.ParseEnum(rounding, RoundingMethod.Nearest);

            var trailing = spec.Value<bool?>("suppressTrailingZeros");
            if (trailing.HasValue)
            {
                if (options.CanSuppressTrailingZeros())
                    options.SuppressTrailingZeros = trailing.Value;
                else
                    warnings.Add("suppressTrailingZeros is not available for this unit; ignored.");
            }

            var leading = spec.Value<bool?>("suppressLeadingZeros");
            if (leading.HasValue)
            {
                if (options.CanSuppressLeadingZeros())
                    options.SuppressLeadingZeros = leading.Value;
                else
                    warnings.Add("suppressLeadingZeros is not available for this unit; ignored.");
            }

            var plus = spec.Value<bool?>("plusPrefix");
            if (plus.HasValue)
            {
                if (options.CanUsePlusPrefix())
                    options.UsePlusPrefix = plus.Value;
                else
                    warnings.Add("plusPrefix is not available for this unit; ignored.");
            }

            var grouping = spec.Value<bool?>("digitGrouping");
            if (grouping.HasValue)
                options.UseDigitGrouping = grouping.Value;

            type.SetUnitsFormatOptions(options);
        }

#if REVIT2022_OR_GREATER
        private static ForgeTypeId UnitFromCode(string code)
        {
            switch (Normalize(code))
            {
                case "mm": return UnitTypeId.Millimeters;
                case "cm": return UnitTypeId.Centimeters;
                case "m": return UnitTypeId.Meters;
                case "ft": return UnitTypeId.Feet;
                case "in": return UnitTypeId.Inches;
                case "ftin": return UnitTypeId.FeetFractionalInches;
                case "fracin": return UnitTypeId.FractionalInches;
                case "deg": return UnitTypeId.Degrees;
                case "degmin": return UnitTypeId.DegreesMinutes;
                default: throw new ArgumentException($"Unknown unit '{code}'. Use mm, cm, m, ft, in, ftin, fracIn, deg or degMin.");
            }
        }

        private static string UnitCode(ForgeTypeId id)
        {
            if (id == UnitTypeId.Millimeters) return "mm";
            if (id == UnitTypeId.Centimeters) return "cm";
            if (id == UnitTypeId.Meters) return "m";
            if (id == UnitTypeId.Feet) return "ft";
            if (id == UnitTypeId.Inches) return "in";
            if (id == UnitTypeId.FeetFractionalInches) return "ftin";
            if (id == UnitTypeId.FractionalInches) return "fracIn";
            if (id == UnitTypeId.Degrees) return "deg";
            if (id == UnitTypeId.DegreesMinutes) return "degMin";
            return id?.TypeId;
        }
#else
        private static DisplayUnitType UnitFromCode(string code)
        {
            switch (Normalize(code))
            {
                case "mm": return DisplayUnitType.DUT_MILLIMETERS;
                case "cm": return DisplayUnitType.DUT_CENTIMETERS;
                case "m": return DisplayUnitType.DUT_METERS;
                case "ft": return DisplayUnitType.DUT_DECIMAL_FEET;
                case "in": return DisplayUnitType.DUT_DECIMAL_INCHES;
                case "ftin": return DisplayUnitType.DUT_FEET_FRACTIONAL_INCHES;
                case "fracin": return DisplayUnitType.DUT_FRACTIONAL_INCHES;
                case "deg": return DisplayUnitType.DUT_DECIMAL_DEGREES;
                case "degmin": return DisplayUnitType.DUT_DEGREES_AND_MINUTES;
                default: throw new ArgumentException($"Unknown unit '{code}'. Use mm, cm, m, ft, in, ftin, fracIn, deg or degMin.");
            }
        }

        private static string UnitCode(DisplayUnitType unit)
        {
            switch (unit)
            {
                case DisplayUnitType.DUT_MILLIMETERS: return "mm";
                case DisplayUnitType.DUT_CENTIMETERS: return "cm";
                case DisplayUnitType.DUT_METERS: return "m";
                case DisplayUnitType.DUT_DECIMAL_FEET: return "ft";
                case DisplayUnitType.DUT_DECIMAL_INCHES: return "in";
                case DisplayUnitType.DUT_FEET_FRACTIONAL_INCHES: return "ftin";
                case DisplayUnitType.DUT_FRACTIONAL_INCHES: return "fracIn";
                case DisplayUnitType.DUT_DECIMAL_DEGREES: return "deg";
                case DisplayUnitType.DUT_DEGREES_AND_MINUTES: return "degMin";
                default: return unit.ToString();
            }
        }
#endif

        // --------------------------------------------------------------- types

        private static IEnumerable<ElementType> CollectTypes(Document doc, string kind, string style)
        {
            IEnumerable<ElementType> types;
            switch (kind)
            {
                case "text":
                    types = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<ElementType>();
                    break;
                case "dimension":
                    types = new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                        .Where(t => !IsSpot(t.StyleType))
                        .Where(t => string.IsNullOrEmpty(style) || Normalize(t.StyleType.ToString()) == Normalize(style));
                    break;
                case "spotElevation":
                    types = new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                        .Where(t => IsSpot(t.StyleType))
                        .Where(t => string.IsNullOrEmpty(style) || Normalize(t.StyleType.ToString()) == Normalize(style));
                    break;
                case "grid":
                    types = new FilteredElementCollector(doc).OfClass(typeof(GridType)).Cast<ElementType>();
                    break;
                case "level":
                    types = new FilteredElementCollector(doc).OfClass(typeof(LevelType)).Cast<ElementType>();
                    break;
                case "viewport":
                    types = ViewportTypes(doc);
                    break;
                case "arrowhead":
                    types = ArrowheadTypes(doc);
                    break;
                default:
                    throw new ArgumentException($"Unknown kind '{kind}'.");
            }

            return types.Where(t => !string.IsNullOrEmpty(t.Name))
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsSpot(DimensionStyleType style)
        {
            return style == DimensionStyleType.SpotElevation || style == DimensionStyleType.SpotCoordinate ||
                   style == DimensionStyleType.SpotSlope;
        }

        private static IEnumerable<ElementType> ViewportTypes(Document doc)
        {
            var types = new Dictionary<long, ElementType>();
            foreach (var type in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Viewports)
                         .WhereElementIsElementType().OfType<ElementType>())
                types[type.Id.GetValue()] = type;

            if (new FilteredElementCollector(doc).OfClass(typeof(Viewport)).FirstElement() is Viewport sample)
            {
                foreach (var id in sample.GetValidTypes())
                {
                    if (doc.GetElement(id) is ElementType type)
                        types[id.GetValue()] = type;
                }
            }

            return types.Values;
        }

        private static IEnumerable<ElementType> ArrowheadTypes(Document doc)
        {
            return new FilteredElementCollector(doc).WhereElementIsElementType().OfType<ElementType>()
                .Where(t => t.get_Parameter(BuiltInParameter.ARROW_TYPE) != null)
                .ToList();
        }

        private static ElementTypeGroup? DefaultGroup(string kind, ElementType type)
        {
            switch (kind)
            {
                case "text":
                    return ElementTypeGroup.TextNoteType;
                case "grid":
                    return ElementTypeGroup.GridType;
                case "level":
                    return ElementTypeGroup.LevelType;
                case "viewport":
                    return ElementTypeGroup.ViewportType;
                case "dimension":
                case "spotElevation":
                    if (!(type is DimensionType dimension))
                        return null;
                    switch (dimension.StyleType)
                    {
                        case DimensionStyleType.Linear:
                            return ElementTypeGroup.LinearDimensionType;
                        case DimensionStyleType.Angular:
                            return ElementTypeGroup.AngularDimensionType;
                        case DimensionStyleType.Radial:
                            return ElementTypeGroup.RadialDimensionType;
                        case DimensionStyleType.Diameter:
                            return ElementTypeGroup.DiameterDimensionType;
                        case DimensionStyleType.ArcLength:
                            return ElementTypeGroup.ArcLengthDimensionType;
                        case DimensionStyleType.SpotElevation:
                            return ElementTypeGroup.SpotElevationType;
                        case DimensionStyleType.SpotCoordinate:
                            return ElementTypeGroup.SpotCoordinateType;
                        case DimensionStyleType.SpotSlope:
                            return ElementTypeGroup.SpotSlopeType;
                        default:
                            return null;
                    }
                default:
                    return null;
            }
        }

        // ------------------------------------------------------------ settings

        private enum ValueKind
        {
            Length,
            Number,
            Angle,
            Integer,
            Bool,
            Text,
            Color,
            LinePattern,
            TypeRef,
            Choice,
            Units
        }

        private sealed class Setting
        {
            public string Key;
            public ValueKind Kind;
            public BuiltInParameter[] Bips = new BuiltInParameter[0];
            public string[] Names = new string[0];
            public Dictionary<string, int> Choices;
            public BuiltInCategory? RefCategory;
            public bool Arrowhead;
        }

        private static Setting S(string key, ValueKind kind, BuiltInParameter[] bips, params string[] names)
        {
            return new Setting { Key = key, Kind = kind, Bips = bips, Names = names };
        }

        private static BuiltInParameter[] B(params BuiltInParameter[] bips) => bips;

        private static Dictionary<string, int> Map(params object[] pairs)
        {
            var map = new Dictionary<string, int>();
            for (var i = 0; i + 1 < pairs.Length; i += 2)
                map[(string)pairs[i]] = (int)pairs[i + 1];
            return map;
        }

        private static Dictionary<string, List<Setting>> BuildSettings()
        {
            var background = Map("opaque", 0, "transparent", 1);

            List<Setting> TextStyle() => new List<Setting>
            {
                S("font", ValueKind.Text, B(BuiltInParameter.TEXT_FONT), "Text Font"),
                S("textSize", ValueKind.Length, B(BuiltInParameter.TEXT_SIZE), "Text Size"),
                S("bold", ValueKind.Bool, B(BuiltInParameter.TEXT_STYLE_BOLD), "Bold"),
                S("italic", ValueKind.Bool, B(BuiltInParameter.TEXT_STYLE_ITALIC), "Italic"),
                S("underline", ValueKind.Bool, B(BuiltInParameter.TEXT_STYLE_UNDERLINE), "Underline"),
                S("widthFactor", ValueKind.Number, B(BuiltInParameter.TEXT_WIDTH_SCALE), "Width Factor"),
                S("color", ValueKind.Color, B(BuiltInParameter.LINE_COLOR, BuiltInParameter.TEXT_COLOR), "Color")
            };

            Setting Arrow(string key, BuiltInParameter bip, params string[] names)
            {
                var s = S(key, ValueKind.TypeRef, B(bip), names);
                s.Arrowhead = true;
                return s;
            }

            Setting Ref(string key, BuiltInParameter bip, BuiltInCategory? category, params string[] names)
            {
                var s = S(key, ValueKind.TypeRef, B(bip), names);
                s.RefCategory = category;
                return s;
            }

            Setting Choice(string key, BuiltInParameter bip, Dictionary<string, int> map, params string[] names)
            {
                var s = S(key, ValueKind.Choice, B(bip), names);
                s.Choices = map;
                return s;
            }

            var text = TextStyle();
            text.AddRange(new[]
            {
                Choice("background", BuiltInParameter.TEXT_BACKGROUND, background, "Background"),
                S("showBorder", ValueKind.Bool, B(BuiltInParameter.TEXT_BOX_VISIBILITY), "Show Border"),
                S("leaderBorderOffset", ValueKind.Length, B(BuiltInParameter.LEADER_OFFSET_SHEET), "Leader/Border Offset"),
                Arrow("leaderArrowhead", BuiltInParameter.LEADER_ARROWHEAD, "Leader Arrowhead"),
                S("lineWeight", ValueKind.Integer, B(BuiltInParameter.LINE_PEN), "Line Weight"),
                S("tabSize", ValueKind.Length, B(BuiltInParameter.TEXT_TAB_SIZE), "Tab Size")
            });

            var dimension = TextStyle();
            dimension.AddRange(new[]
            {
                Choice("textBackground", BuiltInParameter.DIM_TEXT_BACKGROUND, background, "Text Background"),
                S("textOffset", ValueKind.Length, B(BuiltInParameter.TEXT_DIST_TO_LINE), "Text Offset"),
                Arrow("tickMark", BuiltInParameter.WITNS_LINE_TICK_MARK, "Tick Mark"),
                Arrow("interiorTickMark", BuiltInParameter.DIM_STYLE_INTERIOR_TICK_MARK, "Interior Tick Mark"),
                S("lineWeight", ValueKind.Integer, B(BuiltInParameter.LINE_PEN), "Line Weight"),
                S("tickLineWeight", ValueKind.Integer, B(BuiltInParameter.TICK_MARK_PEN), "Tick Mark Line Weight"),
                S("witnessLineExtension", ValueKind.Length, B(BuiltInParameter.WITNS_LINE_EXTENSION), "Witness Line Extension"),
                S("witnessLineGap", ValueKind.Length, B(BuiltInParameter.WITNS_LINE_GAP_TO_ELT), "Witness Line Gap to Element"),
                S("dimensionLineExtension", ValueKind.Length, B(BuiltInParameter.DIM_LINE_EXTENSION), "Dimension Line Extension"),
                Ref("centerlineSymbol", BuiltInParameter.DIM_STYLE_CENTERLINE_SYMBOL, null, "Centerline Symbol"),
                S("centerlinePattern", ValueKind.LinePattern, B(BuiltInParameter.DIM_STYLE_CENTERLINE_PATTERN), "Centerline Pattern"),
                S("units", ValueKind.Units, B())
            });

            var spot = TextStyle();
            spot.AddRange(new[]
            {
                Choice("textBackground", BuiltInParameter.DIM_TEXT_BACKGROUND, background, "Text Background"),
                Ref("symbol", BuiltInParameter.SPOT_ELEV_SYMBOL, BuiltInCategory.OST_SpotElevSymbols, "Symbol"),
                Arrow("leaderArrowhead", BuiltInParameter.SPOT_ELEV_LEADER_ARROWHEAD, "Leader Arrowhead"),
                S("lineWeight", ValueKind.Integer, B(BuiltInParameter.SPOT_ELEV_LINE_PEN, BuiltInParameter.LINE_PEN),
                    "Leader Line Weight", "Line Weight"),
                S("tickLineWeight", ValueKind.Integer, B(BuiltInParameter.SPOT_ELEV_TICK_MARK_PEN),
                    "Leader Arrowhead Line Weight"),
                S("textOffsetFromLeader", ValueKind.Length, B(BuiltInParameter.SPOT_TEXT_FROM_LEADER), "Text Offset from Leader"),
                S("textOffsetFromSymbol", ValueKind.Length, B(BuiltInParameter.SPOT_ELEV_TEXT_HORIZ_OFFSET), "Text Offset from Symbol"),
                S("indicator", ValueKind.Text, B(BuiltInParameter.SPOT_ELEV_IND_ELEVATION), "Elevation Indicator"),
                Choice("indicatorPosition", BuiltInParameter.SPOT_ELEV_IND_TYPE, Map("prefix", 0, "suffix", 1),
                    "Indicator as Prefix / Suffix"),
                S("units", ValueKind.Units, B())
            });

            var grid = new List<Setting>
            {
                Ref("bubble", BuiltInParameter.GRID_HEAD_TAG, BuiltInCategory.OST_GridHeads, "Symbol"),
                S("bubbleEnd1", ValueKind.Bool, B(BuiltInParameter.GRID_BUBBLE_END_1), "Plan View Symbols End 1 (Default)"),
                S("bubbleEnd2", ValueKind.Bool, B(BuiltInParameter.GRID_BUBBLE_END_2), "Plan View Symbols End 2 (Default)"),
                Choice("nonPlanBubbles", BuiltInParameter.DATUM_BUBBLE_LOCATION_IN_ELEV, null, "Non-Plan View Symbols (Default)"),
                Choice("centerSegment", BuiltInParameter.GRID_CENTER_SEGMENT_STYLE, Map("continuous", 0, "none", 1, "custom", 2),
                    "Center Segment"),
                S("centerSegmentWeight", ValueKind.Integer, B(BuiltInParameter.GRID_CENTER_SEGMENT_WEIGHT), "Center Segment Weight"),
                S("centerSegmentColor", ValueKind.Color, B(BuiltInParameter.GRID_CENTER_SEGMENT_COLOR), "Center Segment Color"),
                S("centerSegmentPattern", ValueKind.LinePattern, B(BuiltInParameter.GRID_CENTER_SEGMENT_PATTERN), "Center Segment Pattern"),
                S("endSegmentWeight", ValueKind.Integer, B(BuiltInParameter.GRID_END_SEGMENT_WEIGHT), "End Segment Weight"),
                S("endSegmentColor", ValueKind.Color, B(BuiltInParameter.GRID_END_SEGMENT_COLOR), "End Segment Color"),
                S("endSegmentPattern", ValueKind.LinePattern, B(BuiltInParameter.GRID_END_SEGMENT_PATTERN), "End Segment Pattern"),
                S("endSegmentLength", ValueKind.Length, B(BuiltInParameter.GRID_END_SEGMENTS_LENGTH), "End Segments Length"),
                S("bubbleLineWeight", ValueKind.Integer, B(BuiltInParameter.GRID_BUBBLE_LINE_PEN), "Bubble Line Weight")
            };

            var level = new List<Setting>
            {
                Ref("symbol", BuiltInParameter.LEVEL_HEAD_TAG, BuiltInCategory.OST_LevelHeads, "Symbol"),
                S("symbolEnd1", ValueKind.Bool, B(BuiltInParameter.DATUM_BUBBLE_END_1), "Symbol at End 1 Default"),
                S("symbolEnd2", ValueKind.Bool, B(BuiltInParameter.DATUM_BUBBLE_END_2), "Symbol at End 2 Default"),
                S("lineWeight", ValueKind.Integer, B(BuiltInParameter.LINE_PEN), "Line Weight"),
                S("color", ValueKind.Color, B(BuiltInParameter.LINE_COLOR), "Color"),
                S("linePattern", ValueKind.LinePattern, B(BuiltInParameter.LINE_PATTERN), "Line Pattern"),
                Choice("elevationBase", BuiltInParameter.LEVEL_RELATIVE_BASE_TYPE,
                    Map("projectbasepoint", 0, "surveypoint", 1), "Elevation Base")
            };

            var viewport = new List<Setting>
            {
                Ref("title", BuiltInParameter.VIEWPORT_ATTR_LABEL_TAG, BuiltInCategory.OST_ViewportLabel, "Title"),
                Choice("showTitle", BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL, null, "Show Title"),
                S("showExtensionLine", ValueKind.Bool, B(BuiltInParameter.VIEWPORT_ATTR_SHOW_EXTENSION_LINE), "Show Extension Line"),
                S("lineWeight", ValueKind.Integer, B(BuiltInParameter.LINE_PEN), "Line Weight"),
                S("color", ValueKind.Color, B(BuiltInParameter.LINE_COLOR), "Color"),
                S("linePattern", ValueKind.LinePattern, B(BuiltInParameter.LINE_PATTERN), "Line Pattern")
            };

            var arrowhead = new List<Setting>
            {
                Choice("style", BuiltInParameter.ARROW_TYPE, null, "Arrow Style"),
                S("tickSize", ValueKind.Length, B(BuiltInParameter.ARROW_SIZE), "Tick Size"),
                S("widthAngle", ValueKind.Angle, B(BuiltInParameter.LEADER_ARROW_WIDTH), "Arrow Width Angle"),
                S("filled", ValueKind.Bool, B(BuiltInParameter.ARROW_FILLED), "Fill Tick"),
                S("closed", ValueKind.Bool, B(BuiltInParameter.ARROW_CLOSED), "Arrow Closed"),
                S("heavyEndPen", ValueKind.Integer, B(BuiltInParameter.HEAVY_END_PEN), "Heavy End Pen Weight")
            };

            return new Dictionary<string, List<Setting>>
            {
                ["text"] = text,
                ["dimension"] = dimension,
                ["spotElevation"] = spot,
                ["grid"] = grid,
                ["level"] = level,
                ["viewport"] = viewport,
                ["arrowhead"] = arrowhead
            };
        }
    }
}
