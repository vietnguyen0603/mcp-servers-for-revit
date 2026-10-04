using System.Diagnostics;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Structure
{
    /// <summary>
    ///     join_elements: bulk Join Geometry between categories. For each
    ///     {cut, by} pair the elements of `by` end up cutting the intersecting
    ///     elements of `cut`. Broad phase: bounding-box sweep; narrow phase:
    ///     ElementIntersectsElementFilter. Operations run in batches, one
    ///     transaction each, with join warnings deleted.
    /// </summary>
    public class JoinElementsEventHandler : JsonParameterEventHandler
    {
        private enum OpKind
        {
            Join,
            Switch,
            Unjoin
        }

        private class Op
        {
            public OpKind Kind;
            public ModelQaUtils.ElementBox Cut;
            public ModelQaUtils.ElementBox By;
            public PairStats Stats;
            public bool SameCategory;
        }

        private class PairStats
        {
            public string Cut;
            public string By;
            public int Candidates;
            public int Tested;
            public int NotIntersecting;
            public int NotJoined;
            public int AlreadyOk;
            public int Joined;
            public int Switched;
            public int Unjoined;
            public int Failed;

            public JObject ToJson() => new JObject
            {
                ["cut"] = Cut,
                ["by"] = By,
                ["candidates"] = Candidates,
                ["tested"] = Tested,
                ["joined"] = Joined,
                ["switched"] = Switched,
                ["alreadyOk"] = AlreadyOk,
                ["unjoined"] = Unjoined,
                ["notIntersecting"] = NotIntersecting,
                ["notJoined"] = NotJoined,
                ["failed"] = Failed
            };
        }

        public override string GetName() => "Join Elements";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            if (doc.IsFamilyDocument)
                return Fail("join_elements works in project documents only.");

            var watch = Stopwatch.StartNew();
            var pairsJson = DocumentationUtils.RequireArray(parameters, "pairs");
            var mode = (parameters.Value<string>("mode") ?? "join").Trim();
            if (!new[] { "join", "unjoin", "switchOrderOnly" }.Contains(mode, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Invalid mode '{mode}'. Use join, unjoin or switchOrderOnly.");
            var batchSize = Math.Max(1, parameters.Value<int?>("batchSize") ?? 500);
            var maxErrors = Math.Max(0, parameters.Value<int?>("maxErrors") ?? 20);
            var levels = ModelQaUtils.ReadNameSet(parameters, "levels");
            var ids = ModelQaUtils.ReadIdSet(parameters, "elementIds");

            if (levels != null)
            {
                var known = new FilteredElementCollector(doc).OfClass(typeof(Level)).Select(l => l.Name)
                    .ToList();
                var missing = levels.Where(n => !known.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
                if (missing.Count == levels.Count)
                    return Fail($"None of the levels exist: {string.Join(", ", missing)}.");
            }

            bool InScope(ModelQaUtils.ElementBox box) =>
                (levels == null || levels.Contains(box.LevelName)) && (ids == null || ids.Contains(box.Id));

            var ctx = new ModelQaUtils.Context(doc);
            var stats = new List<PairStats>();
            var ops = new List<Op>();
            var errors = new JArray();
            var failedTotal = 0;

            void AddError(Op op, string operation, string message)
            {
                failedTotal++;
                if (op != null) op.Stats.Failed++;
                if (errors.Count < maxErrors)
                    errors.Add(new JObject
                    {
                        ["operation"] = operation,
                        ["cutId"] = op?.Cut.Id,
                        ["byId"] = op?.By.Id,
                        ["message"] = message
                    });
            }

            // Plan (read-only): decide the operation for every candidate pair.
            foreach (var token in pairsJson)
            {
                if (!(token is JObject pair))
                    throw new ArgumentException("Each pair must be an object {cut, by}.");
                var cutCat = ModelQaUtils.ParseCategory(pair.Value<string>("cut"));
                var byCat = ModelQaUtils.ParseCategory(pair.Value<string>("by"));
                var cutType = pair.Value<string>("cutType");
                var byType = pair.Value<string>("byType");
                // A same-category pair has no cut order unless type filters tell the two sides apart
                // (e.g. pile cap 3000 mm cuts the 1000 mm raft).
                var same = cutCat == byCat && (string.IsNullOrWhiteSpace(cutType) || string.IsNullOrWhiteSpace(byType));
                var st = new PairStats { Cut = ModelQaUtils.CategoryLabel(cutCat), By = ModelQaUtils.CategoryLabel(byCat) };
                stats.Add(st);

                bool TypeMatches(ModelQaUtils.ElementBox box, string text) =>
                    string.IsNullOrWhiteSpace(text) || (box.TypeName ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
                var cutList = ctx.Boxes(cutCat).Where(b => TypeMatches(b, cutType)).ToList();
                var byList = cutCat == byCat && string.IsNullOrWhiteSpace(cutType) && string.IsNullOrWhiteSpace(byType)
                    ? cutList
                    : ctx.Boxes(byCat).Where(b => TypeMatches(b, byType)).ToList();
                var candidates = ModelQaUtils.SweepPairs(cutList, byList, ModelQaUtils.ToFeet(1));
                st.Candidates = candidates.Count;

                var toTest = new Dictionary<long, List<ModelQaUtils.ElementBox>>();
                var cutById = new Dictionary<long, ModelQaUtils.ElementBox>();
                foreach (var candidate in candidates)
                {
                    var cut = candidate.Key;
                    var by = candidate.Value;
                    if (!InScope(cut) && !InScope(by))
                        continue;
                    st.Tested++;

                    bool joined;
                    try
                    {
                        joined = JoinGeometryUtils.AreElementsJoined(doc, cut.Element, by.Element);
                    }
                    catch (Exception ex)
                    {
                        AddError(new Op { Cut = cut, By = by, Stats = st }, "check", ex.Message);
                        continue;
                    }

                    if (string.Equals(mode, "unjoin", StringComparison.OrdinalIgnoreCase))
                    {
                        if (joined)
                            ops.Add(new Op { Kind = OpKind.Unjoin, Cut = cut, By = by, Stats = st, SameCategory = same });
                        else
                            st.NotJoined++;
                        continue;
                    }

                    if (joined)
                    {
                        bool byCuts;
                        try
                        {
                            byCuts = same || JoinGeometryUtils.IsCuttingElementInJoin(doc, by.Element, cut.Element);
                        }
                        catch (Exception ex)
                        {
                            AddError(new Op { Cut = cut, By = by, Stats = st }, "check", ex.Message);
                            continue;
                        }

                        if (byCuts)
                            st.AlreadyOk++;
                        else
                            ops.Add(new Op { Kind = OpKind.Switch, Cut = cut, By = by, Stats = st, SameCategory = same });
                        continue;
                    }

                    if (string.Equals(mode, "switchOrderOnly", StringComparison.OrdinalIgnoreCase))
                    {
                        st.NotJoined++;
                        continue;
                    }

                    if (!toTest.TryGetValue(cut.Id, out var list))
                    {
                        toTest[cut.Id] = list = new List<ModelQaUtils.ElementBox>();
                        cutById[cut.Id] = cut;
                    }

                    list.Add(by);
                }

                // Narrow phase for unjoined candidates: one filtered collector per cut element.
                foreach (var entry in toTest)
                {
                    var cut = cutById[entry.Key];
                    var intersecting = Intersecting(ctx, cut, entry.Value);
                    foreach (var by in entry.Value)
                    {
                        if (intersecting.Contains(by.Id))
                            ops.Add(new Op { Kind = OpKind.Join, Cut = cut, By = by, Stats = st, SameCategory = same });
                        else
                            st.NotIntersecting++;
                    }
                }
            }

            // Execute in batches.
            var warningsDeleted = 0;
            var disjointWarnings = 0;
            var errorsResolved = 0;
            var transactions = 0;
            for (var start = 0; start < ops.Count; start += batchSize)
            {
                var batch = ops.Skip(start).Take(batchSize).ToList();
                var preprocessor = new JoinFailurePreprocessor();
                var done = new List<Op>();
                using (var transaction = new Transaction(doc, $"MCP: Join Elements ({mode})"))
                {
                    var options = transaction.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(preprocessor);
                    options.SetClearAfterRollback(true);
                    transaction.SetFailureHandlingOptions(options);
                    transaction.Start();
                    transactions++;

                    foreach (var op in batch)
                    {
                        try
                        {
                            Execute(doc, op);
                            done.Add(op);
                        }
                        catch (Exception ex)
                        {
                            AddError(op, op.Kind.ToString().ToLowerInvariant(), ex.Message);
                        }
                    }

                    var status = transaction.Commit();
                    if (status != TransactionStatus.Committed)
                    {
                        foreach (var op in done)
                            AddError(op, op.Kind.ToString().ToLowerInvariant(), $"Transaction was not committed ({status}).");
                        done.Clear();
                    }
                }

                warningsDeleted += preprocessor.WarningsDeleted;
                disjointWarnings += preprocessor.DisjointWarnings;
                errorsResolved += preprocessor.ErrorsResolved;
                foreach (var op in done)
                {
                    switch (op.Kind)
                    {
                        case OpKind.Join:
                            op.Stats.Joined++;
                            break;
                        case OpKind.Switch:
                            op.Stats.Switched++;
                            break;
                        default:
                            op.Stats.Unjoined++;
                            break;
                    }
                }
            }

            var totals = new JObject
            {
                ["candidates"] = stats.Sum(s => s.Candidates),
                ["tested"] = stats.Sum(s => s.Tested),
                ["joined"] = stats.Sum(s => s.Joined),
                ["switched"] = stats.Sum(s => s.Switched),
                ["alreadyOk"] = stats.Sum(s => s.AlreadyOk),
                ["unjoined"] = stats.Sum(s => s.Unjoined),
                ["notIntersecting"] = stats.Sum(s => s.NotIntersecting),
                ["notJoined"] = stats.Sum(s => s.NotJoined),
                ["failed"] = failedTotal
            };
            var response = new JObject
            {
                ["mode"] = mode,
                ["totals"] = totals,
                ["pairs"] = new JArray(stats.Select(s => s.ToJson())),
                ["transactions"] = transactions,
                ["warningsDeleted"] = warningsDeleted,
                ["disjointWarningsDeleted"] = disjointWarnings,
                ["errorsResolved"] = errorsResolved,
                ["errors"] = errors,
                ["elapsedSeconds"] = Math.Round(watch.Elapsed.TotalSeconds, 1)
            };

            var message = $"join_elements ({mode}): {totals["tested"]} pairs tested, {totals["joined"]} joined, " +
                          $"{totals["switched"]} switched, {totals["alreadyOk"]} already ok, {totals["unjoined"]} unjoined, " +
                          $"{failedTotal} failed.";
            return Ok(message, response);
        }

        /// <summary>Ids of the candidates whose solids intersect the cut element.</summary>
        private static HashSet<long> Intersecting(ModelQaUtils.Context ctx, ModelQaUtils.ElementBox cut,
            List<ModelQaUtils.ElementBox> candidates)
        {
            var result = new HashSet<long>();
            try
            {
                var ids = candidates.Select(c => c.Element.Id).ToList();
                var filter = new ElementIntersectsElementFilter(cut.Element);
                foreach (var id in new FilteredElementCollector(ctx.Doc, ids).WherePasses(filter).ToElementIds())
                    result.Add(id.GetValue());
                return result;
            }
            catch (Exception)
            {
                // Element not supported by the filter: fall back to solid booleans.
            }

            foreach (var candidate in candidates)
            {
                if (ctx.IntersectionFt3(cut, candidate) > 1e-6)
                    result.Add(candidate.Id);
            }

            return result;
        }

        private static void Execute(Document doc, Op op)
        {
            var cut = op.Cut.Element;
            var by = op.By.Element;
            switch (op.Kind)
            {
                case OpKind.Join:
                    JoinGeometryUtils.JoinGeometry(doc, by, cut);
                    if (!op.SameCategory && !JoinGeometryUtils.IsCuttingElementInJoin(doc, by, cut))
                        JoinGeometryUtils.SwitchJoinOrder(doc, by, cut);
                    break;
                case OpKind.Switch:
                    JoinGeometryUtils.SwitchJoinOrder(doc, by, cut);
                    break;
                case OpKind.Unjoin:
                    JoinGeometryUtils.UnjoinGeometry(doc, by, cut);
                    break;
            }
        }

        /// <summary>
        ///     Deletes warnings (notably "Highlighted elements are joined but do not
        ///     intersect") and resolves errors with their default resolution so a
        ///     batch is not lost to one bad pair.
        /// </summary>
        private class JoinFailurePreprocessor : IFailuresPreprocessor
        {
            public int WarningsDeleted;
            public int DisjointWarnings;
            public int ErrorsResolved;

            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                var resolved = false;
                foreach (var failure in failuresAccessor.GetFailureMessages())
                {
                    var severity = failure.GetSeverity();
                    if (severity == FailureSeverity.Warning)
                    {
                        var definition = failure.GetFailureDefinitionId();
                        if (definition.Guid == BuiltInFailures.JoinElementsFailures.JoiningDisjointWarn.Guid ||
                            definition.Guid == BuiltInFailures.JoinElementsFailures.JoiningDisjoint.Guid)
                            DisjointWarnings++;
                        failuresAccessor.DeleteWarning(failure);
                        WarningsDeleted++;
                    }
                    else if (severity == FailureSeverity.Error && failure.HasResolutions())
                    {
                        failuresAccessor.ResolveFailure(failure);
                        ErrorsResolved++;
                        resolved = true;
                    }
                }

                return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
            }
        }
    }
}
