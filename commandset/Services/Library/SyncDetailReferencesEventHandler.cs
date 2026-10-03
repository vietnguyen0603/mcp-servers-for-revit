using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Library
{
    /// <summary>
    ///     Rewrites the Detail Number / Sheet Number parameters of manual
    ///     reference bubbles. <c>mappings</c> points listed bubbles at a placed
    ///     view (its current viewport detail number and sheet number);
    ///     <c>renumberFrom</c>/<c>to</c> rewrites every bubble that refers to an
    ///     old sheet/detail pair. Dry run by default.
    /// </summary>
    public class SyncDetailReferencesEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Sync Detail References";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var options = DetailReferenceOptions.Read(parameters);
            var dryRun = parameters.Value<bool?>("dryRun") ?? true;
            var mappings = parameters["mappings"] as JArray;
            var renumberFrom = parameters["renumberFrom"] as JObject;
            var renumberTo = parameters["to"] as JObject;

            if ((mappings == null || mappings.Count == 0) && renumberFrom == null)
                return Fail("Provide 'mappings' or 'renumberFrom' with 'to'.");
            if (renumberFrom != null && renumberTo == null)
                return Fail("'renumberFrom' requires 'to'.");

            var index = ViewportIndex.Build(doc);
            var planned = new List<Change>();
            var errors = new List<object>();

            if (mappings != null)
                PlanMappings(doc, mappings, index, options, planned, errors);
            if (renumberFrom != null)
                PlanRenumber(doc, renumberFrom, renumberTo, options, planned);

            // A bubble listed twice keeps its last planned change.
            planned = planned
                .GroupBy(c => c.Bubble.Instance.Id.GetValue())
                .Select(g => g.Last())
                .OrderBy(c => c.Bubble.Instance.Id.GetValue())
                .ToList();
            var changes = planned.Where(c => c.Changed).ToList();

            if (dryRun || changes.Count == 0)
            {
                return Ok(
                    $"{(dryRun ? "Dry run: " : string.Empty)}{changes.Count} of {planned.Count} bubbles would change; {errors.Count} errors.",
                    new
                    {
                        dryRun,
                        planned = planned.Count,
                        toChange = changes.Count,
                        unchanged = planned.Count - changes.Count,
                        errors,
                        changes = planned.Select(c => c.Describe(null, null)).ToList()
                    });
            }

            var results = new List<object>();
            var succeeded = 0;
            using (var transaction = DocumentationUtils.StartTransaction(doc, "MCP: Sync Detail References"))
            {
                foreach (var change in changes)
                {
                    var sub = new SubTransaction(doc);
                    sub.Start();
                    try
                    {
                        var instance = change.Bubble.Instance;
                        if (change.OldSheet != change.NewSheet)
                            DetailReferenceUtils.WriteValue(instance, change.Bubble.SheetParameter, change.NewSheet);
                        if (change.OldDetail != change.NewDetail)
                            DetailReferenceUtils.WriteValue(instance, change.Bubble.DetailParameter, change.NewDetail);
                        sub.Commit();
                        succeeded++;
                        results.Add(change.Describe(true, null));
                    }
                    catch (Exception ex)
                    {
                        if (sub.HasStarted())
                            sub.RollBack();
                        results.Add(change.Describe(false, ex.Message));
                    }
                }

                var status = transaction.Commit();
                if (status != TransactionStatus.Committed)
                    return Fail($"Transaction was not committed ({status}).");
            }

            return Ok($"Updated {succeeded} of {changes.Count} bubbles; {errors.Count} input errors.", new
            {
                dryRun = false,
                updated = succeeded,
                failed = changes.Count - succeeded,
                unchanged = planned.Count - changes.Count,
                errors,
                results
            });
        }

        private static void PlanMappings(Document doc, JArray mappings, ViewportIndex index,
            DetailReferenceOptions options, List<Change> planned, List<object> errors)
        {
            for (var i = 0; i < mappings.Count; i++)
            {
                var mapping = mappings[i] as JObject;
                var targetId = DocumentationUtils.ReadId(mapping, "targetViewId");
                var target = DocumentationUtils.GetElement<View>(doc, targetId);
                if (target == null)
                {
                    errors.Add(new { mapping = i, message = $"targetViewId {targetId} is not a view." });
                    continue;
                }

                var placement = index.FindView(target.Id);
                if (placement == null)
                {
                    errors.Add(new { mapping = i, message = $"View '{target.Name}' is not placed on a sheet." });
                    continue;
                }

                if (!(mapping?["bubbleIds"] is JArray ids) || ids.Count == 0)
                {
                    errors.Add(new { mapping = i, message = "'bubbleIds' must be a non-empty array." });
                    continue;
                }

                foreach (var token in ids)
                {
                    var id = token.Value<long>();
                    var instance = doc.GetElement(id.ToRevitElementId()) as FamilyInstance;
                    var detail = instance == null ? null : DetailReferenceUtils.FindParameter(instance, options.DetailNumberParam);
                    var sheet = instance == null ? null : DetailReferenceUtils.FindParameter(instance, options.SheetNumberParam);
                    if (detail == null || sheet == null)
                    {
                        errors.Add(new
                        {
                            mapping = i,
                            bubbleId = id,
                            message = instance == null
                                ? "Not a family instance."
                                : $"Missing '{options.DetailNumberParam}' or '{options.SheetNumberParam}' parameter."
                        });
                        continue;
                    }

                    planned.Add(new Change
                    {
                        Bubble = new ReferenceBubble { Instance = instance, DetailParameter = detail, SheetParameter = sheet },
                        NewSheet = placement.Sheet.SheetNumber,
                        NewDetail = placement.DetailNumber,
                        TargetViewId = target.Id.GetValue()
                    });
                }
            }
        }

        private static void PlanRenumber(Document doc, JObject from, JObject to, DetailReferenceOptions options,
            List<Change> planned)
        {
            var fromSheet = from.Value<string>("sheetNumber") ?? throw new ArgumentException("'renumberFrom.sheetNumber' is required.");
            var fromDetail = from.Value<string>("detailNumber") ?? throw new ArgumentException("'renumberFrom.detailNumber' is required.");
            var toSheet = to.Value<string>("sheetNumber") ?? throw new ArgumentException("'to.sheetNumber' is required.");
            var toDetail = to.Value<string>("detailNumber") ?? throw new ArgumentException("'to.detailNumber' is required.");

            foreach (var bubble in DetailReferenceUtils.CollectBubbles(doc, options, out _))
            {
                if (!DetailReferenceUtils.SameValue(bubble.SheetNumber, fromSheet)
                    || !DetailReferenceUtils.SameValue(bubble.DetailNumber, fromDetail))
                    continue;
                planned.Add(new Change { Bubble = bubble, NewSheet = toSheet.Trim(), NewDetail = toDetail.Trim() });
            }
        }

        private sealed class Change
        {
            private ReferenceBubble _bubble;

            /// <summary>Setting the bubble snapshots its current values.</summary>
            public ReferenceBubble Bubble
            {
                get => _bubble;
                set
                {
                    _bubble = value;
                    OldSheet = value.SheetNumber;
                    OldDetail = value.DetailNumber;
                }
            }

            public string OldSheet { get; private set; }
            public string OldDetail { get; private set; }
            public string NewSheet { get; set; }
            public string NewDetail { get; set; }
            public long? TargetViewId { get; set; }

            public bool Changed => OldSheet != NewSheet || OldDetail != NewDetail;

            public object Describe(bool? success, string message)
            {
                var instance = Bubble.Instance;
                return new
                {
                    bubbleId = instance.Id.GetValue(),
                    hostViewId = instance.OwnerViewId.GetValue(),
                    hostViewName = (instance.Document.GetElement(instance.OwnerViewId) as View)?.Name,
                    targetViewId = TargetViewId,
                    before = new { sheetNumber = OldSheet, detailNumber = OldDetail },
                    after = new { sheetNumber = NewSheet, detailNumber = NewDetail },
                    changed = Changed,
                    success,
                    message
                };
            }
        }
    }
}
