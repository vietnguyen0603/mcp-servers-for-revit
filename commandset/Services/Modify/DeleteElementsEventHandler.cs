using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Modify
{
    /// <summary>
    ///     delete_elements: bulk delete by filter (categories / elementIds, levels,
    ///     comments, mark, type name). dryRun (default true) only reports counts per
    ///     category and level; dryRun:false deletes in one transaction.
    /// </summary>
    public class DeleteElementsEventHandler : JsonParameterEventHandler
    {
        private const int SampleSize = 20;

        public override string GetName() => "Delete Elements";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var warnings = new List<string>();
            var levels = ModelSelectionUtils.SortedLevels(doc);
            var elements = ModelSelectionUtils.Collect(doc, parameters, levels, warnings);
            var dryRun = parameters.Value<bool?>("dryRun") ?? true;

            var response = new JObject
            {
                ["dryRun"] = dryRun,
                ["matched"] = elements.Count,
                ["byCategory"] = ModelSelectionUtils.CountBy(elements, ModelSelectionUtils.CategoryKey),
                ["byLevel"] = ModelSelectionUtils.CountBy(elements, e => ModelSelectionUtils.LevelName(doc, ModelSelectionUtils.GetLevelId(e))),
                ["sampleIds"] = new JArray(elements.Take(SampleSize).Select(e => e.Id.GetValue()))
            };

            if (elements.Count == 0 || dryRun)
            {
                AddWarnings(response, warnings);
                return Ok(dryRun
                    ? $"Dry run: {elements.Count} element(s) would be deleted. Pass dryRun:false to delete."
                    : "No matching elements.", response);
            }

            var ids = elements.Select(e => e.Id).ToList();
            var deleted = 0;
            var failed = new List<long>();
            using (var transaction = ModelSelectionUtils.StartTransaction(doc, "MCP: Delete Elements", warnings))
            {
                try
                {
                    deleted = doc.Delete(ids).Count;
                }
                catch (Exception ex)
                {
                    // Fall back to one element at a time so one blocked element does not stop the rest.
                    warnings.Add($"Bulk delete failed ({ex.Message}); deleting one by one.");
                    deleted = 0;
                    foreach (var id in ids)
                    {
                        if (doc.GetElement(id) == null) continue; // removed with an earlier element
                        var sub = new SubTransaction(doc);
                        sub.Start();
                        try
                        {
                            deleted += doc.Delete(id).Count;
                            sub.Commit();
                        }
                        catch (Exception inner)
                        {
                            if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                            failed.Add(id.GetValue());
                            if (failed.Count <= 5) warnings.Add($"{id.GetValue()}: {inner.Message}");
                        }
                    }
                }

                var status = transaction.Commit();
                if (status != TransactionStatus.Committed)
                {
                    AddWarnings(response, warnings);
                    return Fail($"Delete was not committed ({status}). {string.Join(" ", warnings.Take(5))}");
                }
            }

            response["deleted"] = deleted;
            if (failed.Count > 0)
            {
                response["failedCount"] = failed.Count;
                response["failedIds"] = new JArray(failed.Take(SampleSize));
            }
            AddWarnings(response, warnings);
            return Ok($"Deleted {elements.Count - failed.Count} matched element(s) ({deleted} including dependents).", response);
        }

        private static void AddWarnings(JObject response, List<string> warnings)
        {
            if (warnings.Count > 0) response["warnings"] = new JArray(warnings.Take(20));
        }
    }
}
