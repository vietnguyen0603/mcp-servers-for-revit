using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Modify
{
    /// <summary>
    ///     Re-points wall tops from one level to another (e.g. interior walls that stop at
    ///     "3RD" now stop at "3RD TOP PLATE"). A wall is retargeted when its current top
    ///     elevation - constrained or unconnected - lies at a mapped "from" level. Walls that
    ///     run past a mapped level are reported, not split. dryRun reports without changing.
    /// </summary>
    public class SetWallTopLevelsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Set Wall Top Levels";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            var mapping = ReadMapping(parameters, levels);
            if (mapping.Count == 0)
                throw new ArgumentException("No level pairs: give 'mapping' or a 'suffix' that matches existing levels.");

            var tolerance = DocumentationUtils.MmToFeet(parameters.Value<double?>("tolerance") ?? 25);
            var function = parameters.Value<string>("wallFunction") ?? "interior";
            var walls = SelectWalls(doc, parameters, function);

            var targets = new List<Target>();
            var spanning = new JArray();
            foreach (var wall in walls)
            {
                var bottom = WallBottom(doc, wall);
                var top = WallTop(doc, wall);
                if (bottom == null || top == null)
                    continue;

                var pair = mapping.FirstOrDefault(p => Math.Abs(top.Value - p.From.Elevation) <= tolerance && bottom.Value < p.To.Elevation - tolerance);
                if (pair != null)
                    targets.Add(new Target { Wall = wall, Pair = pair });
                else if (mapping.Any(p => bottom.Value < p.From.Elevation - tolerance && top.Value > p.From.Elevation + tolerance))
                    spanning.Add(wall.Id.GetValue());
            }

            var dryRun = parameters.Value<bool?>("dryRun") ?? false;
            var results = dryRun
                ? targets.Select((t, index) => JObject.FromObject(Describe(t, index, true))).ToList()
                : DocumentationUtils.RunBatch(doc, "MCP: Set Wall Top Levels", new JArray(Enumerable.Range(0, targets.Count)),
                    token => Apply(targets[token.Value<int>()]));

            var summary = new JObject
            {
                ["dryRun"] = dryRun,
                ["wallFunction"] = function,
                ["mapping"] = new JArray(mapping.Select(p => new JObject
                {
                    ["from"] = p.From.Name, ["to"] = p.To.Name, ["topOffset"] = DocumentationUtils.FeetToMm(p.TopOffset)
                })),
                ["candidates"] = walls.Count,
                ["matched"] = targets.Count,
                ["succeeded"] = results.Count(r => r.Value<bool>("success")),
                ["failed"] = results.Count(r => !r.Value<bool>("success")),
                ["spanningWallIds"] = spanning,
                ["results"] = new JArray(results)
            };
            return Ok($"{(dryRun ? "Would retarget" : "Retargeted")} {summary["succeeded"]} of {targets.Count} walls; {spanning.Count} walls run past a mapped level.", summary);
        }

        private static object Apply(Target target)
        {
            var heightType = target.Wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
            var topOffset = target.Wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET);
            if (heightType == null || heightType.IsReadOnly || topOffset == null)
                throw new InvalidOperationException("Wall top constraint is read-only (in-place, grouped or attached wall).");

            heightType.Set(target.Pair.To.Id);
            topOffset.Set(target.Pair.TopOffset);
            return Describe(target, 0, false);
        }

        private static object Describe(Target target, int index, bool dryRun)
        {
            var result = new JObject
            {
                ["wallId"] = target.Wall.Id.GetValue(),
                ["type"] = target.Wall.WallType.Name,
                ["from"] = target.Pair.From.Name,
                ["to"] = target.Pair.To.Name
            };
            if (dryRun)
            {
                result.AddFirst(new JProperty("success", true));
                result.AddFirst(new JProperty("index", index));
            }

            return result;
        }

        /// <summary>Explicit from/to pairs win; a suffix maps every level "X" to an existing "X{suffix}".</summary>
        private static List<Pair> ReadMapping(JObject parameters, List<Level> levels)
        {
            var defaultOffset = DocumentationUtils.MmToFeet(parameters.Value<double?>("topOffset") ?? 0);
            if (parameters["mapping"] is JArray mapping && mapping.Count > 0)
            {
                return mapping.Select(item => new Pair
                {
                    From = FindLevel(levels, item["from"]),
                    To = FindLevel(levels, item["to"]),
                    TopOffset = item.Value<double?>("topOffset") is double mm ? DocumentationUtils.MmToFeet(mm) : defaultOffset
                }).ToList();
            }

            var suffix = parameters.Value<string>("suffix");
            if (string.IsNullOrEmpty(suffix))
                return new List<Pair>();

            return levels
                .Select(from => new Pair
                {
                    From = from,
                    To = levels.FirstOrDefault(l => string.Equals(l.Name, from.Name + suffix, StringComparison.Ordinal)),
                    TopOffset = defaultOffset
                })
                .Where(p => p.To != null)
                .ToList();
        }

        private static Level FindLevel(List<Level> levels, JToken token)
        {
            if (token == null)
                throw new ArgumentException("Each mapping item needs 'from' and 'to' (level id or name).");
            if (token.Type == JTokenType.Integer)
            {
                var id = token.Value<long>();
                return levels.FirstOrDefault(l => l.Id.GetValue() == id) ?? throw new ArgumentException($"Element {id} is not a level.");
            }

            var name = token.Value<string>();
            return levels.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal))
                   ?? levels.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"Level '{name}' not found. Available: {string.Join(", ", levels.Select(l => l.Name))}.");
        }

        private static List<Wall> SelectWalls(Document doc, JObject parameters, string function)
        {
            var ids = parameters["elementIds"] is JArray array && array.Count > 0
                ? new HashSet<long>(array.Select(t => t.Value<long>()))
                : null;
            return new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
                .Where(w => ids == null || ids.Contains(w.Id.GetValue()))
                .Where(w => function == "all"
                            || (function == "interior" && w.WallType.Function == WallFunction.Interior)
                            || (function == "exterior" && w.WallType.Function == WallFunction.Exterior))
                .OrderBy(w => w.Id.GetValue())
                .ToList();
        }

        private static double? WallBottom(Document doc, Wall wall)
        {
            var baseLevel = doc.GetElement(wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() ?? ElementId.InvalidElementId) as Level;
            return baseLevel == null ? (double?)null : baseLevel.Elevation + (wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0);
        }

        /// <summary>Top elevation from the top constraint, or base + unconnected height when unconstrained.</summary>
        private static double? WallTop(Document doc, Wall wall)
        {
            if (doc.GetElement(wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() ?? ElementId.InvalidElementId) is Level topLevel)
                return topLevel.Elevation + (wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.AsDouble() ?? 0);

            var bottom = WallBottom(doc, wall);
            var height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble();
            return bottom == null || height == null ? (double?)null : bottom.Value + height.Value;
        }

        private class Pair
        {
            public Level From;
            public Level To;
            public double TopOffset;
        }

        private class Target
        {
            public Wall Wall;
            public Pair Pair;
        }
    }
}
