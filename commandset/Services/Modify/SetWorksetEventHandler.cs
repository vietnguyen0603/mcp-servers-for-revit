using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Modify
{
    /// <summary>
    ///     set_workset: moves filtered model elements to a (possibly new) user workset
    ///     by setting ELEM_PARTITION_PARAM, or lists the user worksets. Optionally
    ///     enables worksharing on a non-workshared document first.
    /// </summary>
    public class SetWorksetEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Set Workset";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var listOnly = parameters.Value<bool?>("listWorksets") ?? false;
            var enable = parameters.Value<bool?>("enableWorksharing") ?? false;
            var notes = new List<string>();

            if (!doc.IsWorkshared)
            {
                if (!enable)
                {
                    if (listOnly)
                        return Ok("The document is not workshared.", new JObject { ["workshared"] = false, ["worksets"] = new JArray() });
                    return Fail("The document is not workshared. Pass enableWorksharing:true to enable worksharing " +
                                "(worksets 'Shared Levels and Grids' and 'Workset1'; this cannot be undone).");
                }
                if (doc.IsFamilyDocument)
                    return Fail("Worksharing cannot be enabled in a family document.");
                try
                {
                    doc.EnableWorksharing("Shared Levels and Grids", "Workset1");
                    notes.Add("Worksharing enabled (worksets 'Shared Levels and Grids' and 'Workset1'). Save the model as a central file to share it.");
                }
                catch (Exception ex)
                {
                    return Fail($"Could not enable worksharing: {ex.Message}");
                }
            }

            if (listOnly)
            {
                var listed = ListWorksets(doc);
                if (notes.Count > 0) listed["notes"] = new JArray(notes);
                return Ok($"{((JArray)listed["worksets"]).Count} user workset(s).", listed);
            }

            var name = parameters.Value<string>("workset")?.Trim();
            if (string.IsNullOrEmpty(name))
                return Fail("Give 'workset' (or listWorksets: true).");
            var createIfMissing = parameters.Value<bool?>("createIfMissing") ?? true;

            var warnings = new List<string>();
            var levels = ModelSelectionUtils.SortedLevels(doc);
            var elements = ModelSelectionUtils.Collect(doc, parameters, levels, warnings);

            var counts = new Dictionary<string, int[]>(StringComparer.Ordinal); // changed, already, readOnly, failed
            var created = false;
            Workset workset;
            using (var transaction = ModelSelectionUtils.StartTransaction(doc, "MCP: Set Workset", warnings))
            {
                workset = FindWorkset(doc, name);
                if (workset == null)
                {
                    if (!createIfMissing)
                    {
                        transaction.RollBack();
                        return Fail($"Workset '{name}' not found. Existing: {string.Join(", ", UserWorksets(doc).Select(w => w.Name))}.");
                    }
                    if (!WorksetTable.IsWorksetNameUnique(doc, name))
                    {
                        transaction.RollBack();
                        return Fail($"'{name}' is already used by a non-user workset.");
                    }
                    workset = Workset.Create(doc, name);
                    created = true;
                }

                var worksetValue = workset.Id.IntegerValue;
                foreach (var element in elements)
                {
                    var key = ModelSelectionUtils.CategoryKey(element);
                    if (!counts.TryGetValue(key, out var c))
                        counts[key] = c = new int[4];

                    var parameter = element.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    if (parameter != null && parameter.AsInteger() == worksetValue)
                    {
                        c[1]++;
                        continue;
                    }
                    if (parameter == null || parameter.IsReadOnly)
                    {
                        c[2]++;
                        continue;
                    }
                    try
                    {
                        if (parameter.Set(worksetValue)) c[0]++;
                        else c[3]++;
                    }
                    catch (Exception ex)
                    {
                        c[3]++;
                        if (c[3] <= 3) warnings.Add($"{element.Id.GetValue()} ({key}): {ex.Message}");
                    }
                }

                var status = transaction.Commit();
                if (status != TransactionStatus.Committed)
                    return Fail($"Workset change was not committed ({status}). {string.Join(" ", warnings.Take(5))}");
            }

            var byCategory = new JObject();
            foreach (var pair in counts.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var entry = new JObject { ["changed"] = pair.Value[0] };
                if (pair.Value[1] > 0) entry["already"] = pair.Value[1];
                if (pair.Value[2] > 0) entry["readOnly"] = pair.Value[2];
                if (pair.Value[3] > 0) entry["failedCount"] = pair.Value[3];
                byCategory[pair.Key] = entry;
            }

            var changed = counts.Values.Sum(c => c[0]);
            var response = new JObject
            {
                ["workset"] = new JObject { ["id"] = workset.Id.IntegerValue, ["name"] = workset.Name, ["created"] = created },
                ["matched"] = elements.Count,
                ["changed"] = changed,
                ["byCategory"] = byCategory
            };
            if (notes.Count > 0) response["notes"] = new JArray(notes);
            if (warnings.Count > 0) response["warnings"] = new JArray(warnings.Take(20));
            return Ok($"Moved {changed} of {elements.Count} element(s) to workset '{workset.Name}'.", response);
        }

        private static IEnumerable<Workset> UserWorksets(Document doc) =>
            new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().OrderBy(w => w.Name, StringComparer.Ordinal);

        private static Workset FindWorkset(Document doc, string name)
        {
            var worksets = UserWorksets(doc).ToList();
            return worksets.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.Ordinal))
                   ?? worksets.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static JObject ListWorksets(Document doc)
        {
            var active = doc.GetWorksetTable().GetActiveWorksetId();
            var list = new JArray();
            foreach (var workset in UserWorksets(doc))
            {
                var count = new FilteredElementCollector(doc)
                    .WherePasses(new ElementWorksetFilter(workset.Id, false))
                    .WhereElementIsNotElementType()
                    .GetElementCount();
                list.Add(new JObject
                {
                    ["id"] = workset.Id.IntegerValue,
                    ["name"] = workset.Name,
                    ["isOpen"] = workset.IsOpen,
                    ["isEditable"] = workset.IsEditable,
                    ["isDefault"] = workset.IsDefaultWorkset,
                    ["isActive"] = workset.Id == active,
                    ["owner"] = workset.Owner,
                    ["elementCount"] = count
                });
            }
            return new JObject { ["workshared"] = true, ["worksets"] = list };
        }
    }
}
