using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Creates a schedule for one category (or a multi-category schedule)
    ///     with fields, filters and sorting/grouping. Fields are matched by
    ///     their schedulable name, case-insensitively. Fields used only by a
    ///     filter or sort are added as hidden fields.
    /// </summary>
    public class CreateScheduleEventHandler : JsonParameterEventHandler
    {
        private const int MaxFilters = 8;

        public override string GetName() => "Create Schedule";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var categoryName = parameters.Value<string>("category");
            var categoryId = ElementId.InvalidElementId;
            if (!string.IsNullOrWhiteSpace(categoryName)
                && !string.Equals(categoryName.Replace("-", "").Replace(" ", ""), "MultiCategory", StringComparison.OrdinalIgnoreCase))
            {
                var category = DocumentationUtils.ResolveCategory(doc, categoryName)
                               ?? throw new ArgumentException($"Category '{categoryName}' not found.");
                categoryId = category.Id;
            }

            var fieldSpecs = (parameters["fields"] as JArray ?? new JArray()).Select(ParseFieldSpec).ToList();
            if (fieldSpecs.Count == 0)
                throw new ArgumentException("'fields' must list at least one field name.");
            var filters = parameters["filters"] as JArray ?? new JArray();
            if (filters.Count > MaxFilters)
                throw new ArgumentException($"Revit schedules support at most {MaxFilters} filters.");
            var sorts = parameters["sortBy"] as JArray ?? new JArray();

            var warnings = new List<string>();
            ViewSchedule schedule;

            using (var transaction = DocumentationUtils.StartTransaction(doc, "MCP: Create Schedule"))
            {
                schedule = ViewSchedule.CreateSchedule(doc, categoryId);
                var name = parameters.Value<string>("name");
                if (!string.IsNullOrWhiteSpace(name))
                    schedule.Name = name.Trim();

                var definition = schedule.Definition;
                var schedulable = definition.GetSchedulableFields();
                var added = new Dictionary<string, ScheduleField>(StringComparer.OrdinalIgnoreCase);

                ScheduleField AddField(string fieldName, bool hidden)
                {
                    if (added.TryGetValue(fieldName, out var existing))
                        return existing;
                    var match = schedulable.FirstOrDefault(f =>
                        string.Equals(f.GetName(doc), fieldName, StringComparison.OrdinalIgnoreCase));
                    if (match == null)
                    {
                        warnings.Add($"Field '{fieldName}' is not schedulable for this category.");
                        return null;
                    }

                    var field = definition.AddField(match);
                    field.IsHidden = hidden;
                    added[fieldName] = field;
                    return field;
                }

                foreach (var spec in fieldSpecs)
                {
                    var field = AddField(spec.Name, spec.Hidden);
                    if (field != null && !string.IsNullOrWhiteSpace(spec.Heading))
                        field.ColumnHeading = spec.Heading;
                }

                if (added.Count == 0)
                {
                    transaction.RollBack();
                    return Fail("None of the requested fields are schedulable for this category. " +
                                $"Available fields include: {string.Join(", ", schedulable.Select(f => f.GetName(doc)).Distinct().OrderBy(n => n).Take(80))}");
                }

                foreach (var filter in filters.OfType<JObject>())
                {
                    var field = AddField(filter.Value<string>("field") ?? string.Empty, true);
                    if (field == null)
                        continue;
                    var error = AddFilter(definition, field, filter);
                    if (error != null)
                        warnings.Add(error);
                }

                foreach (var sort in sorts.OfType<JObject>())
                {
                    var field = AddField(sort.Value<string>("field") ?? string.Empty, true);
                    if (field == null)
                        continue;
                    var sortField = new ScheduleSortGroupField(field.FieldId,
                        DocumentationUtils.ParseEnum(sort.Value<string>("order"), ScheduleSortOrder.Ascending))
                    {
                        ShowHeader = sort.Value<bool?>("showHeader") ?? false,
                        ShowFooter = sort.Value<bool?>("showFooter") ?? false,
                        ShowBlankLine = sort.Value<bool?>("blankLine") ?? false
                    };
                    definition.AddSortGroupField(sortField);
                }

                definition.IsItemized = parameters.Value<bool?>("itemized") ?? true;

                var status = transaction.Commit();
                if (status != TransactionStatus.Committed)
                    return Fail($"Schedule transaction was not committed ({status}).");
            }

            return Ok($"Created schedule '{schedule.Name}'.", new
            {
                scheduleId = schedule.Id.GetValue(),
                name = schedule.Name,
                fields = schedule.Definition.GetFieldOrder()
                    .Select(id => schedule.Definition.GetField(id))
                    .Select(f => new { name = f.GetName(), heading = f.ColumnHeading, hidden = f.IsHidden })
                    .ToList(),
                warnings = warnings.Count > 0 ? warnings : null
            });
        }

        private static string AddFilter(ScheduleDefinition definition, ScheduleField field, JObject filter)
        {
            var type = DocumentationUtils.ParseEnum(filter.Value<string>("operator"), ScheduleFilterType.Equal);
            var value = filter["value"];
            var candidates = new List<Func<ScheduleFilter>>();

            if (type == ScheduleFilterType.HasValue || type == ScheduleFilterType.HasNoValue)
            {
                candidates.Add(() => new ScheduleFilter(field.FieldId, type));
            }
            else if (value == null || value.Type == JTokenType.Null)
            {
                return $"Filter on '{field.GetName()}' requires a 'value'.";
            }
            else if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float)
            {
                var number = value.Value<double>();
                var internalValue = IsLengthField(field) ? DocumentationUtils.MmToFeet(number) : number;
                candidates.Add(() => new ScheduleFilter(field.FieldId, type, internalValue));
                if (Math.Abs(number - Math.Round(number)) < 1e-9)
                    candidates.Add(() => new ScheduleFilter(field.FieldId, type, (int)Math.Round(number)));
                candidates.Add(() => new ScheduleFilter(field.FieldId, type, value.ToString()));
            }
            else
            {
                candidates.Add(() => new ScheduleFilter(field.FieldId, type, value.ToString()));
            }

            string lastError = null;
            foreach (var create in candidates)
            {
                try
                {
                    definition.AddFilter(create());
                    return null;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }

            return $"Filter on '{field.GetName()}' was not added: {lastError}";
        }

        private static bool IsLengthField(ScheduleField field)
        {
            try
            {
#if REVIT2022_OR_GREATER
                return field.GetSpecTypeId() == SpecTypeId.Length;
#else
                return field.UnitType == UnitType.UT_Length;
#endif
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static FieldSpec ParseFieldSpec(JToken token)
        {
            if (token.Type == JTokenType.String)
                return new FieldSpec { Name = token.ToString() };
            if (token is JObject obj && !string.IsNullOrWhiteSpace(obj.Value<string>("name")))
                return new FieldSpec
                {
                    Name = obj.Value<string>("name"),
                    Heading = obj.Value<string>("heading"),
                    Hidden = obj.Value<bool?>("hidden") ?? false
                };
            throw new ArgumentException("Each field must be a name or an object with 'name'.");
        }

        private class FieldSpec
        {
            public string Name { get; set; }
            public string Heading { get; set; }
            public bool Hidden { get; set; }
        }
    }
}
