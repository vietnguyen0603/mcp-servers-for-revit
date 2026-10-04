using System.Globalization;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Framing
{
    /// <summary>
    ///     Type and level lookup shared by the column and beam commands, cached
    ///     for one call. Types resolve by id or by family + type name (exact,
    ///     case-insensitive); levels by name, id, or elevation in mm (1 mm).
    /// </summary>
    internal class FramingLookup
    {
        public const double LevelTolerance = 1.0 / DocumentationUtils.MmPerFoot;

        private readonly Document _doc;
        private readonly BuiltInCategory _category;
        private readonly string _categoryLabel;
        private readonly List<Level> _levels;
        private readonly Dictionary<string, FamilySymbol> _types = new Dictionary<string, FamilySymbol>(StringComparer.OrdinalIgnoreCase);
        private List<FamilySymbol> _symbols;

        public FramingLookup(Document doc, BuiltInCategory category, string categoryLabel)
        {
            _doc = doc;
            _category = category;
            _categoryLabel = categoryLabel;
            _levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).ToList();
        }

        public FamilySymbol ResolveType(JObject item)
        {
            var typeId = DocumentationUtils.ReadId(item, "typeId");
            if (typeId != null)
            {
                var symbol = _doc.GetElement(typeId.Value.ToRevitElementId()) as FamilySymbol
                             ?? throw new ArgumentException($"typeId {typeId} is not a family type.");
                if (symbol.Category == null || symbol.Category.Id.GetValue() != (long)_category)
                    throw new ArgumentException(
                        $"typeId {typeId} ('{symbol.FamilyName}: {symbol.Name}') is {symbol.Category?.Name ?? "uncategorized"}, not {_categoryLabel}.");
                return symbol;
            }

            var familyName = item.Value<string>("familyName")?.Trim();
            if (string.IsNullOrEmpty(familyName))
                throw new ArgumentException("Give typeId or familyName (+ typeName).");
            var typeName = item.Value<string>("typeName")?.Trim();
            var key = familyName + "\n" + typeName;
            if (_types.TryGetValue(key, out var cached))
                return cached;

            _symbols ??= new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol)).OfCategory(_category)
                .Cast<FamilySymbol>().ToList();
            var family = _symbols.Where(s => string.Equals(s.FamilyName, familyName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (family.Count == 0)
                throw new ArgumentException(
                    $"No {_categoryLabel} family '{familyName}' is loaded (use load_family). Loaded: {Join(_symbols.Select(s => s.FamilyName).Distinct())}.");

            FamilySymbol match;
            if (string.IsNullOrEmpty(typeName))
            {
                if (family.Count > 1)
                    throw new ArgumentException($"Family '{familyName}' has {family.Count} types; give typeName: {Join(family.Select(s => s.Name))}.");
                match = family[0];
            }
            else
            {
                match = family.FirstOrDefault(s => string.Equals(s.Name, typeName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"Type '{typeName}' not found in '{familyName}'. Available: {Join(family.Select(s => s.Name))}.");
            }

            _types[key] = match;
            return match;
        }

        /// <summary>Level by exact name (case-insensitive), element id, or elevation in mm within 1 mm.</summary>
        public Level ResolveLevel(JToken token, string field)
        {
            if (token == null || token.Type == JTokenType.Null)
                throw new ArgumentException($"'{field}' is required.");

            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                return ResolveNumber(token.Value<double>(), field);

            var text = token.ToString().Trim();
            var byName = _levels.FirstOrDefault(l => string.Equals(l.Name, text, StringComparison.OrdinalIgnoreCase));
            if (byName != null)
                return byName;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return ResolveNumber(number, field);
            throw new ArgumentException($"Level '{text}' ({field}) not found. Levels: {LevelList()}.");
        }

        private Level ResolveNumber(double value, string field)
        {
            if (value >= 1 && value < long.MaxValue && Math.Abs(value - Math.Round(value)) < 1e-9)
            {
                var byId = _levels.FirstOrDefault(l => l.Id.GetValue() == (long)Math.Round(value));
                if (byId != null)
                    return byId;
            }

            return LevelAt(DocumentationUtils.MmToFeet(value))
                   ?? throw new ArgumentException($"No level with id or elevation {value} mm ({field}). Levels: {LevelList()}.");
        }

        /// <summary>The level whose elevation (feet) is within 1 mm of the given one, or null.</summary>
        public Level LevelAt(double elevation)
        {
            return _levels.Where(l => Math.Abs(l.Elevation - elevation) <= LevelTolerance)
                .OrderBy(l => Math.Abs(l.Elevation - elevation)).FirstOrDefault();
        }

        public Level LevelAbove(Level level)
        {
            return _levels.FirstOrDefault(l => l.Elevation > level.Elevation + LevelTolerance);
        }

        private string LevelList()
        {
            return Join(_levels.Select(l => $"{l.Name} ({DocumentationUtils.FeetToMm(l.Elevation)} mm, id {l.Id.GetValue()})"));
        }

        private static string Join(IEnumerable<string> names)
        {
            var list = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            return list.Count == 0 ? "none" : string.Join(", ", list.Take(30)) + (list.Count > 30 ? $", ... ({list.Count} total)" : "");
        }

        /// <summary>Optional millimetre value converted to feet.</summary>
        public static double? ReadMm(JObject item, string name)
        {
            var value = item[name];
            if (value == null || value.Type == JTokenType.Null)
                return null;
            return DocumentationUtils.MmToFeet(value.Value<double>());
        }

        public static double RequireMm(JToken item, string name)
        {
            var value = item?[name];
            if (value == null || (value.Type != JTokenType.Integer && value.Type != JTokenType.Float))
                throw new ArgumentException($"'{name}' (mm) is required.");
            return DocumentationUtils.MmToFeet(value.Value<double>());
        }

        /// <summary>A required {x, y} millimetre plan point, as feet.</summary>
        public static XYZ RequireXy(JObject item, string name, double z)
        {
            var point = item[name] ?? throw new ArgumentException($"'{name}' {{x, y}} is required.");
            return new XYZ(RequireMm(point, "x"), RequireMm(point, "y"), z);
        }

        public static void SetParameter(Element element, BuiltInParameter id, Action<Parameter> set, string label, List<string> warnings)
        {
            var parameter = element.get_Parameter(id);
            if (parameter == null || parameter.IsReadOnly)
            {
                warnings.Add($"Could not set {label}: parameter missing or read-only.");
                return;
            }

            set(parameter);
        }

        /// <summary>Mark and comments, shared by both commands.</summary>
        public static void SetIdentity(Element element, JObject item, List<string> warnings)
        {
            var mark = item["mark"];
            if (mark != null && mark.Type != JTokenType.Null)
                SetParameter(element, BuiltInParameter.ALL_MODEL_MARK, p => p.Set(mark.ToString()), "mark", warnings);
            var comments = item.Value<string>("comments");
            if (comments != null)
                SetParameter(element, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, p => p.Set(comments), "comments", warnings);
        }
    }
}
