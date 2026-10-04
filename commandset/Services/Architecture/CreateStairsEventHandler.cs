using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services.Foundations;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Architecture
{
    /// <summary>
    ///     create_stairs: straight (1 flight) or U-shaped (2 flights + mid landing) component stairs
    ///     between two levels. Each stair is built in its own StairsEditScope (started outside any
    ///     transaction, committed with a failure preprocessor), so each stair is one undo step.
    ///     All wire lengths are millimetres.
    /// </summary>
    public class CreateStairsEventHandler : JsonParameterEventHandler
    {
        private const double Tolerance = 1.0 / 304.8;

        public override string GetName() => "Create Stairs";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "stairs");
            var ctx = new FoundationModelUtils.Context(doc);
            var results = new List<JObject>();
            for (var index = 0; index < items.Count; index++)
            {
                try
                {
                    if (!(items[index] is JObject item))
                        throw new ArgumentException("Each stair must be an object.");
                    var result = CreateStairs(ctx, item);
                    result.AddFirst(new JProperty("success", true));
                    result.AddFirst(new JProperty("index", index));
                    results.Add(result);
                }
                catch (Exception ex)
                {
                    results.Add(new JObject { ["index"] = index, ["success"] = false, ["message"] = ex.Message });
                }
            }
            return Ok($"Created {results.Count(r => r.Value<bool>("success"))} of {results.Count} stairs.",
                DocumentationUtils.Summarize(results));
        }

        private static JObject CreateStairs(FoundationModelUtils.Context ctx, JObject item)
        {
            var doc = ctx.Doc;
            var warnings = new List<string>();

            // ---- inputs
            var baseLevel = FoundationModelUtils.ResolveLevel(ctx, item["baseLevel"], warnings, out var baseExtra, "baseLevel");
            var topLevel = FoundationModelUtils.ResolveLevel(ctx, item["topLevel"], warnings, out var topExtra, "topLevel");
            if (baseLevel.Id == topLevel.Id)
                throw new ArgumentException("baseLevel and topLevel must be different levels (use offsets for partial heights).");
            if (topLevel.Elevation < baseLevel.Elevation)
                throw new ArgumentException($"topLevel '{topLevel.Name}' is below baseLevel '{baseLevel.Name}'.");
            var baseOffset = FoundationModelUtils.ToFeet(item.Value<double?>("baseOffset") ?? 0) + baseExtra;
            var topOffset = FoundationModelUtils.ToFeet(item.Value<double?>("topOffset") ?? 0) + topExtra;
            var bottom = baseLevel.Elevation + baseOffset;
            var height = topLevel.Elevation + topOffset - bottom;
            if (height < 100 * Tolerance)
                throw new ArgumentException($"Stair height {FoundationModelUtils.ToMm(height)} mm is too small.");

            var start = FoundationModelUtils.ReadXY(item["start"], bottom, "start");
            var direction = ReadDirection(item);
            var width = FoundationModelUtils.ToFeet(item.Value<double?>("width") ?? throw new ArgumentException("'width' is required (mm)."));
            if (width <= 0) throw new ArgumentException("'width' must be positive (mm).");
            var flights = item.Value<int?>("flights") ?? 1;
            if (flights != 1 && flights != 2) throw new ArgumentException("'flights' must be 1 or 2.");
            var gap = FoundationModelUtils.ToFeet(item.Value<double?>("gap") ?? 0);
            var side = (item.Value<string>("side") ?? "right").Trim().ToLowerInvariant();
            if (side != "left" && side != "right") throw new ArgumentException("'side' must be left or right.");
            var landingDepthMm = item.Value<double?>("landingDepth");

            var type = ResolveStairsType(doc, item);
            var risers = item.Value<int?>("riserCount") ?? (int)Math.Ceiling(height / type.MaxRiserHeight - 1e-6);
            if (risers < 2) risers = 2;
            var riserHeight = height / risers;
            if (riserHeight > type.MaxRiserHeight + Tolerance / 10)
                warnings.Add($"Riser height {FoundationModelUtils.ToMm(riserHeight)} mm exceeds the type maximum {FoundationModelUtils.ToMm(type.MaxRiserHeight)} mm.");
            var tread = item.Value<double?>("treadDepth") is double t ? FoundationModelUtils.ToFeet(t) : type.MinTreadDepth;
            if (tread < type.MinTreadDepth - Tolerance / 10)
                warnings.Add($"Tread depth {FoundationModelUtils.ToMm(tread)} mm is below the type minimum {FoundationModelUtils.ToMm(type.MinTreadDepth)} mm.");
            if (flights == 2 && risers < 4)
                throw new ArgumentException("A two-flight stair needs at least 4 risers.");

            var plan = new[] { risers };
            if (flights == 2)
                plan = new[] { (risers + 1) / 2, risers - (risers + 1) / 2 };

            // ---- edit scope (must start outside any transaction)
            if (doc.IsModifiable)
                throw new InvalidOperationException("A transaction is already open; stairs need their own edit scope.");

            var failures = new FailureCollector();
            ElementId stairsId;
            var runs = new List<StairsRun>();
            var landingIds = new List<ElementId>();
            using (var scope = new StairsEditScope(doc, "MCP: Create Stairs"))
            {
                stairsId = scope.Start(baseLevel.Id, topLevel.Id);
                try
                {
                    using (var transaction = new Transaction(doc, "MCP: Stairs runs"))
                    {
                        var options = transaction.GetFailureHandlingOptions();
                        options.SetFailuresPreprocessor(failures);
                        options.SetClearAfterRollback(true);
                        transaction.SetFailureHandlingOptions(options);
                        transaction.Start();

                        var stairs = doc.GetElement(stairsId) as Stairs
                                     ?? throw new InvalidOperationException("Revit did not create the stairs.");
                        if (stairs.GetTypeId() != type.Id)
                            stairs.ChangeTypeId(type.Id);
                        SetLength(stairs, BuiltInParameter.STAIRS_BASE_OFFSET, baseOffset, "base offset", warnings);
                        SetLength(stairs, BuiltInParameter.STAIRS_TOP_OFFSET, topOffset, "top offset", warnings);
                        doc.Regenerate();
                        stairs.DesiredRisersNumber = risers;
                        stairs.ActualTreadDepth = tread;
                        doc.Regenerate();

                        // run 1 from start along direction
                        var lateral = side == "left"
                            ? new XYZ(-direction.Y, direction.X, 0)
                            : new XYZ(direction.Y, -direction.X, 0);
                        var run1 = CreateRun(doc, stairsId, start, direction, plan[0], tread, width, bottom, 0, riserHeight, warnings, "run 1");
                        runs.Add(run1);
                        if (flights == 2)
                        {
                            // run 1 ends at its last riser (path length = (risers - 1) treads)
                            var end1 = start + direction * ((plan[0] - 1) * tread);
                            CheckRunEnd(run1, end1, warnings);
                            var start2 = end1 + lateral * (width + gap);
                            var landingHeight = plan[0] * riserHeight;
                            var run2 = CreateRun(doc, stairsId, start2, direction.Negate(), plan[1], tread, width, bottom,
                                landingHeight, riserHeight, warnings, "run 2");
                            runs.Add(run2);

                            if (landingDepthMm.HasValue)
                            {
                                landingIds.Add(CreateLanding(doc, stairsId, end1, direction, lateral, width, gap,
                                    FoundationModelUtils.ToFeet(landingDepthMm.Value), bottom, landingHeight));
                            }
                            else if (StairsLanding.CanCreateAutomaticLanding(doc, run1.Id, run2.Id))
                            {
                                landingIds.AddRange(StairsLanding.CreateAutomaticLanding(doc, run1.Id, run2.Id));
                            }
                            else
                            {
                                warnings.Add("Revit cannot create an automatic landing between the runs; sketched one of depth = width.");
                                landingIds.Add(CreateLanding(doc, stairsId, end1, direction, lateral, width, gap, width, bottom,
                                    landingHeight));
                            }
                        }

                        FoundationModelUtils.SetMark(ctx, stairs, item, warnings);
                        var status = transaction.Commit();
                        if (status != TransactionStatus.Committed)
                            throw new InvalidOperationException($"Stairs runs were not committed ({status}). {failures.ErrorText}");
                    }

                    scope.Commit(failures);
                }
                catch
                {
                    if (scope.IsActive) scope.Cancel();
                    throw;
                }
            }

            var created = doc.GetElement(stairsId) as Stairs;
            if (created == null || !created.IsValidObject)
                throw new InvalidOperationException($"Revit rolled the stairs back. {failures.ErrorText}".Trim());
            warnings.AddRange(failures.Warnings.Distinct());

            var result = new JObject
            {
                ["id"] = created.Id.GetValue(),
                ["typeId"] = created.GetTypeId().GetValue(),
                ["typeName"] = doc.GetElement(created.GetTypeId())?.Name,
                ["baseLevelName"] = baseLevel.Name,
                ["topLevelName"] = topLevel.Name,
                ["heightMm"] = FoundationModelUtils.ToMm(created.Height),
                ["riserCount"] = created.ActualRisersNumber,
                ["desiredRiserCount"] = created.DesiredRisersNumber,
                ["actualRiserHeightMm"] = FoundationModelUtils.ToMm(created.ActualRiserHeight),
                ["treadDepthMm"] = FoundationModelUtils.ToMm(created.ActualTreadDepth),
                ["treadCount"] = created.ActualTreadsNumber,
                ["runIds"] = new JArray(created.GetStairsRuns().Select(id => id.GetValue())),
                ["runRisers"] = new JArray(created.GetStairsRuns()
                    .Select(id => doc.GetElement(id) as StairsRun)
                    .Where(r => r != null)
                    .Select(r => r.ActualRisersNumber)),
                ["landingIds"] = new JArray(created.GetStairsLandings().Select(id => id.GetValue()))
            };
            if (created.ActualRisersNumber != risers)
                warnings.Add($"Stairs have {created.ActualRisersNumber} risers, {risers} requested.");
            FoundationModelUtils.AddWarnings(result, warnings);
            return result;
        }

        private static XYZ ReadDirection(JObject item)
        {
            if (item["direction"] is JObject d && d.HasValues)
            {
                var v = FoundationModelUtils.ReadXY(d, 0, "direction");
                if (v.GetLength() < 1e-9) throw new ArgumentException("'direction' must be a non-zero vector.");
                return v.Normalize();
            }
            var angle = item.Value<double?>("angleDeg") ?? throw new ArgumentException("Give direction or angleDeg.");
            var radians = angle * Math.PI / 180;
            return new XYZ(Math.Cos(radians), Math.Sin(radians), 0);
        }

        private static StairsType ResolveStairsType(Document doc, JObject item)
        {
            var typeId = DocumentationUtils.ReadId(item, "stairsTypeId");
            if (typeId != null)
                return doc.GetElement(typeId.Value.ToRevitElementId()) as StairsType
                       ?? throw new ArgumentException($"stairsTypeId {typeId} is not a stairs type.");

            var all = new FilteredElementCollector(doc).OfClass(typeof(StairsType)).Cast<StairsType>().ToList();
            var name = item.Value<string>("stairsTypeName");
            if (!string.IsNullOrWhiteSpace(name))
                return all.FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                       ?? throw new ArgumentException(
                           $"Stairs type '{name}' not found. Types: {string.Join(", ", all.Select(t => t.Name))}.");

            return doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.StairsType)) as StairsType
                   ?? all.FirstOrDefault()
                   ?? throw new ArgumentException("The project has no component stairs type.");
        }

        private static void SetLength(Element element, BuiltInParameter builtIn, double feet, string what, List<string> warnings)
        {
            var parameter = element.get_Parameter(builtIn);
            if (parameter != null && !parameter.IsReadOnly)
                parameter.Set(feet);
            else if (Math.Abs(feet) > 1e-9)
                warnings.Add($"Could not set the stairs {what}.");
        }

        /// <summary>
        ///     Straight run of <paramref name="risers" /> risers starting at <paramref name="start" />, its base
        ///     <paramref name="baseHeight" /> above the stairs base. The path length is (risers - 1) treads; when
        ///     Revit rounds to a different count the run's top elevation is set to force it.
        /// </summary>
        private static StairsRun CreateRun(Document doc, ElementId stairsId, XYZ start, XYZ direction, int risers, double tread,
            double width, double stairsBase, double baseHeight, double riserHeight, List<string> warnings, string name)
        {
            var z = stairsBase + baseHeight;
            var from = new XYZ(start.X, start.Y, z);
            var to = from + direction * (Math.Max(risers - 1, 1) * tread);
            var run = StairsRun.CreateStraightRun(doc, stairsId, Line.CreateBound(from, to), StairsRunJustification.Center);
            try
            {
                run.ActualRunWidth = width;
            }
            catch (Exception ex)
            {
                warnings.Add($"{name}: could not set the width ({ex.Message}).");
            }

            if (run.ActualRisersNumber != risers)
            {
                try
                {
                    run.TopElevation = baseHeight + risers * riserHeight;
                }
                catch (Exception ex)
                {
                    warnings.Add($"{name}: Revit made {run.ActualRisersNumber} risers instead of {risers} ({ex.Message}).");
                }
                doc.Regenerate();
                if (run.ActualRisersNumber != risers)
                    warnings.Add($"{name}: has {run.ActualRisersNumber} risers, {risers} planned.");
            }
            return run;
        }

        /// <summary>Warns when the run's path does not end where the U-stair layout assumed.</summary>
        private static void CheckRunEnd(StairsRun run, XYZ expected, List<string> warnings)
        {
            try
            {
                var path = run.GetStairsPath()?.Cast<Curve>().ToList();
                if (path == null || path.Count == 0) return;
                var end = path[path.Count - 1].GetEndPoint(1);
                var distance = new XYZ(end.X - expected.X, end.Y - expected.Y, 0).GetLength();
                if (distance > 5 * Tolerance)
                    warnings.Add($"run 1 path ends {FoundationModelUtils.ToMm(distance)} mm from the planned landing edge; check the landing.");
            }
            catch (Exception)
            {
                // diagnostics only
            }
        }

        /// <summary>Rectangular landing from the end of run 1 across both runs, <paramref name="depth" /> deep.</summary>
        private static ElementId CreateLanding(Document doc, ElementId stairsId, XYZ edge, XYZ direction, XYZ lateral,
            double width, double gap, double depth, double stairsBase, double landingHeight)
        {
            var a = new XYZ(edge.X, edge.Y, stairsBase) - lateral * (width / 2);
            var b = a + lateral * (2 * width + gap);
            var c = b + direction * depth;
            var d = a + direction * depth;
            var loop = new CurveLoop();
            loop.Append(Line.CreateBound(a, b));
            loop.Append(Line.CreateBound(b, c));
            loop.Append(Line.CreateBound(c, d));
            loop.Append(Line.CreateBound(d, a));
            return StairsLanding.CreateSketchedLanding(doc, stairsId, loop, landingHeight).Id;
        }

        /// <summary>Deletes warnings (keeping their text for the result) and rolls back on errors.</summary>
        private class FailureCollector : IFailuresPreprocessor
        {
            public List<string> Warnings { get; } = new List<string>();
            public List<string> Errors { get; } = new List<string>();
            public string ErrorText => Errors.Count == 0 ? "" : "Revit errors: " + string.Join("; ", Errors.Distinct());

            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                var rollback = false;
                foreach (var failure in failuresAccessor.GetFailureMessages())
                {
                    var text = failure.GetDescriptionText();
                    if (failure.GetSeverity() == FailureSeverity.Warning)
                    {
                        Warnings.Add(text);
                        failuresAccessor.DeleteWarning(failure);
                    }
                    else
                    {
                        Errors.Add(text);
                        rollback = true;
                    }
                }
                return rollback ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
            }
        }
    }
}
