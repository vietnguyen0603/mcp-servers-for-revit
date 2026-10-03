using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Places annotation symbol families (Generic Annotations by default, plus
    ///     other view-based, non-tag annotation symbols) in a view, sets their
    ///     instance parameters and adds leaders. With no symbols and
    ///     <c>listTypes</c>, lists the available types and their editable
    ///     instance parameters instead.
    /// </summary>
    public class PlaceAnnotationSymbolEventHandler : JsonParameterEventHandler
    {
        private const int MaxCandidates = 30;

        public override string GetName() => "Place Annotation Symbol";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = parameters["symbols"] as JArray;
            if ((items == null || items.Count == 0) && parameters.Value<bool?>("listTypes") == true)
                return ListTypes(uiDoc, parameters);

            var view = DetailGeometry.ResolveView(uiDoc, parameters);
            items = DocumentationUtils.RequireArray(parameters, "symbols");
            var symbols = CollectSymbols(doc, true);

            var results = DocumentationUtils.RunBatch(doc, "MCP: Place Annotation Symbols", items, token =>
            {
                var item = (JObject)token;
                var symbol = ResolveSymbol(symbols, item);
                var location = DetailGeometry.ReadPoint(view, item["location"], "location");
                var leaders = ReadLeaders(view, item["leaders"]);

                if (!symbol.IsActive)
                {
                    symbol.Activate();
                    doc.Regenerate();
                }

                var instance = doc.Create.NewFamilyInstance(location, symbol, view);

                var rotation = item.Value<double?>("rotationDegrees") ?? 0;
                if (Math.Abs(rotation) > 1e-9)
                {
                    var axis = Line.CreateBound(location, location + view.ViewDirection);
                    ElementTransformUtils.RotateElement(doc, instance.Id, axis, rotation * Math.PI / 180);
                }

                var warnings = new List<string>();
                if (item["parameters"] is JObject values)
                {
                    foreach (var property in values.Properties())
                    {
                        var error = SetParameter(instance, property.Name, property.Value);
                        if (error != null)
                            warnings.Add(error);
                    }
                }

                if (leaders.Count > 0)
                    AddLeaders(doc, instance, leaders);

                return new
                {
                    elementId = instance.Id.GetValue(),
                    familyName = symbol.FamilyName,
                    typeName = symbol.Name,
                    leaderCount = leaders.Count > 0 ? (int?)leaders.Count : null,
                    warnings = warnings.Count > 0 ? warnings : null
                };
            });

            return Ok(
                $"Placed {results.Count(r => r.Value<bool>("success"))} of {results.Count} annotation symbols in '{view.Name}'.",
                DocumentationUtils.Summarize(results));
        }

        /// <summary>
        ///     Placeable annotation symbol types: Generic Annotations, plus (when
        ///     <paramref name="allCategories" />) other view-based annotation
        ///     families that are not tags, title blocks or detail items.
        ///     Generic Annotations sort first so name matching prefers them.
        /// </summary>
        private static List<FamilySymbol> CollectSymbols(Document doc, bool allCategories)
        {
            var genericId = (long)BuiltInCategory.OST_GenericAnnotation;
            var excluded = new HashSet<long>
            {
                (long)BuiltInCategory.OST_TitleBlocks,
                (long)BuiltInCategory.OST_DetailComponents
            };

            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s =>
                {
                    var category = s.Category;
                    if (category == null)
                        return false;
                    var categoryId = category.Id.GetValue();
                    if (categoryId == genericId)
                        return true;
                    if (!allCategories || excluded.Contains(categoryId) || category.CategoryType != CategoryType.Annotation
                        || category.IsTagCategory)
                        return false;
                    try
                    {
                        return s.Family?.FamilyPlacementType == FamilyPlacementType.ViewBased;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                })
                .OrderBy(s => s.Category.Id.GetValue() == genericId ? 0 : 1)
                .ThenBy(s => s.FamilyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static FamilySymbol ResolveSymbol(List<FamilySymbol> symbols, JObject item)
        {
            if (symbols.Count == 0)
                throw new InvalidOperationException("No annotation symbol families are loaded.");

            var typeId = DocumentationUtils.ReadId(item, "familyTypeId");
            if (typeId != null)
                return symbols.FirstOrDefault(s => s.Id.GetValue() == typeId)
                       ?? throw new ArgumentException(
                           $"familyTypeId {typeId} is not a placeable annotation symbol type (generic annotation or view-based, non-tag annotation family).");

            var familyName = item.Value<string>("familyName")?.Trim();
            var typeName = item.Value<string>("typeName")?.Trim();
            if (string.IsNullOrEmpty(typeName) && familyName != null && familyName.Contains(":"))
            {
                var split = familyName.LastIndexOf(':');
                typeName = familyName.Substring(split + 1).Trim();
                familyName = familyName.Substring(0, split).Trim();
            }

            if (string.IsNullOrEmpty(familyName) && string.IsNullOrEmpty(typeName))
                throw new ArgumentException("Provide 'familyTypeId', or 'familyName' and/or 'typeName'.");

            bool Matches(FamilySymbol s, Func<string, string, bool> compare) =>
                (string.IsNullOrEmpty(familyName) || compare(s.FamilyName, familyName))
                && (string.IsNullOrEmpty(typeName) || compare(s.Name, typeName));

            var exact = symbols.Where(s => Matches(s, (a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase))).ToList();
            if (exact.Count > 0)
                return exact[0];

            var partial = symbols.Where(s => Matches(s, (a, b) => a.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            if (partial.Count == 1)
                return partial[0];

            var requested = $"{familyName ?? "*"} : {typeName ?? "*"}";
            if (partial.Count > 1)
                throw new ArgumentException(
                    $"Annotation symbol '{requested}' is ambiguous; matches: {Describe(partial)}. Use familyTypeId or the exact names.");

            var words = $"{familyName} {typeName}"
                .Split(new[] { ' ', '_', '-', ':', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 2)
                .ToList();
            var close = symbols
                .Select(s => new
                {
                    Symbol = s,
                    Score = words.Count(w => $"{s.FamilyName} {s.Name}".IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Symbol)
                .ToList();
            throw new ArgumentException(
                $"Annotation symbol '{requested}' not found. {(close.Count > 0 ? "Closest" : "Available")}: {Describe(close.Count > 0 ? close : symbols)}.");
        }

        private static string Describe(List<FamilySymbol> symbols)
        {
            var text = string.Join(", ", symbols.Take(MaxCandidates).Select(s => $"{s.FamilyName} : {s.Name} (id {s.Id.GetValue()})"));
            return symbols.Count > MaxCandidates ? $"{text}, ... ({symbols.Count - MaxCandidates} more)" : text;
        }

        private static List<Tuple<XYZ, XYZ>> ReadLeaders(View view, JToken token)
        {
            var leaders = new List<Tuple<XYZ, XYZ>>();
            if (token == null || token.Type == JTokenType.Null)
                return leaders;
            if (!(token is JArray array))
                throw new ArgumentException("'leaders' must be an array of {end, elbow?}.");
            for (var i = 0; i < array.Count; i++)
            {
                var leader = array[i];
                if (leader?["end"] == null || leader["end"].Type == JTokenType.Null)
                    throw new ArgumentException($"leaders[{i}].end is required (the default leader end lands far from the symbol).");
                var end = DetailGeometry.ReadPoint(view, leader["end"], $"leaders[{i}].end");
                var elbowToken = leader["elbow"];
                var elbow = elbowToken == null || elbowToken.Type == JTokenType.Null
                    ? null
                    : DetailGeometry.ReadPoint(view, elbowToken, $"leaders[{i}].elbow");
                leaders.Add(Tuple.Create(end, elbow));
            }

            return leaders;
        }

        private static void AddLeaders(Document doc, FamilyInstance instance, List<Tuple<XYZ, XYZ>> leaders)
        {
            if (!(instance is AnnotationSymbol annotation))
                throw new ArgumentException($"'{instance.Symbol.FamilyName}' is not an annotation symbol; it cannot have leaders.");

            foreach (var leader in leaders)
            {
                annotation.addLeader();
                var added = annotation.GetLeaders().LastOrDefault()
                            ?? throw new InvalidOperationException(
                                $"'{instance.Symbol.FamilyName}' did not accept a leader; enable leaders in the family.");
                added.End = leader.Item1;
                if (leader.Item2 != null)
                    added.Elbow = leader.Item2;
            }

            doc.Regenerate();
        }

        /// <summary>
        ///     Writes an instance parameter; Yes/No (integer) parameters accept
        ///     true/false and "Yes"/"No". Returns null on success or a warning.
        /// </summary>
        private static string SetParameter(FamilyInstance instance, string name, JToken value)
        {
            var parameter = instance.LookupParameter(name);
            if (parameter == null)
            {
                if (instance.Symbol.LookupParameter(name) != null)
                    return $"'{name}' is a type parameter; it was not changed.";
                var available = EditableParameters(instance).Select(p => p.Definition.Name).ToList();
                return $"Parameter '{name}' not found. Instance parameters: {string.Join(", ", available)}.";
            }

            if (parameter.StorageType == StorageType.Integer)
            {
                if (value.Type == JTokenType.Boolean)
                {
                    value = new JValue(value.Value<bool>() ? 1 : 0);
                }
                else if (value.Type == JTokenType.String)
                {
                    switch (value.Value<string>().Trim().ToLowerInvariant())
                    {
                        case "yes":
                        case "y":
                        case "true":
                        case "on":
                            value = new JValue(1);
                            break;
                        case "no":
                        case "n":
                        case "false":
                        case "off":
                            value = new JValue(0);
                            break;
                    }
                }
            }

            return DocumentationUtils.SetParameterValue(instance, name, value);
        }

        private static IEnumerable<Parameter> EditableParameters(Element element)
        {
            return element.Parameters
                .Cast<Parameter>()
                .Where(p => p.Definition != null && !p.IsReadOnly
                                                 && (!(p.Definition is InternalDefinition internalDefinition)
                                                     || internalDefinition.BuiltInParameter == BuiltInParameter.INVALID))
                .OrderBy(p => p.Definition.Name, StringComparer.OrdinalIgnoreCase);
        }

        private static string ParameterKind(Parameter parameter)
        {
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return "Text";
                case StorageType.Double:
                    return "Number";
                case StorageType.ElementId:
                    return "ElementId";
                case StorageType.Integer:
#if REVIT2022_OR_GREATER
                    return parameter.Definition.GetDataType() == SpecTypeId.Boolean.YesNo ? "YesNo" : "Integer";
#else
                    return parameter.Definition.ParameterType == ParameterType.YesNo ? "YesNo" : "Integer";
#endif
                default:
                    return parameter.StorageType.ToString();
            }
        }

        /// <summary>
        ///     Lists placeable types with their editable instance parameters. The
        ///     parameters come from an existing instance, or from a temporary
        ///     instance placed in the target view inside a rolled-back transaction.
        /// </summary>
        private AIResult<object> ListTypes(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var symbols = CollectSymbols(doc, parameters.Value<bool?>("allCategories") == true);

            View view = null;
            try
            {
                view = DetailGeometry.ResolveView(uiDoc, parameters);
            }
            catch (Exception)
            {
                // No usable view: parameters are only read from existing instances.
            }

            var symbolIds = new HashSet<long>(symbols.Select(s => s.Id.GetValue()));
            var instances = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(i => i.Symbol != null && symbolIds.Contains(i.Symbol.Id.GetValue()))
                .GroupBy(i => i.Symbol.Id.GetValue())
                .ToDictionary(g => g.Key, g => g.ToList());

            object Describe(Element element) => element == null
                ? null
                : EditableParameters(element)
                    .Select(p => new { name = p.Definition.Name, kind = ParameterKind(p) })
                    .ToList();

            var temporary = new Dictionary<long, object>();
            var missing = symbols.Where(s => !instances.ContainsKey(s.Id.GetValue())).ToList();
            if (view != null && missing.Count > 0)
            {
                using (var transaction = DocumentationUtils.StartTransaction(doc, "MCP: Inspect Annotation Symbols"))
                {
                    foreach (var symbol in missing)
                    {
                        var sub = new SubTransaction(doc);
                        sub.Start();
                        try
                        {
                            if (!symbol.IsActive)
                            {
                                symbol.Activate();
                                doc.Regenerate();
                            }

                            var probe = doc.Create.NewFamilyInstance(view.Origin ?? XYZ.Zero, symbol, view);
                            temporary[symbol.Id.GetValue()] = Describe(probe);
                        }
                        catch (Exception)
                        {
                            // Not placeable in this view; leave its parameters unknown.
                        }
                        finally
                        {
                            if (sub.HasStarted())
                                sub.RollBack();
                        }
                    }

                    transaction.RollBack();
                }
            }

            var types = symbols.Select(s =>
            {
                var id = s.Id.GetValue();
                instances.TryGetValue(id, out var placed);
                temporary.TryGetValue(id, out var probed);
                return new
                {
                    familyTypeId = id,
                    familyName = s.FamilyName,
                    typeName = s.Name,
                    category = s.Category?.Name,
                    instanceCount = placed?.Count ?? 0,
                    instanceParameters = placed != null ? Describe(placed[0]) : probed
                };
            }).ToList();

            return Ok($"Listed {types.Count} annotation symbol types.", new { count = types.Count, types });
        }
    }
}
