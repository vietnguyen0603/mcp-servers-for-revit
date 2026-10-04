using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Modify
{
    /// <summary>
    ///     set_parameters: bulk parameter writes. Filter mode selects elements like
    ///     delete_elements (categories / elementIds / levels / comments / mark / type
    ///     name) and writes one value or a per-element expression ("{Mark}-{b}x{h}");
    ///     onType writes each distinct type once. Explicit mode takes
    ///     items [{elementId, parameter, value | expression, onType?}].
    /// </summary>
    public class SetParametersEventHandler : JsonParameterEventHandler
    {
        private const int SampleSize = 20;

        public override string GetName() => "Set Parameters";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            return parameters["items"] is JArray items && items.Count > 0
                ? RunItems(doc, parameters, items)
                : RunFilter(doc, parameters);
        }

        // ---------------------------------------------------------------- explicit items

        private AIResult<object> RunItems(Document doc, JObject parameters, JArray items)
        {
            var defaultOnType = parameters.Value<bool?>("onType") ?? false;
            var skipIfUnresolved = parameters.Value<bool?>("skipIfUnresolved") ?? true;
            var dryRun = parameters.Value<bool?>("dryRun") ?? false;
            var cache = new Dictionary<string, List<ParameterExpression.Token>>(StringComparer.Ordinal);

            Func<JToken, object> action = token =>
            {
                if (!(token is JObject item)) throw new ArgumentException("Each item must be an object.");
                var id = item.Value<long?>("elementId") ?? throw new ArgumentException("'elementId' is required.");
                var element = doc.GetElement(id.ToRevitElementId()) ?? throw new ArgumentException($"Element {id} not found.");
                var name = item.Value<string>("parameter");
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("'parameter' is required.");
                var onType = item.Value<bool?>("onType") ?? defaultOnType;
                var target = Target(doc, element, onType);
                var parameter = FindWritable(doc, target, element, name, onType);

                JToken value = item["value"];
                var expression = item.Value<string>("expression");
                if (expression != null)
                {
                    var unresolved = new List<string>();
                    var text = ParameterExpression.Evaluate(doc, element, Tokens(cache, expression), unresolved);
                    if (unresolved.Count > 0 && skipIfUnresolved)
                        throw new InvalidOperationException($"Unresolved placeholder(s): {string.Join(", ", unresolved)}.");
                    value = new JValue(text);
                }
                else if (item["value"] == null)
                {
                    throw new ArgumentException("Give 'value' or 'expression'.");
                }

                var before = ParameterExpression.RawKey(parameter);
                if (dryRun)
                    return new
                    {
                        elementId = element.Id.GetValue(), targetId = target.Id.GetValue(), parameter = name,
                        current = ParameterExpression.Display(doc, parameter), value = value?.ToString()
                    };

                var error = ParameterExpression.Write(doc, parameter, value);
                if (error != null) throw new InvalidOperationException(error);
                return new
                {
                    elementId = element.Id.GetValue(), targetId = target.Id.GetValue(), parameter = name,
                    value = ParameterExpression.Display(doc, parameter),
                    changed = before != ParameterExpression.RawKey(parameter)
                };
            };

            List<JObject> results;
            if (dryRun)
            {
                results = new List<JObject>();
                for (var index = 0; index < items.Count; index++)
                {
                    try
                    {
                        var result = JObject.FromObject(action(items[index]));
                        result.AddFirst(new JProperty("success", true));
                        result.AddFirst(new JProperty("index", index));
                        results.Add(result);
                    }
                    catch (Exception ex)
                    {
                        results.Add(new JObject { ["index"] = index, ["success"] = false, ["message"] = ex.Message });
                    }
                }
            }
            else
            {
                results = DocumentationUtils.RunBatch(doc, "MCP: Set Parameters", items, action);
            }

            var succeeded = results.Count(r => r.Value<bool>("success"));
            return Ok(dryRun
                    ? $"Dry run: {succeeded} of {results.Count} value(s) computed; nothing written."
                    : $"Set {succeeded} of {results.Count} parameter value(s).",
                DocumentationUtils.Summarize(results));
        }

        // ---------------------------------------------------------------- filter mode

        private AIResult<object> RunFilter(Document doc, JObject parameters)
        {
            var warnings = new List<string>();
            var name = parameters.Value<string>("parameter");
            if (string.IsNullOrWhiteSpace(name)) return Fail("Give 'parameter' (or 'items').");
            var expression = parameters.Value<string>("expression");
            var valueToken = parameters["value"];
            if ((expression == null) == (valueToken == null))
                return Fail("Give exactly one of 'value' or 'expression'.");

            var onType = parameters.Value<bool?>("onType") ?? false;
            var skipIfUnresolved = parameters.Value<bool?>("skipIfUnresolved") ?? true;
            var dryRun = parameters.Value<bool?>("dryRun") ?? false;
            var maxElements = parameters.Value<int?>("maxElements") ?? 20000;
            var tokens = expression == null ? null : ParameterExpression.Parse(expression);

            var levels = ModelSelectionUtils.SortedLevels(doc);
            var elements = ModelSelectionUtils.Collect(doc, parameters, levels, warnings);
            if (elements.Count > maxElements)
                return Fail($"{elements.Count} elements match, more than maxElements ({maxElements}). Narrow the filters or raise maxElements.");

            // Plan: (target, value) per instance, or per distinct type with onType.
            var unresolvedCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var skipped = new List<object>();
            var plan = new List<KeyValuePair<Element, JToken>>();
            var conflicts = new List<object>();

            Func<Element, JToken> compute = element =>
            {
                if (tokens == null) return valueToken;
                var unresolved = new List<string>();
                var text = ParameterExpression.Evaluate(doc, element, tokens, unresolved);
                foreach (var placeholder in unresolved)
                    unresolvedCounts[placeholder] = unresolvedCounts.TryGetValue(placeholder, out var n) ? n + 1 : 1;
                if (unresolved.Count > 0 && skipIfUnresolved)
                {
                    if (skipped.Count < SampleSize)
                        skipped.Add(new { elementId = element.Id.GetValue(), unresolved });
                    return null;
                }
                return new JValue(text);
            };

            var skippedCount = 0;
            if (onType)
            {
                foreach (var group in elements.GroupBy(e => e.GetTypeId().GetValue()).OrderBy(g => g.Key))
                {
                    var type = doc.GetElement(group.First().GetTypeId());
                    if (type == null)
                    {
                        skippedCount += group.Count();
                        if (skipped.Count < SampleSize) skipped.Add(new { elementId = group.First().Id.GetValue(), reason = "Element has no type." });
                        continue;
                    }

                    JToken chosen = null;
                    var distinct = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var element in group)
                    {
                        var value = compute(element);
                        if (value == null) continue;
                        if (chosen == null)
                        {
                            chosen = value;
                        }
                        distinct.Add(value.ToString());
                        if (tokens == null) break; // a constant value: one evaluation per type
                    }

                    if (chosen == null)
                    {
                        skippedCount++;
                        continue;
                    }

                    if (distinct.Count > 1 && conflicts.Count < SampleSize)
                        conflicts.Add(new { typeId = type.Id.GetValue(), typeName = type.Name, used = chosen.ToString(), values = distinct.Take(5).ToList() });
                    plan.Add(new KeyValuePair<Element, JToken>(type, chosen));
                }
            }
            else
            {
                foreach (var element in elements)
                {
                    var value = compute(element);
                    if (value == null)
                    {
                        skippedCount++;
                        continue;
                    }
                    plan.Add(new KeyValuePair<Element, JToken>(element, value));
                }
            }

            var response = new JObject
            {
                ["parameter"] = name,
                ["onType"] = onType,
                ["dryRun"] = dryRun,
                ["matched"] = elements.Count,
                ["targets"] = plan.Count,
                ["byCategory"] = ModelSelectionUtils.CountBy(elements, ModelSelectionUtils.CategoryKey)
            };

            var setCount = 0;
            var unchanged = 0;
            var failures = new List<object>();
            var failedCount = 0;
            var samples = new JArray();

            if (dryRun)
            {
                foreach (var entry in plan)
                {
                    var parameter = TryFindWritable(doc, entry.Key, name, onType, out var error);
                    if (parameter == null)
                    {
                        failedCount++;
                        if (failures.Count < SampleSize) failures.Add(new { id = entry.Key.Id.GetValue(), message = error });
                        continue;
                    }
                    if (samples.Count < SampleSize)
                        samples.Add(new JObject
                        {
                            ["id"] = entry.Key.Id.GetValue(),
                            ["current"] = ParameterExpression.Display(doc, parameter),
                            ["value"] = entry.Value?.ToString()
                        });
                }
            }
            else if (plan.Count > 0)
            {
                using (var transaction = ModelSelectionUtils.StartTransaction(doc, "MCP: Set Parameters", warnings))
                {
                    foreach (var entry in plan)
                    {
                        var parameter = TryFindWritable(doc, entry.Key, name, onType, out var error);
                        if (parameter != null)
                        {
                            var before = ParameterExpression.RawKey(parameter);
                            error = ParameterExpression.Write(doc, parameter, entry.Value);
                            if (error == null)
                            {
                                if (before == ParameterExpression.RawKey(parameter)) unchanged++;
                                else setCount++;
                                if (samples.Count < SampleSize)
                                    samples.Add(new JObject
                                    {
                                        ["id"] = entry.Key.Id.GetValue(),
                                        ["value"] = ParameterExpression.Display(doc, parameter)
                                    });
                                continue;
                            }
                        }

                        failedCount++;
                        if (failures.Count < SampleSize) failures.Add(new { id = entry.Key.Id.GetValue(), message = error });
                    }

                    if (setCount == 0)
                    {
                        transaction.RollBack();
                    }
                    else
                    {
                        var status = transaction.Commit();
                        if (status != TransactionStatus.Committed)
                            return Fail($"Parameter changes were not committed ({status}). {string.Join(" ", warnings.Take(5))}");
                    }
                }
            }

            if (!dryRun)
            {
                response["set"] = setCount;
                response["unchanged"] = unchanged;
            }
            response["skippedUnresolved"] = skippedCount;
            response["failed"] = failedCount;
            if (unresolvedCounts.Count > 0) response["unresolvedPlaceholders"] = JObject.FromObject(unresolvedCounts);
            if (skipped.Count > 0) response["skippedSamples"] = JArray.FromObject(skipped);
            if (conflicts.Count > 0) response["conflicts"] = JArray.FromObject(conflicts);
            if (failures.Count > 0) response["failures"] = JArray.FromObject(failures);
            response["samples"] = samples;
            if (warnings.Count > 0) response["warnings"] = new JArray(warnings.Take(20));

            var what = onType ? "type(s)" : "element(s)";
            var message = dryRun
                ? $"Dry run: {plan.Count - failedCount} {what} would get '{name}'; {skippedCount} skipped (unresolved), {failedCount} failed. Nothing written."
                : $"Set '{name}' on {setCount} {what}; {unchanged} unchanged, {skippedCount} skipped (unresolved), {failedCount} failed.";
            if (elements.Count == 0) message = "No matching elements.";
            return Ok(message, response);
        }

        // ---------------------------------------------------------------- helpers

        private static List<ParameterExpression.Token> Tokens(Dictionary<string, List<ParameterExpression.Token>> cache, string expression)
        {
            if (!cache.TryGetValue(expression, out var tokens))
            {
                tokens = ParameterExpression.Parse(expression);
                cache[expression] = tokens;
            }
            return tokens;
        }

        private static Element Target(Document doc, Element element, bool onType)
        {
            if (!onType || element is ElementType) return element;
            return doc.GetElement(element.GetTypeId())
                   ?? throw new InvalidOperationException($"Element {element.Id.GetValue()} has no type.");
        }

        private static Parameter TryFindWritable(Document doc, Element target, string name, bool onType, out string error)
        {
            try
            {
                error = null;
                return FindWritable(doc, target, target, name, onType);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>The named parameter of the target, with a hint when it lives on the type/instance instead.</summary>
        private static Parameter FindWritable(Document doc, Element target, Element element, string name, bool onType)
        {
            var candidates = target.GetParameters(name);
            var parameter = candidates?.FirstOrDefault(p => !p.IsReadOnly) ?? candidates?.FirstOrDefault() ?? target.LookupParameter(name);
            if (parameter != null)
            {
                if (parameter.IsReadOnly) throw new InvalidOperationException($"Parameter '{name}' is read-only on {target.Id.GetValue()}.");
                return parameter;
            }

            if (!onType && !(target is ElementType))
            {
                var type = doc.GetElement(target.GetTypeId());
                if (type?.LookupParameter(name) != null)
                    throw new InvalidOperationException($"'{name}' is a type parameter (of '{type.Name}'); pass onType:true.");
            }
            else if (onType && !ReferenceEquals(element, target) && element.LookupParameter(name) != null)
            {
                throw new InvalidOperationException($"'{name}' is an instance parameter; drop onType.");
            }

            throw new InvalidOperationException($"Parameter '{name}' not found on {target.Id.GetValue()}.");
        }
    }
}
