using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Modify
{
    /// <summary>
    ///     copy_to_levels: "Paste Aligned > Selected Levels" for the API. The selected
    ///     elements of the source level are copied to each target level with one
    ///     ElementTransformUtils.CopyElements call (translation = elevation difference),
    ///     then every copy is re-hosted on the target level with the source offsets,
    ///     and its bottom elevation is checked (and corrected) against the source.
    ///     One transaction per target level.
    /// </summary>
    public class CopyToLevelsEventHandler : JsonParameterEventHandler
    {
        private const double Mm = 304.8;
        private const double Tolerance = 1.0 / Mm;
        private const int MaxWarnings = 20;

        public override string GetName() => "Copy To Levels";

        private enum Kind
        {
            Beam,
            Floor,
            Wall,
            Column,
            LevelFamily,
            Other
        }

        /// <summary>Snapshot of a source element taken before any copy.</summary>
        private class Source
        {
            public Element Element;
            public Kind Kind;
            public string Category;
            public long TypeId;
            public string PlanKey;
            public string MatchKey;
            public double? MinZ;
            public BuiltInParameter LevelParam;
            public readonly Dictionary<BuiltInParameter, double> Offsets = new Dictionary<BuiltInParameter, double>();
            public int TopLevelIndex = -1;
            public double TopOffset;
            public double Height;
        }

        private class Context
        {
            public Document Doc;
            public List<Level> Levels;
            public Level SourceLevel;
            public int SourceIndex;
        }

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var levels = ModelSelectionUtils.SortedLevels(doc);
            if (levels.Count < 2)
                return Fail("The project needs at least two levels.");

            var ctx = new Context { Doc = doc, Levels = levels };
            ctx.SourceLevel = ModelSelectionUtils.ResolveLevel(levels, parameters["sourceLevel"], "sourceLevel");
            ctx.SourceIndex = levels.FindIndex(l => l.Id == ctx.SourceLevel.Id);

            var warnings = new List<string>();
            var targets = ResolveTargets(ctx, parameters["targetLevels"], warnings);
            if (targets.Count == 0)
                return Fail("No target levels (the source level is never a target).");

            var skipExisting = parameters.Value<bool?>("skipExisting") ?? true;
            var returnIds = parameters.Value<bool?>("returnIds") ?? false;

            // Selection: candidates that belong to the source level and can be copied on their own.
            var candidates = ModelSelectionUtils.Collect(doc, parameters, levels, warnings, useLevels: false);
            int notOnLevel = 0, inGroups = 0, nested = 0, stacked = 0;
            var selected = new List<Element>();
            foreach (var element in candidates)
            {
                if (ModelSelectionUtils.GetLevelId(element) != ctx.SourceLevel.Id) { notOnLevel++; continue; }
                if (element.GroupId != null && element.GroupId != ElementId.InvalidElementId) { inGroups++; continue; }
                if (element is FamilyInstance fi && fi.SuperComponent != null) { nested++; continue; }
                if (element is Wall wall && wall.IsStackedWallMember) { stacked++; continue; }
                selected.Add(element);
            }

            // Hosted instances (on a wall, floor face, ...) only copy together with their host.
            var selectedIds = new HashSet<long>(selected.Select(e => e.Id.GetValue()));
            var orphans = selected.Where(e => e is FamilyInstance fi && fi.Host != null && !(fi.Host is Level) &&
                                              !selectedIds.Contains(fi.Host.Id.GetValue())).ToList();
            if (orphans.Count > 0)
            {
                selected = selected.Except(orphans).ToList();
                warnings.Add($"{orphans.Count} hosted instance(s) skipped because their host is not copied (include the host category).");
            }

            if (inGroups > 0) warnings.Add($"{inGroups} element(s) in groups skipped (copy the group instead).");
            if (nested > 0) warnings.Add($"{nested} nested family instance(s) skipped (they follow their parent).");
            if (stacked > 0) warnings.Add($"{stacked} stacked-wall member(s) skipped.");
            if (notOnLevel > 0 && parameters["elementIds"] is JArray ids && ids.Count > 0)
                warnings.Add($"{notOnLevel} given element(s) are not associated with level '{ctx.SourceLevel.Name}' and were skipped.");

            if (selected.Count == 0)
                return Fail($"No matching elements are associated with level '{ctx.SourceLevel.Name}'." +
                            (warnings.Count > 0 ? " " + string.Join(" ", warnings) : ""));

            var sources = selected.Select(e => Snapshot(ctx, e)).ToList();

            // Existing plan keys per level (for skipExisting), over the selected categories.
            Dictionary<long, HashSet<string>> existing = null;
            if (skipExisting)
            {
                var categoryIds = selected.Select(e => e.Category.Id).GroupBy(id => id.GetValue()).Select(g => g.First()).ToList();
                existing = new Dictionary<long, HashSet<string>>();
                foreach (var element in new FilteredElementCollector(doc)
                             .WherePasses(new ElementMulticategoryFilter(categoryIds))
                             .WhereElementIsNotElementType())
                {
                    var levelId = ModelSelectionUtils.GetLevelId(element).GetValue();
                    if (!existing.TryGetValue(levelId, out var keys))
                        existing[levelId] = keys = new HashSet<string>();
                    keys.Add(ExistKey(element));
                }
            }

            var results = new List<JObject>();
            foreach (var target in targets)
            {
                HashSet<string> targetKeys = null;
                existing?.TryGetValue(target.Id.GetValue(), out targetKeys);
                results.Add(CopyToLevel(ctx, sources, target, targetKeys, returnIds));
            }

            var succeeded = results.Count(r => r.Value<bool>("success"));
            var copiedTotal = results.Sum(r => r.Value<int?>("copiedTotal") ?? 0);
            var response = new JObject
            {
                ["sourceLevel"] = new JObject
                {
                    ["id"] = ctx.SourceLevel.Id.GetValue(),
                    ["name"] = ctx.SourceLevel.Name,
                    ["elevation"] = DocumentationUtils.FeetToMm(ctx.SourceLevel.Elevation)
                },
                ["selected"] = ModelSelectionUtils.CountBy(selected, ModelSelectionUtils.CategoryKey),
                ["succeeded"] = succeeded,
                ["failed"] = results.Count - succeeded,
                ["results"] = new JArray(results)
            };
            if (warnings.Count > 0) response["warnings"] = new JArray(warnings.Take(MaxWarnings));

            return Ok($"Copied {copiedTotal} element(s) from '{ctx.SourceLevel.Name}' to {succeeded} of {results.Count} level(s).", response);
        }

        private static List<Level> ResolveTargets(Context ctx, JToken token, List<string> warnings)
        {
            var levels = ctx.Levels;
            var list = new List<Level>();
            if (token is JObject range)
            {
                var from = levels.FindIndex(l => l.Id == ModelSelectionUtils.ResolveLevel(levels, range["from"], "targetLevels.from").Id);
                var to = levels.FindIndex(l => l.Id == ModelSelectionUtils.ResolveLevel(levels, range["to"], "targetLevels.to").Id);
                for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
                    list.Add(levels[i]);
            }
            else if (token is JArray array && array.Count > 0)
            {
                for (var i = 0; i < array.Count; i++)
                    list.Add(ModelSelectionUtils.ResolveLevel(levels, array[i], $"targetLevels[{i}]"));
            }
            else
            {
                throw new ArgumentException("'targetLevels' must be a non-empty array or {from, to}.");
            }

            var result = new List<Level>();
            foreach (var level in list)
            {
                if (level.Id == ctx.SourceLevel.Id)
                {
                    if (token is JArray) warnings.Add($"Target '{level.Name}' is the source level; skipped.");
                    continue;
                }
                if (result.All(l => l.Id != level.Id)) result.Add(level);
            }
            return result;
        }

        // ---------------------------------------------------------------- snapshot

        private static Source Snapshot(Context ctx, Element element)
        {
            var source = new Source
            {
                Element = element,
                Category = ModelSelectionUtils.CategoryKey(element),
                TypeId = element.GetTypeId()?.GetValue() ?? -1,
                PlanKey = PlanKey(element, 0, false),
                MatchKey = MatchKey(element, 0),
                MinZ = element.get_BoundingBox(null)?.Min.Z
            };

            if (element is Wall)
            {
                source.Kind = Kind.Wall;
                ReadOffset(source, BuiltInParameter.WALL_BASE_OFFSET);
                var top = element.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId();
                if (top != null && top != ElementId.InvalidElementId)
                {
                    source.TopLevelIndex = ctx.Levels.FindIndex(l => l.Id == top);
                    source.TopOffset = element.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.AsDouble() ?? 0;
                }
                source.Height = element.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0;
            }
            else if (element is Floor)
            {
                source.Kind = Kind.Floor;
                ReadOffset(source, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
            }
            else if (element is FamilyInstance)
            {
                if (DocumentationUtils.IsCategory(element, BuiltInCategory.OST_StructuralFraming) &&
                    element.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM) != null &&
                    element.get_Parameter(BuiltInParameter.STRUCTURAL_BEAM_END0_ELEVATION) != null)
                {
                    source.Kind = Kind.Beam;
                    ReadOffset(source, BuiltInParameter.STRUCTURAL_BEAM_END0_ELEVATION);
                    ReadOffset(source, BuiltInParameter.STRUCTURAL_BEAM_END1_ELEVATION);
                }
                else if (IsLevelParam(element, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM) &&
                         IsLevelParam(element, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM))
                {
                    source.Kind = Kind.Column;
                    ReadOffset(source, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM);
                    var top = element.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM).AsElementId();
                    source.TopLevelIndex = ctx.Levels.FindIndex(l => l.Id == top);
                    source.TopOffset = element.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.AsDouble() ?? 0;
                }
                else
                {
                    foreach (var bip in new[]
                             {
                                 BuiltInParameter.FAMILY_LEVEL_PARAM,
                                 BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,
                                 BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,
                                 BuiltInParameter.LEVEL_PARAM,
                                 BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM,
                                 BuiltInParameter.SCHEDULE_LEVEL_PARAM
                             })
                    {
                        var parameter = element.get_Parameter(bip);
                        if (parameter != null && !parameter.IsReadOnly && parameter.StorageType == StorageType.ElementId)
                        {
                            source.Kind = Kind.LevelFamily;
                            source.LevelParam = bip;
                            break;
                        }
                    }

                    if (source.Kind != Kind.LevelFamily)
                        source.Kind = Kind.Other;
                    ReadOffset(source, BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
                    ReadOffset(source, BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                    ReadOffset(source, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM);
                }
            }
            else
            {
                source.Kind = Kind.Other;
            }

            return source;
        }

        private static bool IsLevelParam(Element element, BuiltInParameter bip)
        {
            var parameter = element.get_Parameter(bip);
            return parameter != null && parameter.StorageType == StorageType.ElementId &&
                   parameter.AsElementId() != ElementId.InvalidElementId;
        }

        private static void ReadOffset(Source source, BuiltInParameter bip)
        {
            var parameter = source.Element.get_Parameter(bip);
            if (parameter != null && parameter.StorageType == StorageType.Double && !parameter.IsReadOnly)
                source.Offsets[bip] = parameter.AsDouble();
        }

        // ---------------------------------------------------------------- keys

        private static long R(double feet) => (long)Math.Round(feet * Mm);

        /// <summary>
        ///     Plan location key (mm): location curve end points (order independent, plus the
        ///     mid point for arcs), location point, else bounding-box XY. With Z when requested.
        /// </summary>
        private static string PlanKey(Element element, double zShift, bool withZ)
        {
            switch (element.Location)
            {
                case LocationCurve lc when lc.Curve != null && lc.Curve.IsBound:
                {
                    var a = lc.Curve.GetEndPoint(0);
                    var b = lc.Curve.GetEndPoint(1);
                    if (R(a.X) > R(b.X) || (R(a.X) == R(b.X) && R(a.Y) > R(b.Y)))
                    {
                        var t = a;
                        a = b;
                        b = t;
                    }
                    var key = $"C{R(a.X)},{R(a.Y)},{R(b.X)},{R(b.Y)}";
                    if (!(lc.Curve is Line))
                    {
                        var m = lc.Curve.Evaluate(0.5, true);
                        key += $",{R(m.X)},{R(m.Y)}";
                    }
                    return withZ ? key + $"|{R(Math.Min(a.Z, b.Z) - zShift)}" : key;
                }
                case LocationPoint lp:
                {
                    var p = lp.Point;
                    var key = $"P{R(p.X)},{R(p.Y)}";
                    return withZ ? key + $"|{R(p.Z - zShift)}" : key;
                }
                default:
                {
                    var box = element.get_BoundingBox(null);
                    if (box == null) return "N" + element.Id.GetValue();
                    var key = $"B{R(box.Min.X)},{R(box.Min.Y)},{R(box.Max.X)},{R(box.Max.Y)}";
                    return withZ ? key + $"|{R(box.Min.Z - zShift)}" : key;
                }
            }
        }

        private static string BaseKey(Element element) =>
            ModelSelectionUtils.CategoryKey(element) + "|" + (element.GetTypeId()?.GetValue() ?? -1);

        private static string ExistKey(Element element) => BaseKey(element) + "|" + PlanKey(element, 0, false);

        private static string MatchKey(Element element, double zShift) => BaseKey(element) + "|" + PlanKey(element, zShift, true);

        // ---------------------------------------------------------------- copy

        private static JObject CopyToLevel(Context ctx, List<Source> sources, Level target, HashSet<string> targetKeys, bool returnIds)
        {
            var doc = ctx.Doc;
            var delta = target.Elevation - ctx.SourceLevel.Elevation;
            var indexDelta = ctx.Levels.FindIndex(l => l.Id == target.Id) - ctx.SourceIndex;
            var warnings = new List<string>();
            var result = new JObject
            {
                ["level"] = target.Name,
                ["elevation"] = DocumentationUtils.FeetToMm(target.Elevation)
            };

            var toCopy = new List<Source>();
            var skipped = 0;
            foreach (var source in sources)
            {
                if (targetKeys != null && targetKeys.Contains(source.Category + "|" + source.TypeId + "|" + source.PlanKey))
                    skipped++;
                else
                    toCopy.Add(source);
            }

            if (toCopy.Count == 0)
            {
                result["success"] = true;
                result["copiedTotal"] = 0;
                result["copied"] = new JObject();
                result["skippedExisting"] = skipped;
                return result;
            }

            int notReleveled = 0, corrected = 0, extra = 0;
            var copiedIds = new List<ElementId>();
            var copied = new List<Element>();
            using (var transaction = ModelSelectionUtils.StartTransaction(doc, $"MCP: Copy to level {target.Name}", warnings))
            {
                try
                {
                    var newIds = ElementTransformUtils.CopyElements(doc, toCopy.Select(s => s.Element.Id).ToList(), new XYZ(0, 0, delta));
                    doc.Regenerate();

                    // Match each copy to its source by category + type + plan location + Z (shifted back).
                    var exact = new Dictionary<string, Queue<Source>>();
                    var loose = new Dictionary<string, Queue<Source>>();
                    foreach (var source in toCopy)
                    {
                        Enqueue(exact, source.MatchKey, source);
                        Enqueue(loose, source.Category + "|" + source.TypeId + "|" + source.PlanKey, source);
                    }

                    var pairs = new List<KeyValuePair<Element, Source>>();
                    var used = new HashSet<Source>();
                    foreach (var id in newIds)
                    {
                        var copy = doc.GetElement(id);
                        if (copy == null) continue;
                        var source = Dequeue(exact, MatchKey(copy, delta), used) ?? Dequeue(loose, ExistKey(copy), used);
                        if (source == null)
                        {
                            extra++;
                            continue;
                        }
                        used.Add(source);
                        pairs.Add(new KeyValuePair<Element, Source>(copy, source));
                        copiedIds.Add(id);
                        copied.Add(copy);
                    }

                    foreach (var pair in pairs)
                    {
                        try
                        {
                            if (!Relevel(ctx, pair.Key, pair.Value, target, indexDelta, warnings))
                                notReleveled++;
                        }
                        catch (Exception ex)
                        {
                            notReleveled++;
                            warnings.Add($"Copy of {pair.Value.Element.Id.GetValue()} ({pair.Value.Category}) not re-hosted: {ex.Message}");
                        }
                    }

                    doc.Regenerate();

                    // Verify the bottom elevation against the source and correct any double shift.
                    foreach (var pair in pairs)
                    {
                        var source = pair.Value;
                        if (source.MinZ == null || !pair.Key.IsValidObject) continue;
                        var box = pair.Key.get_BoundingBox(null);
                        if (box == null) continue;
                        var diff = source.MinZ.Value + delta - box.Min.Z;
                        if (Math.Abs(diff) <= Tolerance) continue;
                        try
                        {
                            ElementTransformUtils.MoveElement(doc, pair.Key.Id, new XYZ(0, 0, diff));
                            corrected++;
                            warnings.Add($"Copy {pair.Key.Id.GetValue()} of {source.Element.Id.GetValue()} ({source.Category}) was off by {Math.Round(-diff * Mm, 1)} mm; moved back.");
                        }
                        catch (Exception ex)
                        {
                            warnings.Add($"Copy {pair.Key.Id.GetValue()} ({source.Category}) is off by {Math.Round(-diff * Mm, 1)} mm and could not be moved: {ex.Message}");
                        }
                    }

                    var status = transaction.Commit();
                    if (status != TransactionStatus.Committed)
                    {
                        result["success"] = false;
                        result["message"] = $"Transaction was not committed ({status}).";
                        AddWarnings(result, warnings);
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    if (transaction.HasStarted() && !transaction.HasEnded())
                        transaction.RollBack();
                    result["success"] = false;
                    result["message"] = ex.Message;
                    AddWarnings(result, warnings);
                    return result;
                }
            }

            result["success"] = true;
            result["copiedTotal"] = copied.Count;
            result["copied"] = ModelSelectionUtils.CountBy(copied, ModelSelectionUtils.CategoryKey);
            result["skippedExisting"] = skipped;
            if (notReleveled > 0) result["notReleveled"] = notReleveled;
            if (corrected > 0) result["corrected"] = corrected;
            if (extra > 0) result["extraCopies"] = extra;
            if (returnIds) result["ids"] = new JArray(copiedIds.Select(id => id.GetValue()));
            AddWarnings(result, warnings);
            return result;
        }

        private static void Enqueue(Dictionary<string, Queue<Source>> map, string key, Source source)
        {
            if (!map.TryGetValue(key, out var queue))
                map[key] = queue = new Queue<Source>();
            queue.Enqueue(source);
        }

        private static Source Dequeue(Dictionary<string, Queue<Source>> map, string key, HashSet<Source> used)
        {
            if (!map.TryGetValue(key, out var queue)) return null;
            while (queue.Count > 0)
            {
                var source = queue.Dequeue();
                if (!used.Contains(source)) return source;
            }
            return null;
        }

        private static void AddWarnings(JObject result, List<string> warnings)
        {
            if (warnings.Count == 0) return;
            result["warnings"] = new JArray(warnings.Take(MaxWarnings));
            if (warnings.Count > MaxWarnings) result["moreWarnings"] = warnings.Count - MaxWarnings;
        }

        // ---------------------------------------------------------------- re-hosting

        /// <summary>Associates the copy with the target level and restores the source offsets. False when not possible.</summary>
        private static bool Relevel(Context ctx, Element copy, Source source, Level target, int indexDelta, List<string> warnings)
        {
            switch (source.Kind)
            {
                case Kind.Beam:
                    SetLevel(copy, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM, target.Id);
                    RestoreOffsets(copy, source);
                    return true;

                case Kind.Floor:
                    SetLevel(copy, BuiltInParameter.LEVEL_PARAM, target.Id);
                    RestoreOffsets(copy, source);
                    return true;

                case Kind.Column:
                    RelevelColumn(ctx, copy, source, target, indexDelta, warnings);
                    return true;

                case Kind.Wall:
                    RelevelWall(ctx, copy, source, target, indexDelta);
                    return true;

                case Kind.LevelFamily:
                    SetLevel(copy, source.LevelParam, target.Id);
                    // Schedule-only levels do not move the element; its offsets stay relative to the host.
                    if (source.LevelParam != BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM &&
                        source.LevelParam != BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                        RestoreOffsets(copy, source);
                    return true;

                default:
                    return false;
            }
        }

        private static void SetLevel(Element element, BuiltInParameter bip, ElementId levelId)
        {
            var parameter = element.get_Parameter(bip)
                            ?? throw new InvalidOperationException($"{bip} not found.");
            if (parameter.AsElementId() == levelId) return;
            if (parameter.IsReadOnly)
                throw new InvalidOperationException($"{bip} is read-only.");
            if (!parameter.Set(levelId))
                throw new InvalidOperationException($"Could not set {bip}.");
        }

        private static void SetDouble(Element element, BuiltInParameter bip, double value)
        {
            var parameter = element.get_Parameter(bip);
            if (parameter == null || parameter.IsReadOnly) return;
            if (Math.Abs(parameter.AsDouble() - value) < 1e-9) return;
            if (!parameter.Set(value))
                throw new InvalidOperationException($"Could not set {bip}.");
        }

        private static void RestoreOffsets(Element copy, Source source)
        {
            foreach (var offset in source.Offsets)
                SetDouble(copy, offset.Key, offset.Value);
        }

        private static void RelevelColumn(Context ctx, Element copy, Source source, Level target, int indexDelta, List<string> warnings)
        {
            var levels = ctx.Levels;
            Level top;
            double topOffset;
            var topIndex = source.TopLevelIndex < 0 ? -1 : source.TopLevelIndex + indexDelta;
            var baseOffset = source.Offsets.TryGetValue(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM, out var b) ? b : 0;
            if (topIndex >= 0 && topIndex < levels.Count &&
                levels[topIndex].Elevation + source.TopOffset > target.Elevation + baseOffset + Tolerance)
            {
                top = levels[topIndex];
                topOffset = source.TopOffset;
            }
            else
            {
                // Keep the absolute top: highest level at or below it plus the remainder.
                var sourceTop = source.TopLevelIndex >= 0
                    ? levels[source.TopLevelIndex].Elevation + source.TopOffset
                    : ctx.SourceLevel.Elevation + baseOffset;
                var absTop = sourceTop + (target.Elevation - ctx.SourceLevel.Elevation);
                top = levels.LastOrDefault(l => l.Elevation <= absTop + Tolerance) ?? target;
                topOffset = absTop - top.Elevation;
                warnings.Add($"Column copy {copy.Id.GetValue()}: no level {Math.Abs(indexDelta)} above its top level; top set to '{top.Name}' {Math.Round(topOffset * Mm)} mm.");
            }

            // Order so the top never ends up below the base while editing.
            if (indexDelta > 0)
            {
                SetLevel(copy, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, top.Id);
                SetDouble(copy, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM, topOffset);
                SetLevel(copy, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, target.Id);
                SetDouble(copy, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM, baseOffset);
            }
            else
            {
                SetLevel(copy, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, target.Id);
                SetDouble(copy, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM, baseOffset);
                SetLevel(copy, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, top.Id);
                SetDouble(copy, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM, topOffset);
            }
        }

        private static void RelevelWall(Context ctx, Element copy, Source source, Level target, int indexDelta)
        {
            var levels = ctx.Levels;
            var baseOffset = source.Offsets.TryGetValue(BuiltInParameter.WALL_BASE_OFFSET, out var b) ? b : 0;
            var topIndex = source.TopLevelIndex < 0 ? -1 : source.TopLevelIndex + indexDelta;
            var connected = topIndex >= 0 && topIndex < levels.Count &&
                            levels[topIndex].Elevation + source.TopOffset > target.Elevation + baseOffset + Tolerance;

            void SetBase()
            {
                SetLevel(copy, BuiltInParameter.WALL_BASE_CONSTRAINT, target.Id);
                SetDouble(copy, BuiltInParameter.WALL_BASE_OFFSET, baseOffset);
            }

            if (connected)
            {
                void SetTop()
                {
                    SetLevel(copy, BuiltInParameter.WALL_HEIGHT_TYPE, levels[topIndex].Id);
                    SetDouble(copy, BuiltInParameter.WALL_TOP_OFFSET, source.TopOffset);
                }

                if (indexDelta > 0)
                {
                    SetTop();
                    SetBase();
                }
                else
                {
                    SetBase();
                    SetTop();
                }
            }
            else
            {
                // Unconnected with the source height (also when the shifted top level does not exist).
                var height = copy.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
                if (height != null && height.AsElementId() != ElementId.InvalidElementId)
                {
                    if (height.IsReadOnly || !height.Set(ElementId.InvalidElementId))
                        throw new InvalidOperationException("Could not make the wall unconnected.");
                }
                SetBase();
                if (source.Height > Tolerance)
                    SetDouble(copy, BuiltInParameter.WALL_USER_HEIGHT_PARAM, source.Height);
            }
        }
    }
}
