using System.Globalization;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Creates (or reuses) a rule-based parameter filter for a set of
    ///     categories and adds it to views with optional graphic overrides and
    ///     visibility. Parameters are matched by BuiltInParameter name or by
    ///     display name among the parameters filterable for every category.
    ///     Numeric values on length parameters are millimetres.
    /// </summary>
    public class CreateViewFilterEventHandler : JsonParameterEventHandler
    {
        private const double LengthEpsilonFeet = 1e-4;
        private const double NumberEpsilon = 1e-6;

        public override string GetName() => "Create View Filter";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var name = parameters.Value<string>("name")?.Trim();
            if (string.IsNullOrEmpty(name))
                return Fail("'name' is required.");

            var categoryIds = new List<ElementId>();
            foreach (var categoryName in parameters["categories"]?.ToObject<List<string>>() ?? new List<string>())
            {
                var category = DocumentationUtils.ResolveCategory(doc, categoryName)
                               ?? throw new ArgumentException($"Category '{categoryName}' not found.");
                if (!categoryIds.Contains(category.Id))
                    categoryIds.Add(category.Id);
            }

            if (categoryIds.Count == 0)
                return Fail("'categories' must list at least one category.");

            var rules = parameters["rules"] as JArray ?? new JArray();
            var orLogic = string.Equals(parameters.Value<string>("logic"), "Or", StringComparison.OrdinalIgnoreCase);
            var reuseExisting = parameters.Value<bool?>("reuseExisting") ?? false;
            var overrides = parameters["overrides"] as JObject;
            var visible = parameters.Value<bool?>("visible") ?? overrides?.Value<bool?>("visible");
            var viewIds = (parameters["viewIds"] as JArray ?? new JArray()).Select(t => t.Value<long>()).Distinct().ToList();

            var warnings = new List<string>();
            var viewResults = new List<object>();
            var viewsUpdated = 0;
            ParameterFilterElement filter;
            bool created;

            using (var transaction = DocumentationUtils.StartTransaction(doc, "MCP: Create View Filter"))
            {
                var existing = new FilteredElementCollector(doc)
                    .OfClass(typeof(ParameterFilterElement))
                    .Cast<ParameterFilterElement>()
                    .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    if (!reuseExisting)
                    {
                        transaction.RollBack();
                        return Fail($"A filter named '{existing.Name}' already exists (id {existing.Id.GetValue()}). Set reuseExisting to add it to views.");
                    }

                    filter = existing;
                    created = false;
                    if (rules.Count > 0)
                        warnings.Add("Existing filter reused; its categories and rules were not changed.");
                }
                else
                {
                    var filterableCategories = new HashSet<long>(
                        ParameterFilterUtilities.GetAllFilterableCategories().Select(id => id.GetValue()));
                    var unfilterable = categoryIds.Where(id => !filterableCategories.Contains(id.GetValue())).ToList();
                    if (unfilterable.Count > 0)
                        throw new ArgumentException(
                            $"Categories cannot be used in a view filter: {string.Join(", ", unfilterable.Select(id => Category.GetCategory(doc, id)?.Name ?? id.GetValue().ToString()))}.");

                    var elementFilter = BuildElementFilter(doc, categoryIds, rules, orLogic, warnings);
                    filter = elementFilter == null
                        ? ParameterFilterElement.Create(doc, name, categoryIds)
                        : ParameterFilterElement.Create(doc, name, categoryIds, elementFilter);
                    created = true;
                }

                var settings = overrides != null && overrides.Properties().Any(p => p.Name != "visible")
                    ? GraphicOverrideBuilder.Build(doc, overrides)
                    : null;

                foreach (var viewId in viewIds)
                {
                    var subTransaction = new SubTransaction(doc);
                    subTransaction.Start();
                    View view = null;
                    try
                    {
                        view = DocumentationUtils.GetElement<View>(doc, viewId)
                               ?? throw new ArgumentException($"{viewId} is not a view.");
                        if (!view.AreGraphicsOverridesAllowed())
                            throw new InvalidOperationException($"View '{view.Name}' does not support filters.");

                        if (!view.IsFilterApplied(filter.Id))
                            view.AddFilter(filter.Id);
                        if (settings != null)
                            view.SetFilterOverrides(filter.Id, settings);
                        if (visible != null)
                            view.SetFilterVisibility(filter.Id, visible.Value);

                        subTransaction.Commit();
                        viewsUpdated++;
                        viewResults.Add(new { viewId, success = true, viewName = view.Name });
                    }
                    catch (Exception ex)
                    {
                        if (subTransaction.HasStarted())
                            subTransaction.RollBack();
                        var message = ex.Message;
                        if (view != null && view.ViewTemplateId != ElementId.InvalidElementId)
                            message += $" View uses template {view.ViewTemplateId.GetValue()}; add the filter to the template instead.";
                        viewResults.Add(new { viewId, success = false, message });
                    }
                }

                var status = transaction.Commit();
                if (status != TransactionStatus.Committed)
                    return Fail($"View filter transaction was not committed ({status}).");
            }

            return Ok($"{(created ? "Created" : "Reused")} filter '{filter.Name}'; added to {viewsUpdated} of {viewIds.Count} views.",
                new
                {
                    filterId = filter.Id.GetValue(),
                    name = filter.Name,
                    created,
                    views = viewResults,
                    warnings = warnings.Count > 0 ? warnings : null
                });
        }

        private static ElementFilter BuildElementFilter(Document doc, List<ElementId> categoryIds, JArray rules,
            bool orLogic, List<string> warnings)
        {
            if (rules.Count == 0)
                return null;

            var filterable = ParameterFilterUtilities.GetFilterableParametersInCommon(doc, categoryIds);
            var filters = new List<ElementFilter>();
            foreach (var rule in rules.OfType<JObject>())
            {
                var parameterName = rule.Value<string>("parameter");
                var parameterId = ResolveParameterId(doc, filterable, parameterName)
                                  ?? throw new ArgumentException(
                                      $"Parameter '{parameterName}' is not filterable for all of the given categories.");
                var filterRule = CreateRule(doc, categoryIds, parameterId, rule.Value<string>("operator") ?? "Equals",
                    rule["value"], parameterName, warnings);
                filters.Add(new ElementParameterFilter(filterRule));
            }

            ElementFilter combined = filters.Count == 1
                ? filters[0]
                : orLogic
                    ? new LogicalOrFilter(filters)
                    : new LogicalAndFilter(filters);

            if (!ParameterFilterElement.ElementFilterIsAcceptableForParameterFilterElement(
                    doc, new HashSet<ElementId>(categoryIds), combined))
                throw new ArgumentException("The rules cannot be used in a view filter for these categories.");

            return combined;
        }

        private static ElementId ResolveParameterId(Document doc, ICollection<ElementId> filterable, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            var trimmed = name.Trim();

            // Exact BuiltInParameter enum names first, then display names.
            foreach (var id in filterable)
            {
                var value = id.GetValue();
                if (value < 0 && string.Equals(((BuiltInParameter)(int)value).ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
                    return id;
            }

            foreach (var id in filterable)
            {
                if (string.Equals(ParameterLabel(doc, id), trimmed, StringComparison.OrdinalIgnoreCase))
                    return id;
            }

            return null;
        }

        private static string ParameterLabel(Document doc, ElementId id)
        {
            var value = id.GetValue();
            if (value < 0)
            {
                try
                {
                    return LabelUtils.GetLabelFor((BuiltInParameter)(int)value);
                }
                catch (Exception)
                {
                    return null;
                }
            }

            var element = doc.GetElement(id);
            return (element as ParameterElement)?.GetDefinition()?.Name ?? element?.Name;
        }

        private static FilterRule CreateRule(Document doc, List<ElementId> categoryIds, ElementId parameterId,
            string op, JToken value, string parameterName, List<string> warnings)
        {
            var key = op.Trim().ToLowerInvariant();

            if (key == "hasvalue" || key == "hasnovalue")
            {
#if REVIT2022_OR_GREATER
                return key == "hasvalue"
                    ? ParameterFilterRuleFactory.CreateHasValueParameterRule(parameterId)
                    : ParameterFilterRuleFactory.CreateHasNoValueParameterRule(parameterId);
#else
                throw new NotSupportedException("HasValue/HasNoValue filter rules require Revit 2022 or later.");
#endif
            }

            if (value == null || value.Type == JTokenType.Null)
                throw new ArgumentException($"Rule on '{parameterName}' requires a 'value'.");

            var isNumber = value.Type == JTokenType.Integer || value.Type == JTokenType.Float;
            var text = isNumber ? value.Value<double>().ToString(CultureInfo.InvariantCulture) : value.ToString();
            var (storage, isLength, sampled) = ResolveParameterInfo(doc, categoryIds, parameterId);
            if (!sampled && storage == StorageType.Double)
                warnings.Add($"No element has '{parameterName}' yet; its value was used without unit conversion.");

            if (storage == StorageType.None)
            {
                storage = isNumber ? StorageType.Double : StorageType.String;
                if (isNumber)
                    warnings.Add($"Could not determine the type of '{parameterName}'; the value was used without unit conversion.");
            }

            switch (storage)
            {
                case StorageType.String:
                    return CreateStringRule(key, parameterId, text);
                case StorageType.Integer:
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                        throw new ArgumentException($"'{parameterName}' is an integer parameter; '{text}' is not an integer.");
                    return CreateNumericRule(key, parameterId,
                        () => ParameterFilterRuleFactory.CreateEqualsRule(parameterId, integer),
                        () => ParameterFilterRuleFactory.CreateNotEqualsRule(parameterId, integer),
                        () => ParameterFilterRuleFactory.CreateGreaterRule(parameterId, integer),
                        () => ParameterFilterRuleFactory.CreateGreaterOrEqualRule(parameterId, integer),
                        () => ParameterFilterRuleFactory.CreateLessRule(parameterId, integer),
                        () => ParameterFilterRuleFactory.CreateLessOrEqualRule(parameterId, integer));
                case StorageType.Double:
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                        throw new ArgumentException($"'{parameterName}' is a numeric parameter; '{text}' is not a number.");
                    var internalValue = isLength ? DocumentationUtils.MmToFeet(number) : number;
                    var epsilon = isLength ? LengthEpsilonFeet : NumberEpsilon;
                    return CreateNumericRule(key, parameterId,
                        () => ParameterFilterRuleFactory.CreateEqualsRule(parameterId, internalValue, epsilon),
                        () => ParameterFilterRuleFactory.CreateNotEqualsRule(parameterId, internalValue, epsilon),
                        () => ParameterFilterRuleFactory.CreateGreaterRule(parameterId, internalValue, epsilon),
                        () => ParameterFilterRuleFactory.CreateGreaterOrEqualRule(parameterId, internalValue, epsilon),
                        () => ParameterFilterRuleFactory.CreateLessRule(parameterId, internalValue, epsilon),
                        () => ParameterFilterRuleFactory.CreateLessOrEqualRule(parameterId, internalValue, epsilon));
                case StorageType.ElementId:
                    if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idValue))
                        throw new ArgumentException($"'{parameterName}' is an element id parameter; '{text}' is not an id.");
                    var elementId = idValue.ToRevitElementId();
                    return CreateNumericRule(key, parameterId,
                        () => ParameterFilterRuleFactory.CreateEqualsRule(parameterId, elementId),
                        () => ParameterFilterRuleFactory.CreateNotEqualsRule(parameterId, elementId),
                        () => ParameterFilterRuleFactory.CreateGreaterRule(parameterId, elementId),
                        () => ParameterFilterRuleFactory.CreateGreaterOrEqualRule(parameterId, elementId),
                        () => ParameterFilterRuleFactory.CreateLessRule(parameterId, elementId),
                        () => ParameterFilterRuleFactory.CreateLessOrEqualRule(parameterId, elementId));
                default:
                    throw new ArgumentException($"Parameter '{parameterName}' cannot be used in a filter rule.");
            }
        }

        private static FilterRule CreateNumericRule(string key, ElementId parameterId,
            Func<FilterRule> equals, Func<FilterRule> notEquals, Func<FilterRule> greater,
            Func<FilterRule> greaterOrEqual, Func<FilterRule> less, Func<FilterRule> lessOrEqual)
        {
            switch (key)
            {
                case "equals": return equals();
                case "notequals": return notEquals();
                case "greater": return greater();
                case "greaterorequal": return greaterOrEqual();
                case "less": return less();
                case "lessorequal": return lessOrEqual();
                default:
                    throw new ArgumentException($"Operator '{key}' is only valid for text parameters.");
            }
        }

        private static FilterRule CreateStringRule(string key, ElementId id, string text)
        {
            switch (key)
            {
#if REVIT2023_OR_GREATER
                case "equals": return ParameterFilterRuleFactory.CreateEqualsRule(id, text);
                case "notequals": return ParameterFilterRuleFactory.CreateNotEqualsRule(id, text);
                case "greater": return ParameterFilterRuleFactory.CreateGreaterRule(id, text);
                case "greaterorequal": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(id, text);
                case "less": return ParameterFilterRuleFactory.CreateLessRule(id, text);
                case "lessorequal": return ParameterFilterRuleFactory.CreateLessOrEqualRule(id, text);
                case "contains": return ParameterFilterRuleFactory.CreateContainsRule(id, text);
                case "notcontains": return ParameterFilterRuleFactory.CreateNotContainsRule(id, text);
                case "beginswith": return ParameterFilterRuleFactory.CreateBeginsWithRule(id, text);
                case "notbeginswith": return ParameterFilterRuleFactory.CreateNotBeginsWithRule(id, text);
                case "endswith": return ParameterFilterRuleFactory.CreateEndsWithRule(id, text);
                case "notendswith": return ParameterFilterRuleFactory.CreateNotEndsWithRule(id, text);
#else
                case "equals": return ParameterFilterRuleFactory.CreateEqualsRule(id, text, false);
                case "notequals": return ParameterFilterRuleFactory.CreateNotEqualsRule(id, text, false);
                case "greater": return ParameterFilterRuleFactory.CreateGreaterRule(id, text, false);
                case "greaterorequal": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(id, text, false);
                case "less": return ParameterFilterRuleFactory.CreateLessRule(id, text, false);
                case "lessorequal": return ParameterFilterRuleFactory.CreateLessOrEqualRule(id, text, false);
                case "contains": return ParameterFilterRuleFactory.CreateContainsRule(id, text, false);
                case "notcontains": return ParameterFilterRuleFactory.CreateNotContainsRule(id, text, false);
                case "beginswith": return ParameterFilterRuleFactory.CreateBeginsWithRule(id, text, false);
                case "notbeginswith": return ParameterFilterRuleFactory.CreateNotBeginsWithRule(id, text, false);
                case "endswith": return ParameterFilterRuleFactory.CreateEndsWithRule(id, text, false);
                case "notendswith": return ParameterFilterRuleFactory.CreateNotEndsWithRule(id, text, false);
#endif
                default:
                    throw new ArgumentException($"Unsupported operator '{key}'.");
            }
        }

        /// <summary>
        ///     Determines storage type and whether the parameter is a length by
        ///     sampling instances and types of the filter's categories.
        /// </summary>
        private static (StorageType storage, bool isLength, bool sampled) ResolveParameterInfo(Document doc,
            List<ElementId> categoryIds, ElementId parameterId)
        {
            var value = parameterId.GetValue();
            var builtIn = value < 0 ? (BuiltInParameter?)(BuiltInParameter)(int)value : null;
            var categoryFilter = new ElementMulticategoryFilter(categoryIds);

            foreach (var types in new[] { false, true })
            {
                var collector = new FilteredElementCollector(doc).WherePasses(categoryFilter);
                collector = types ? collector.WhereElementIsElementType() : collector.WhereElementIsNotElementType();
                foreach (var element in collector.Take(200))
                {
                    var parameter = builtIn != null
                        ? element.get_Parameter(builtIn.Value)
                        : element.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Id == parameterId);
                    if (parameter != null)
                        return (parameter.StorageType, IsLength(parameter), true);
                }
            }

            return (builtIn != null ? doc.get_TypeOfStorage(builtIn.Value) : StorageType.None, false, false);
        }

        private static bool IsLength(Parameter parameter)
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
    }
}
