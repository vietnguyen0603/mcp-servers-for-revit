using System.Diagnostics;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    /// <summary>
    ///     check_model: read-only structural model QA. Sections: counts,
    ///     elevations, overlaps (solid intersection volumes after a bounding-box
    ///     sweep), unsupported beams/columns and levels without a floor.
    ///     No transaction is opened.
    /// </summary>
    public class CheckModelEventHandler : JsonParameterEventHandler
    {
        private static readonly string[] AllChecks = { "counts", "elevations", "overlaps", "unsupported", "levelsWithoutFloor" };

        public override string GetName() => "Check Model";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var watch = Stopwatch.StartNew();

            var checks = parameters["checks"] is JArray checkArray && checkArray.Count > 0
                ? checkArray.Select(t => t.ToString()).ToList()
                : AllChecks.ToList();
            foreach (var check in checks)
                if (!AllChecks.Contains(check, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException($"Unknown check '{check}'. Use: {string.Join(", ", AllChecks)}.");
            bool Has(string name) => checks.Contains(name, StringComparer.OrdinalIgnoreCase);

            var categories = parameters["categories"] is JArray catArray && catArray.Count > 0
                ? catArray.Select(t => ModelQaUtils.ParseCategory(t.ToString())).Distinct().ToList()
                : ModelQaUtils.DefaultCategories.ToList();
            var levels = ModelQaUtils.ReadNameSet(parameters, "levels");
            var byType = parameters.Value<bool?>("byType") ?? false;
            var maxItems = Math.Max(1, parameters.Value<int?>("maxItems") ?? 200);
            var outFile = parameters.Value<string>("outFile");
            var capLists = string.IsNullOrWhiteSpace(outFile);

            var ctx = new ModelQaUtils.Context(doc);
            if (levels != null)
            {
                var missing = levels.Where(n => ctx.Levels.All(l => !string.Equals(l.Name, n, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (missing.Count == levels.Count)
                    return Fail($"None of the levels exist: {string.Join(", ", missing)}. Levels: {string.Join(", ", ctx.Levels.Select(l => l.Name))}.");
            }

            bool InScope(ModelQaUtils.ElementBox box) => levels == null || levels.Contains(box.LevelName);

            var full = new JObject();
            var summary = new JObject();

            if (Has("counts"))
                full["counts"] = Counts(ctx, categories, InScope, byType, summary);
            if (Has("elevations"))
                full["elevations"] = Elevations(ctx, categories, InScope, summary);
            if (Has("overlaps"))
                full["overlaps"] = Overlaps(ctx, parameters, categories, InScope, summary);
            if (Has("unsupported"))
                full["unsupported"] = Unsupported(ctx, parameters, InScope, summary);
            if (Has("levelsWithoutFloor"))
                full["levelsWithoutFloor"] = LevelsWithoutFloor(ctx, levels, summary);

            summary["elapsedSeconds"] = Math.Round(watch.Elapsed.TotalSeconds, 1);
            full["summary"] = summary;

            if (!capLists)
            {
                ModelQaUtils.WriteJson(outFile, full);
                return Ok($"check_model: full report written to {outFile}.", new JObject
                {
                    ["outFile"] = outFile,
                    ["summary"] = summary
                });
            }

            foreach (var section in new[] { "overlaps", "unsupported" })
            {
                if (!(full[section] is JObject obj) || !(obj["items"] is JArray items) || items.Count <= maxItems)
                    continue;
                obj["items"] = new JArray(items.Take(maxItems));
                obj["truncated"] = true;
                obj["listed"] = maxItems;
            }

            return Ok("check_model: " + string.Join("; ", summary.Properties()
                .Where(p => p.Value.Type == JTokenType.Integer).Select(p => $"{p.Name} {p.Value}")), full);
        }

        // ---------------------------------------------------------------- counts

        private static JObject Counts(ModelQaUtils.Context ctx, List<BuiltInCategory> categories,
            Func<ModelQaUtils.ElementBox, bool> inScope, bool byType, JObject summary)
        {
            var result = new JArray();
            var total = 0;
            foreach (var bic in categories)
            {
                var boxes = ctx.Boxes(bic).Where(inScope).ToList();
                total += boxes.Count;
                var byLevel = new JArray();
                foreach (var group in boxes.GroupBy(b => b.LevelName).OrderBy(g => LevelOrder(ctx, g.First().Level)))
                {
                    var row = new JObject { ["level"] = group.Key, ["count"] = group.Count() };
                    if (byType)
                        row["types"] = new JArray(group.GroupBy(b => b.TypeName).OrderBy(g => g.Key)
                            .Select(g => new JObject { ["type"] = g.Key, ["count"] = g.Count() }));
                    byLevel.Add(row);
                }

                var entry = new JObject
                {
                    ["category"] = ModelQaUtils.CategoryLabel(bic),
                    ["count"] = boxes.Count,
                    ["byLevel"] = byLevel
                };
                if (byType)
                    entry["types"] = new JArray(boxes.GroupBy(b => b.TypeName).OrderBy(g => g.Key)
                        .Select(g => new JObject { ["type"] = g.Key, ["count"] = g.Count() }));
                result.Add(entry);
            }

            summary["elements"] = total;
            return new JObject { ["total"] = total, ["categories"] = result };
        }

        private static double LevelOrder(ModelQaUtils.Context ctx, Level level) =>
            level == null ? double.MaxValue : level.Elevation;

        // ------------------------------------------------------------ elevations

        private static JObject Elevations(ModelQaUtils.Context ctx, List<BuiltInCategory> categories,
            Func<ModelQaUtils.ElementBox, bool> inScope, JObject summary)
        {
            var rows = new JArray();
            foreach (var bic in categories)
            {
                var groups = ctx.Boxes(bic).Where(inScope)
                    .GroupBy(b => new { b.TypeName, b.LevelName })
                    .OrderBy(g => g.Key.TypeName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(g => LevelOrder(ctx, g.First().Level));
                foreach (var group in groups)
                {
                    var level = group.First().Level;
                    var row = new JObject
                    {
                        ["category"] = ModelQaUtils.CategoryLabel(bic),
                        ["type"] = group.Key.TypeName,
                        ["level"] = group.Key.LevelName,
                        ["levelElevationMm"] = level == null ? null : (JToken)ModelQaUtils.ToMm(level.Elevation),
                        ["count"] = group.Count(),
                        ["bottomMm"] = Range(group.Select(b => b.Min.Z)),
                        ["topMm"] = Range(group.Select(b => b.Max.Z))
                    };
                    if (level != null)
                    {
                        row["bottomRelMm"] = Range(group.Select(b => b.Min.Z - level.Elevation));
                        row["topRelMm"] = Range(group.Select(b => b.Max.Z - level.Elevation));
                    }

                    rows.Add(row);
                }
            }

            summary["elevationRows"] = rows.Count;
            return new JObject
            {
                ["note"] = "Bounding-box bottom/top elevations in mm; *RelMm are relative to the element's level.",
                ["rows"] = rows
            };
        }

        private static JObject Range(IEnumerable<double> feet)
        {
            var list = feet.ToList();
            return new JObject { ["min"] = ModelQaUtils.ToMm(list.Min()), ["max"] = ModelQaUtils.ToMm(list.Max()) };
        }

        // -------------------------------------------------------------- overlaps

        private static JObject Overlaps(ModelQaUtils.Context ctx, JObject parameters, List<BuiltInCategory> categories,
            Func<ModelQaUtils.ElementBox, bool> inScope, JObject summary)
        {
            var sameCategory = parameters.Value<bool?>("sameCategoryOverlaps") ?? true;
            var skipJoined = parameters.Value<bool?>("skipJoined") ?? true;
            var minVolumeFt3 = (parameters.Value<double?>("minOverlapVolumeM3") ?? 0.01) / ModelQaUtils.CubicMetresPerCubicFoot;

            var groups = new List<KeyValuePair<BuiltInCategory, BuiltInCategory>>();
            if (sameCategory)
                groups.AddRange(categories.Select(c => new KeyValuePair<BuiltInCategory, BuiltInCategory>(c, c)));
            if (parameters["clashPairs"] is JArray clashPairs)
            {
                foreach (var token in clashPairs.OfType<JObject>())
                {
                    var a = ModelQaUtils.ParseCategory(token.Value<string>("a"));
                    var b = ModelQaUtils.ParseCategory(token.Value<string>("b"));
                    if (!groups.Any(g => (g.Key == a && g.Value == b) || (g.Key == b && g.Value == a)))
                        groups.Add(new KeyValuePair<BuiltInCategory, BuiltInCategory>(a, b));
                }
            }

            var items = new List<JObject>();
            var groupStats = new JArray();
            var booleanFailuresBefore = ctx.BooleanFailures;
            // Require a real overlap of at least 1 mm in every axis (touching elements are fine).
            var overlapTol = -ModelQaUtils.ToFeet(1);
            foreach (var group in groups)
            {
                var same = group.Key == group.Value;
                var listA = ctx.Boxes(group.Key);
                var listB = same ? listA : ctx.Boxes(group.Value);
                var candidates = ModelQaUtils.SweepPairs(listA, listB, overlapTol);
                int tested = 0, joinedSkipped = 0, found = 0;
                double volume = 0;
                foreach (var pair in candidates)
                {
                    var a = pair.Key;
                    var b = pair.Value;
                    if (!inScope(a) && !inScope(b))
                        continue;
                    tested++;
                    if (skipJoined)
                    {
                        var joined = false;
                        try
                        {
                            joined = JoinGeometryUtils.AreElementsJoined(ctx.Doc, a.Element, b.Element);
                        }
                        catch (Exception)
                        {
                            // not joinable: treat as not joined
                        }

                        if (joined)
                        {
                            joinedSkipped++;
                            continue;
                        }
                    }

                    var v = ctx.IntersectionFt3(a, b);
                    if (v < minVolumeFt3 || v <= 0)
                        continue;
                    found++;
                    volume += v;
                    var smaller = Math.Min(ctx.VolumeFt3(a), ctx.VolumeFt3(b));
                    var ratio = smaller > 0 ? Math.Min(1.0, v / smaller) : 0;
                    items.Add(new JObject
                    {
                        ["kind"] = same ? "sameCategory" : "clash",
                        ["a"] = a.Info(),
                        ["b"] = b.Info(),
                        ["volumeM3"] = Math.Round(v * ModelQaUtils.CubicMetresPerCubicFoot, 4),
                        ["shareOfSmaller"] = Math.Round(ratio, 3),
                        ["duplicateLike"] = ratio >= 0.9
                    });
                }

                groupStats.Add(new JObject
                {
                    ["a"] = ModelQaUtils.CategoryLabel(group.Key),
                    ["b"] = ModelQaUtils.CategoryLabel(group.Value),
                    ["candidates"] = candidates.Count,
                    ["tested"] = tested,
                    ["skippedJoined"] = joinedSkipped,
                    ["overlapping"] = found,
                    ["volumeM3"] = Math.Round(volume * ModelQaUtils.CubicMetresPerCubicFoot, 3)
                });
            }

            var sorted = items.OrderByDescending(i => i.Value<double>("volumeM3")).ToList();
            summary["overlaps"] = sorted.Count;
            var failures = ctx.BooleanFailures - booleanFailuresBefore;
            return new JObject
            {
                ["total"] = sorted.Count,
                ["minOverlapVolumeM3"] = parameters.Value<double?>("minOverlapVolumeM3") ?? 0.01,
                ["groups"] = groupStats,
                ["booleanFailures"] = failures,
                ["items"] = new JArray(sorted)
            };
        }

        // ----------------------------------------------------------- unsupported

        private static JObject Unsupported(ModelQaUtils.Context ctx, JObject parameters,
            Func<ModelQaUtils.ElementBox, bool> inScope, JObject summary)
        {
            var tol = ModelQaUtils.ToFeet(parameters.Value<double?>("supportToleranceMm") ?? 300);
            var columns = ctx.Boxes(BuiltInCategory.OST_StructuralColumns);
            var walls = ctx.Boxes(BuiltInCategory.OST_Walls);
            var framing = ctx.Boxes(BuiltInCategory.OST_StructuralFraming);
            var foundations = ctx.Boxes(BuiltInCategory.OST_StructuralFoundation);
            var floors = ctx.Boxes(BuiltInCategory.OST_Floors);
            var cell = ModelQaUtils.ToFeet(3000);

            var items = new List<JObject>();
            var widths = new Dictionary<long, double>();

            // Beams: each end near a column, wall, foundation or another beam.
            var beamSupports = new ModelQaUtils.XYGrid(columns.Concat(walls).Concat(framing).Concat(foundations), cell, tol);
            int beamsChecked = 0, beamsUnsupported = 0, endsUnsupported = 0;
            foreach (var beam in framing.Where(inScope))
            {
                if (!(beam.Element.Location is LocationCurve lc) || lc.Curve == null || !lc.Curve.IsBound)
                    continue;
                beamsChecked++;
                var bad = false;
                for (var end = 0; end < 2; end++)
                {
                    var p = lc.Curve.GetEndPoint(end);
                    var supported = beamSupports.Query(p).Any(s => s.Id != beam.Id && SupportsPoint(ctx, s, p, tol, widths));
                    if (supported)
                        continue;
                    bad = true;
                    endsUnsupported++;
                    var item = beam.Info();
                    item["issue"] = end == 0 ? "beamStartUnsupported" : "beamEndUnsupported";
                    item["pointMm"] = PointMm(p);
                    items.Add(item);
                }

                if (bad)
                    beamsUnsupported++;
            }

            // Columns: something under the base (column, wall, beam, foundation or floor).
            var columnSupports = new ModelQaUtils.XYGrid(columns.Concat(walls).Concat(framing).Concat(foundations).Concat(floors), cell, tol);
            int columnsChecked = 0, columnsUnsupported = 0;
            foreach (var column in columns.Where(inScope))
            {
                var p = ColumnBase(column);
                columnsChecked++;
                var supported = columnSupports.Query(p).Any(s =>
                    s.Id != column.Id &&
                    p.X >= s.Min.X - tol && p.X <= s.Max.X + tol &&
                    p.Y >= s.Min.Y - tol && p.Y <= s.Max.Y + tol &&
                    s.Min.Z - tol <= p.Z && s.Max.Z >= p.Z - tol &&
                    (s.Category != BuiltInCategory.OST_Walls || NearWall(s, p, tol)));
                if (supported)
                    continue;
                columnsUnsupported++;
                var item = column.Info();
                item["issue"] = "columnBaseUnsupported";
                item["pointMm"] = PointMm(p);
                items.Add(item);
            }

            summary["unsupportedBeams"] = beamsUnsupported;
            summary["unsupportedColumns"] = columnsUnsupported;
            return new JObject
            {
                ["toleranceMm"] = ModelQaUtils.ToMm(tol),
                ["beamsChecked"] = beamsChecked,
                ["beamsUnsupported"] = beamsUnsupported,
                ["beamEndsUnsupported"] = endsUnsupported,
                ["columnsChecked"] = columnsChecked,
                ["columnsUnsupported"] = columnsUnsupported,
                ["total"] = items.Count,
                ["note"] = "Beam ends: within tolerance of a column/foundation box, a wall (plan distance to its location line minus half its width) or another beam's location line (minus half its width). Column bases: inside the plan box of a column, wall, beam, foundation or floor spanning the base elevation; floor and foundation outlines are approximated by their bounding boxes.",
                ["items"] = new JArray(items)
            };
        }

        private static bool SupportsPoint(ModelQaUtils.Context ctx, ModelQaUtils.ElementBox support, XYZ p, double tol,
            Dictionary<long, double> widths)
        {
            if (!ModelQaUtils.BoxContains(support, p, tol))
                return false;
            switch (support.Category)
            {
                case BuiltInCategory.OST_Walls:
                    return NearWall(support, p, tol);
                case BuiltInCategory.OST_StructuralFraming:
                    if (!(support.Element.Location is LocationCurve lc) || lc.Curve == null)
                        return true;
                    if (!widths.TryGetValue(support.Id, out var width))
                        widths[support.Id] = width = SectionWidth(ctx.Doc, support.Element);
                    return lc.Curve.Distance(p) <= tol + width / 2;
                default:
                    return true;
            }
        }

        /// <summary>Plan distance from the point to the wall's location line within half the wall width + tolerance.</summary>
        private static bool NearWall(ModelQaUtils.ElementBox wall, XYZ p, double tol)
        {
            if (!(wall.Element is Wall w) || !(w.Location is LocationCurve lc) || lc.Curve == null)
                return true;
            var curve = lc.Curve;
            var z = curve.GetEndPoint(0).Z;
            double distance;
            try
            {
                distance = curve.Distance(new XYZ(p.X, p.Y, z));
            }
            catch (Exception)
            {
                return true;
            }

            return distance <= w.Width / 2 + tol;
        }

        private static double SectionWidth(Document doc, Element element)
        {
            var type = doc.GetElement(element.GetTypeId());
            foreach (var name in new[] { "b", "Width", "B", "bf" })
            {
                var parameter = element.LookupParameter(name) ?? type?.LookupParameter(name);
                if (parameter != null && parameter.StorageType == StorageType.Double && parameter.AsDouble() > 0)
                    return parameter.AsDouble();
            }

            return 0;
        }

        private static XYZ ColumnBase(ModelQaUtils.ElementBox column)
        {
            switch (column.Element.Location)
            {
                case LocationPoint lp when lp.Point != null:
                    return new XYZ(lp.Point.X, lp.Point.Y, column.Min.Z);
                case LocationCurve lc when lc.Curve != null && lc.Curve.IsBound:
                    var a = lc.Curve.GetEndPoint(0);
                    var b = lc.Curve.GetEndPoint(1);
                    var low = a.Z <= b.Z ? a : b;
                    return new XYZ(low.X, low.Y, column.Min.Z);
                default:
                    return new XYZ((column.Min.X + column.Max.X) / 2, (column.Min.Y + column.Max.Y) / 2, column.Min.Z);
            }
        }

        private static JObject PointMm(XYZ p) => new JObject
        {
            ["x"] = ModelQaUtils.ToMm(p.X),
            ["y"] = ModelQaUtils.ToMm(p.Y),
            ["z"] = ModelQaUtils.ToMm(p.Z)
        };

        // ---------------------------------------------------- levelsWithoutFloor

        private static JObject LevelsWithoutFloor(ModelQaUtils.Context ctx, HashSet<string> levelNames, JObject summary)
        {
            var floors = ctx.Boxes(BuiltInCategory.OST_Floors);
            var foundationSlabs = ctx.Boxes(BuiltInCategory.OST_StructuralFoundation).Where(b => b.Element is Floor).ToList();
            var floorCounts = floors.Where(b => b.Level != null).GroupBy(b => b.Level.Id.GetValue())
                .ToDictionary(g => g.Key, g => g.Count());
            var foundationCounts = foundationSlabs.Where(b => b.Level != null).GroupBy(b => b.Level.Id.GetValue())
                .ToDictionary(g => g.Key, g => g.Count());

            var checkedLevels = ctx.Levels.Where(l => levelNames == null || levelNames.Contains(l.Name)).ToList();
            var missing = new JArray();
            foreach (var level in checkedLevels)
            {
                var id = level.Id.GetValue();
                if (floorCounts.ContainsKey(id))
                    continue;
                missing.Add(new JObject
                {
                    ["level"] = level.Name,
                    ["id"] = id,
                    ["elevationMm"] = ModelQaUtils.ToMm(level.Elevation),
                    ["foundationSlabs"] = foundationCounts.TryGetValue(id, out var n) ? n : 0
                });
            }

            summary["levelsWithoutFloor"] = missing.Count;
            return new JObject
            {
                ["levelsChecked"] = checkedLevels.Count,
                ["total"] = missing.Count,
                ["levels"] = missing
            };
        }
    }
}
