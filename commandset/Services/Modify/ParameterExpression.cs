using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Modify
{
    /// <summary>
    ///     Expression templates for set_parameters ("{Mark}-{b}x{h}") and
    ///     parameter reads/writes in API units (lengths in mm). Placeholders are
    ///     read from the element, then from its type; "{Name:fmt}" picks a
    ///     format (mm0..mm9, cm, m, display, raw, id, upper, lower); "{{" and "}}"
    ///     are literal braces. Mirrors parseExpression in set_parameters.ts.
    /// </summary>
    internal static class ParameterExpression
    {
        private static readonly Regex FormatPattern =
            new Regex(@"^(?:(?:mm|cm|m)\d?|display|raw|id|upper|lower)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal class Token
        {
            public string Literal;
            public string Name;
            public string Format;
        }

        internal static List<Token> Parse(string template)
        {
            var tokens = new List<Token>();
            var literal = new StringBuilder();
            for (var i = 0; i < template.Length; i++)
            {
                var c = template[i];
                if (c == '{')
                {
                    if (i + 1 < template.Length && template[i + 1] == '{')
                    {
                        literal.Append('{');
                        i++;
                        continue;
                    }

                    var end = template.IndexOf('}', i + 1);
                    if (end < 0) throw new ArgumentException($"Expression: unclosed '{{' at position {i}.");
                    var inner = template.Substring(i + 1, end - i - 1);
                    if (inner.Contains("{")) throw new ArgumentException($"Expression: nested '{{' at position {i}.");
                    var colon = inner.LastIndexOf(':');
                    var name = (colon > 0 ? inner.Substring(0, colon) : inner).Trim();
                    var format = colon > 0 ? inner.Substring(colon + 1).Trim().ToLowerInvariant() : null;
                    if (name.Length == 0) throw new ArgumentException($"Expression: empty placeholder at position {i}.");
                    if (format != null && !FormatPattern.IsMatch(format))
                        throw new ArgumentException($"Expression: unknown format '{format}' in {{{inner}}}.");

                    if (literal.Length > 0)
                    {
                        tokens.Add(new Token { Literal = literal.ToString() });
                        literal.Clear();
                    }

                    tokens.Add(new Token { Name = name, Format = format });
                    i = end;
                    continue;
                }

                if (c == '}')
                {
                    if (i + 1 < template.Length && template[i + 1] == '}')
                    {
                        literal.Append('}');
                        i++;
                        continue;
                    }

                    throw new ArgumentException($"Expression: unmatched '}}' at position {i}.");
                }

                literal.Append(c);
            }

            if (literal.Length > 0) tokens.Add(new Token { Literal = literal.ToString() });
            return tokens;
        }

        /// <summary>
        ///     Evaluates the tokens for <paramref name="element" /> (placeholders fall back to
        ///     its type). Missing or empty placeholders are added to <paramref name="unresolved" />
        ///     and contribute an empty string.
        /// </summary>
        internal static string Evaluate(Document doc, Element element, List<Token> tokens, List<string> unresolved)
        {
            var type = element is ElementType ? null : doc.GetElement(element.GetTypeId());
            var text = new StringBuilder();
            foreach (var token in tokens)
            {
                if (token.Name == null)
                {
                    text.Append(token.Literal);
                    continue;
                }

                var value = Resolve(doc, element, type, token.Name, token.Format);
                if (string.IsNullOrEmpty(value))
                {
                    if (!unresolved.Contains(token.Name)) unresolved.Add(token.Name);
                    continue;
                }

                text.Append(value);
            }

            return text.ToString();
        }

        private static string Resolve(Document doc, Element element, Element type, string name, string format)
        {
            var elementType = element as ElementType ?? type as ElementType;
            string special = null;
            var isSpecial = true;
            switch (name.ToLowerInvariant())
            {
                case "type name":
                    special = elementType?.Name;
                    break;
                case "family name":
                    special = elementType?.FamilyName;
                    break;
                case "family and type":
                    special = elementType == null ? null : $"{elementType.FamilyName}: {elementType.Name}";
                    break;
                case "category":
                    special = element.Category?.Name;
                    break;
                case "level":
                    var levelId = ModelSelectionUtils.GetLevelId(element);
                    special = levelId == ElementId.InvalidElementId
                        ? null
                        : format == "id" ? levelId.GetValue().ToString(CultureInfo.InvariantCulture) : doc.GetElement(levelId)?.Name;
                    break;
                case "element id":
                case "id":
                    special = element.Id.GetValue().ToString(CultureInfo.InvariantCulture);
                    break;
                case "type id":
                    special = elementType?.Id.GetValue().ToString(CultureInfo.InvariantCulture);
                    break;
                default:
                    isSpecial = false;
                    break;
            }

            if (isSpecial) return ApplyCase(special, format);

            var parameter = FindParameter(element, name) ?? (type == null ? null : FindParameter(type, name));
            return parameter == null ? null : Format(doc, parameter, format);
        }

        private static string ApplyCase(string text, string format)
        {
            if (text == null) return null;
            if (format == "upper") return text.ToUpperInvariant();
            if (format == "lower") return text.ToLowerInvariant();
            return text;
        }

        /// <summary>First parameter of that name, preferring one with a value.</summary>
        internal static Parameter FindParameter(Element element, string name)
        {
            var parameters = element.GetParameters(name);
            if (parameters == null || parameters.Count == 0) return element.LookupParameter(name);
            return parameters.FirstOrDefault(p => p.HasValue) ?? parameters[0];
        }

        /// <summary>Parameter value as text: lengths in mm without units by default.</summary>
        internal static string Format(Document doc, Parameter parameter, string format)
        {
            if (!parameter.HasValue) return null;
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return ApplyCase(parameter.AsString(), format);
                case StorageType.Integer:
                    return format == "raw"
                        ? parameter.AsInteger().ToString(CultureInfo.InvariantCulture)
                        : ApplyCase(parameter.AsValueString() ?? parameter.AsInteger().ToString(CultureInfo.InvariantCulture), format);
                case StorageType.Double:
                    var value = parameter.AsDouble();
                    if (format == "raw") return value.ToString("0.######", CultureInfo.InvariantCulture);
                    if (format == "display" || !IsLength(parameter)) return parameter.AsValueString() ?? value.ToString("0.######", CultureInfo.InvariantCulture);
                    return FormatLength(value, format);
                case StorageType.ElementId:
                    var id = parameter.AsElementId();
                    if (id == null || id == ElementId.InvalidElementId) return null;
                    if (format == "id") return id.GetValue().ToString(CultureInfo.InvariantCulture);
                    return ApplyCase(doc.GetElement(id)?.Name ?? parameter.AsValueString(), format);
                default:
                    return null;
            }
        }

        private static string FormatLength(double feet, string format)
        {
            var unit = "mm";
            var decimals = 0;
            if (!string.IsNullOrEmpty(format) && format != "upper" && format != "lower" && format != "id")
            {
                unit = format.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                var digits = format.Substring(unit.Length);
                decimals = digits.Length > 0 ? int.Parse(digits, CultureInfo.InvariantCulture) : unit == "mm" ? 0 : unit == "cm" ? 1 : 3;
            }

            var mm = feet * DocumentationUtils.MmPerFoot;
            var scaled = unit == "cm" ? mm / 10 : unit == "m" ? mm / 1000 : mm;
            var rounded = Math.Round(scaled, decimals, MidpointRounding.AwayFromZero);
            if (rounded == 0) rounded = 0; // no "-0"
            return rounded.ToString("F" + decimals, CultureInfo.InvariantCulture);
        }

        internal static bool IsLength(Parameter parameter)
        {
            try
            {
#if REVIT2022_OR_GREATER
                return parameter.Definition.GetDataType() == SpecTypeId.Length;
#else
                return parameter.Definition.ParameterType == ParameterType.Length;
#endif
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Comparable snapshot of a parameter's stored value.</summary>
        internal static string RawKey(Parameter parameter)
        {
            switch (parameter.StorageType)
            {
                case StorageType.String: return parameter.AsString() ?? string.Empty;
                case StorageType.Integer: return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
                case StorageType.Double: return Math.Round(parameter.AsDouble(), 9).ToString("R", CultureInfo.InvariantCulture);
                case StorageType.ElementId: return parameter.AsElementId()?.GetValue().ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                default: return string.Empty;
            }
        }

        /// <summary>Display value of a parameter for reports.</summary>
        internal static string Display(Document doc, Parameter parameter)
        {
            if (parameter.StorageType == StorageType.String) return parameter.AsString() ?? string.Empty;
            if (parameter.StorageType == StorageType.Double && IsLength(parameter))
                return FormatLength(parameter.AsDouble(), "mm1").TrimEnd('0').TrimEnd('.');
            return Format(doc, parameter, null) ?? string.Empty;
        }

        /// <summary>
        ///     Writes a JSON value: text as is; numbers to length parameters as mm (also
        ///     numeric strings); other numbers / unit strings through SetValueString (display
        ///     units); true/false/yes/no to Yes/No; element ids or level/material/element
        ///     names to element-id parameters. Returns null on success or an error message.
        /// </summary>
        internal static string Write(Document doc, Parameter parameter, JToken value)
        {
            var name = parameter.Definition?.Name ?? "?";
            if (parameter.IsReadOnly) return $"Parameter '{name}' is read-only.";

            var isNull = value == null || value.Type == JTokenType.Null;
            var text = isNull
                ? string.Empty
                : value.Type == JTokenType.Float || value.Type == JTokenType.Integer
                    ? value.Value<double>().ToString("R", CultureInfo.InvariantCulture)
                    : value.Type == JTokenType.Boolean
                        ? (value.Value<bool>() ? "1" : "0")
                        : value.ToString();

            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        if (value != null && value.Type == JTokenType.Boolean) text = value.Value<bool>() ? "Yes" : "No";
                        return parameter.Set(text) ? null : $"Could not set '{name}' to '{text}'.";

                    case StorageType.Integer:
                        if (isNull) return $"Parameter '{name}' needs a value.";
                        int integer;
                        var trimmed = text.Trim();
                        if (string.Equals(trimmed, "yes", StringComparison.OrdinalIgnoreCase) || string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase))
                            integer = 1;
                        else if (string.Equals(trimmed, "no", StringComparison.OrdinalIgnoreCase) || string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
                            integer = 0;
                        else if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && Math.Abs(number - Math.Round(number)) < 1e-9)
                            integer = (int)Math.Round(number);
                        else
                            return parameter.SetValueString(trimmed) ? null : $"'{name}' expects an integer or Yes/No, got '{text}'.";
                        return parameter.Set(integer) ? null : $"Could not set '{name}' to {integer}.";

                    case StorageType.Double:
                        if (isNull) return $"Parameter '{name}' needs a value.";
                        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
                        {
                            if (IsLength(parameter))
                                return parameter.Set(numeric / DocumentationUtils.MmPerFoot) ? null : $"Could not set '{name}' to {numeric} mm.";
                            return parameter.SetValueString(text.Trim()) || parameter.Set(numeric) ? null : $"Could not set '{name}' to {numeric}.";
                        }
                        return parameter.SetValueString(text.Trim()) ? null : $"Could not parse '{text}' for '{name}'.";

                    case StorageType.ElementId:
                        if (isNull || text.Trim().Length == 0)
                            return parameter.Set(ElementId.InvalidElementId) ? null : $"Could not clear '{name}'.";
                        if (long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                            return parameter.Set(id.ToRevitElementId()) ? null : $"Could not set '{name}' to element {id}.";
                        var target = FindNamedElement(doc, text.Trim());
                        if (target == null) return $"No level, material or other element named '{text}' for '{name}'.";
                        return parameter.Set(target.Id) ? null : $"Could not set '{name}' to '{text}' ({target.Id.GetValue()}).";

                    default:
                        return $"Parameter '{name}' cannot be written.";
                }
            }
            catch (Exception ex)
            {
                return $"Could not set '{name}': {ex.Message}";
            }
        }

        private static Element FindNamedElement(Document doc, string name)
        {
            foreach (var type in new[] { typeof(Level), typeof(Material), typeof(Phase) })
            {
                var match = new FilteredElementCollector(doc).OfClass(type)
                    .FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            return null;
        }
    }
}
