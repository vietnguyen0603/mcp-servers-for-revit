using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Structure
{
    /// <summary>
    ///     Creates sized types by duplicating an existing family or system type and
    ///     setting its type parameters: welded I sections (d/bf/tw/tf), concrete
    ///     column/beam sizes (b/h), footing sizes, and floor/wall/roof/ceiling
    ///     thickness through the compound structure. Re-running with the same name
    ///     updates the existing type, so calls are idempotent.
    /// </summary>
    public class CreateFamilyTypeEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Create Family Type";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "types");
            var results = DocumentationUtils.RunBatch(doc, "MCP: Create Family Types", items, item => Create(doc, (JObject)item));
            return Ok($"Created or updated {results.Count(r => r.Value<bool>("success"))} of {results.Count} types.",
                DocumentationUtils.Summarize(results));
        }

        private static object Create(Document doc, JObject item)
        {
            var newName = item.Value<string>("newName");
            if (string.IsNullOrWhiteSpace(newName))
                throw new ArgumentException("'newName' is required.");

            var source = ResolveSource(doc, item);
            var ifExists = (item.Value<string>("ifExists") ?? "update").ToLowerInvariant();
            var existing = FindSibling(doc, source, newName);
            if (existing != null && ifExists == "error")
                throw new ArgumentException($"Type '{newName}' already exists (id {existing.Id.GetValue()}).");

            var created = existing == null;
            var type = existing ?? (ElementType)source.Duplicate(newName);
            var warnings = new List<string>();
            var applied = new List<string>();

            if (existing == null || ifExists == "update")
            {
                if (item["parameters"] is JObject values)
                {
                    foreach (var property in values.Properties())
                    {
                        var error = SetTypeParameter(type, property.Name, property.Value);
                        if (error == null) applied.Add(property.Name);
                        else warnings.Add(error);
                    }
                }

                var thickness = item.Value<double?>("thickness");
                if (thickness.HasValue)
                {
                    SetThickness(type, thickness.Value);
                    applied.Add("thickness");
                }
            }

            if (type is FamilySymbol symbol && !symbol.IsActive)
                symbol.Activate();

            var result = new JObject
            {
                ["typeId"] = type.Id.GetValue(),
                ["name"] = type.Name,
                ["familyName"] = (type as FamilySymbol)?.FamilyName ?? type.FamilyName,
                ["category"] = type.Category?.Name,
                ["created"] = created,
                ["applied"] = new JArray(applied)
            };
            if (warnings.Count > 0)
                result["warnings"] = new JArray(warnings);
            if (warnings.Count > 0 || item.Value<bool?>("listParameters") == true)
                result["typeParameters"] = DescribeParameters(type);
            return result;
        }

        /// <summary>Source type: sourceTypeId, or familyName (+ typeName), or category (+ typeName).</summary>
        private static ElementType ResolveSource(Document doc, JObject item)
        {
            var sourceId = DocumentationUtils.ReadId(item, "sourceTypeId");
            if (sourceId != null)
            {
                return doc.GetElement(sourceId.Value.ToRevitElementId()) as ElementType
                       ?? throw new ArgumentException($"sourceTypeId {sourceId} is not a type.");
            }

            var familyName = item.Value<string>("familyName");
            var typeName = item.Value<string>("typeName");
            var category = item.Value<string>("category");
            IEnumerable<ElementType> candidates = new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>();

            if (!string.IsNullOrWhiteSpace(familyName))
            {
                candidates = candidates.Where(t => string.Equals((t as FamilySymbol)?.FamilyName ?? t.FamilyName, familyName, StringComparison.OrdinalIgnoreCase));
            }
            else if (!string.IsNullOrWhiteSpace(category))
            {
                var bic = ParseCategory(category);
                candidates = candidates.Where(t => t.Category != null && t.Category.Id.GetValue() == (long)bic);
            }
            else
            {
                throw new ArgumentException("Give sourceTypeId, familyName or category.");
            }

            var list = candidates.ToList();
            if (list.Count == 0)
                throw new ArgumentException(string.IsNullOrWhiteSpace(familyName)
                    ? $"No types in category '{category}'."
                    : $"Family '{familyName}' is not loaded (use load_family).");

            if (!string.IsNullOrWhiteSpace(typeName))
            {
                var match = list.FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                    throw new ArgumentException($"Type '{typeName}' not found. Available: {string.Join(", ", list.Select(t => t.Name).Take(30))}.");
                return match;
            }

            // Prefer a single-layer system type (simple to resize), else the first type.
            return list.OrderBy(t => (t as HostObjAttributes)?.GetCompoundStructure()?.LayerCount ?? 0).First();
        }

        private static ElementType FindSibling(Document doc, ElementType source, string name)
        {
            if (source is FamilySymbol symbol)
            {
                return symbol.Family.GetFamilySymbolIds()
                    .Select(id => doc.GetElement(id) as ElementType)
                    .FirstOrDefault(t => t != null && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            }

            return new FilteredElementCollector(doc).OfClass(source.GetType()).Cast<ElementType>()
                .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Numbers on length parameters are millimetres; strings use Revit's display format (e.g. "2'-0\"").</summary>
        private static string SetTypeParameter(ElementType type, string name, JToken value)
        {
            var parameter = type.LookupParameter(name);
            if (parameter == null)
                return $"Parameter '{name}' not found on the type.";
            if (parameter.IsReadOnly)
                return $"Parameter '{name}' is read-only.";

            if (parameter.StorageType == StorageType.Double &&
                (value.Type == JTokenType.Integer || value.Type == JTokenType.Float) && IsLength(parameter))
            {
                return parameter.Set(value.Value<double>() / 304.8) ? null : $"Could not set '{name}'.";
            }

            return DocumentationUtils.SetParameterValue(type, name, value);
        }

        private static bool IsLength(Parameter parameter)
        {
#if REVIT2022_OR_GREATER
            return parameter.Definition.GetDataType() == SpecTypeId.Length;
#else
            return parameter.Definition.ParameterType == ParameterType.Length;
#endif
        }

        /// <summary>Total thickness (mm) for walls/floors/roofs/ceilings: the core layer absorbs the change.</summary>
        private static void SetThickness(ElementType type, double thicknessMm)
        {
            if (!(type is HostObjAttributes host))
                throw new ArgumentException("'thickness' applies to wall, floor, roof and ceiling types only.");
            var structure = host.GetCompoundStructure()
                            ?? throw new ArgumentException($"'{type.Name}' has no layered structure.");
            var target = thicknessMm / 304.8;
            var core = structure.GetFirstCoreLayerIndex();
            if (core < 0) core = 0;
            var others = structure.GetWidth() - structure.GetLayerWidth(core);
            var coreWidth = target - others;
            if (coreWidth <= 0.001)
                throw new ArgumentException($"Thickness {thicknessMm} mm is smaller than the non-core layers ({others * 304.8:0} mm).");
            structure.SetLayerWidth(core, coreWidth);
            host.SetCompoundStructure(structure);
        }

        private static JArray DescribeParameters(ElementType type)
        {
            var list = new JArray();
            foreach (Parameter p in type.Parameters)
            {
                if (p.Definition == null || p.IsReadOnly) continue;
                if (p.StorageType != StorageType.Double && p.StorageType != StorageType.String && p.StorageType != StorageType.Integer) continue;
                list.Add(new JObject { ["name"] = p.Definition.Name, ["value"] = p.AsValueString() ?? p.AsString() });
            }
            return list;
        }

        internal static BuiltInCategory ParseCategory(string text)
        {
            var name = text.Trim();
            if (!name.StartsWith("OST_", StringComparison.OrdinalIgnoreCase))
                name = "OST_" + name.Replace(" ", "");
            if (Enum.TryParse(name, true, out BuiltInCategory bic))
                return bic;
            throw new ArgumentException($"Unknown category '{text}' (use e.g. OST_StructuralColumns, OST_StructuralFraming, OST_StructuralFoundation, OST_Floors, OST_Walls).");
        }
    }
}
