using System.IO;
using Newtonsoft.Json.Linq;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    ///     Shared helpers for join_elements and check_model: category parsing,
    ///     cached element bounding boxes with their level and type, a bounding-box
    ///     sweep (broad phase) and solid intersection volumes (narrow phase).
    ///     Wire lengths are millimetres, volumes m3; Revit internal units are feet.
    /// </summary>
    public static class ModelQaUtils
    {
        public const double MmPerFoot = 304.8;
        public const double CubicMetresPerCubicFoot = 0.028316846592;
        public const string NoLevel = "(no level)";

        public static double ToFeet(double mm) => mm / MmPerFoot;

        public static double ToMm(double feet) => Math.Round(feet * MmPerFoot, 1);

        public static readonly BuiltInCategory[] DefaultCategories =
        {
            BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_StructuralColumns,
            BuiltInCategory.OST_StructuralFraming,
            BuiltInCategory.OST_Floors,
            BuiltInCategory.OST_StructuralFoundation
        };

        /// <summary>Parses "OST_Walls" / "Walls" style names (the TS layer normalises to OST_ names).</summary>
        public static BuiltInCategory ParseCategory(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Category name is empty.");
            var trimmed = name.Trim();
            var enumName = trimmed.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)
                ? "OST_" + trimmed.Substring(4)
                : "OST_" + trimmed.Replace(" ", string.Empty);
            if (Enum.TryParse(enumName, true, out BuiltInCategory bic) && Enum.IsDefined(typeof(BuiltInCategory), bic))
                return bic;
            throw new ArgumentException($"Unknown category '{name}'.");
        }

        public static string CategoryLabel(BuiltInCategory bic)
        {
            var text = bic.ToString();
            return text.StartsWith("OST_") ? text.Substring(4) : text;
        }

        /// <summary>Optional set of strings (case-insensitive) from a JSON array, or null when absent/empty.</summary>
        public static HashSet<string> ReadNameSet(JObject parameters, string name)
        {
            if (!(parameters[name] is JArray array) || array.Count == 0)
                return null;
            return new HashSet<string>(array.Select(t => t.ToString().Trim()), StringComparer.OrdinalIgnoreCase);
        }

        public static HashSet<long> ReadIdSet(JObject parameters, string name)
        {
            if (!(parameters[name] is JArray array) || array.Count == 0)
                return null;
            return new HashSet<long>(array.Select(t => t.Value<long>()));
        }

        /// <summary>One model element with its cached box, level and type.</summary>
        public class ElementBox
        {
            public Element Element;
            public long Id;
            public BuiltInCategory Category;
            public XYZ Min;
            public XYZ Max;
            public Level Level;
            public string LevelName;
            public string TypeName;

            public JObject Info() => new JObject
            {
                ["id"] = Id,
                ["category"] = CategoryLabel(Category),
                ["type"] = TypeName,
                ["level"] = LevelName
            };
        }

        /// <summary>Per-run caches: levels, element boxes per category and solids per element.</summary>
        public class Context
        {
            private readonly Dictionary<BuiltInCategory, List<ElementBox>> _boxes = new Dictionary<BuiltInCategory, List<ElementBox>>();
            private readonly Dictionary<long, List<Solid>> _solids = new Dictionary<long, List<Solid>>();
            private readonly Dictionary<long, double> _volumes = new Dictionary<long, double>();
            private readonly Dictionary<long, string> _typeNames = new Dictionary<long, string>();
            private readonly Options _options = new Options
            {
                ComputeReferences = false,
                DetailLevel = ViewDetailLevel.Fine,
                IncludeNonVisibleObjects = false
            };

            public Context(Document doc)
            {
                Doc = doc;
                Levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.Elevation).ToList();
                LevelsById = Levels.ToDictionary(l => l.Id.GetValue());
            }

            public Document Doc { get; }
            public List<Level> Levels { get; }
            public Dictionary<long, Level> LevelsById { get; }
            public int BooleanFailures { get; set; }

            /// <summary>All non-type elements of the category with a bounding box (nested family members skipped).</summary>
            public List<ElementBox> Boxes(BuiltInCategory bic)
            {
                if (_boxes.TryGetValue(bic, out var cached))
                    return cached;

                var list = new List<ElementBox>();
                foreach (var element in new FilteredElementCollector(Doc).OfCategory(bic).WhereElementIsNotElementType())
                {
                    if (element is FamilyInstance fi && fi.SuperComponent != null)
                        continue;
                    if (element.Category == null || element.Category.Id.GetValue() != (long)bic)
                        continue;
                    var bb = element.get_BoundingBox(null);
                    if (bb == null || !bb.Enabled)
                        continue;
                    var level = GetLevel(element);
                    list.Add(new ElementBox
                    {
                        Element = element,
                        Id = element.Id.GetValue(),
                        Category = bic,
                        Min = bb.Min,
                        Max = bb.Max,
                        Level = level,
                        LevelName = level?.Name ?? NoLevel,
                        TypeName = TypeName(element)
                    });
                }

                list.Sort((a, b) => a.Id.CompareTo(b.Id));
                _boxes[bic] = list;
                return list;
            }

            public Level GetLevel(Element element)
            {
                var id = element.LevelId;
                if (id != null && id != ElementId.InvalidElementId && LevelsById.TryGetValue(id.GetValue(), out var level))
                    return level;

                foreach (var bip in new[]
                         {
                             BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,
                             BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,
                             BuiltInParameter.WALL_BASE_CONSTRAINT,
                             BuiltInParameter.SCHEDULE_LEVEL_PARAM,
                             BuiltInParameter.LEVEL_PARAM,
                             BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM
                         })
                {
                    var parameter = element.get_Parameter(bip);
                    if (parameter == null || parameter.StorageType != StorageType.ElementId)
                        continue;
                    var levelId = parameter.AsElementId();
                    if (levelId != null && levelId != ElementId.InvalidElementId &&
                        LevelsById.TryGetValue(levelId.GetValue(), out level))
                        return level;
                }

                return null;
            }

            private string TypeName(Element element)
            {
                var typeId = element.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId)
                    return element.Name;
                var key = typeId.GetValue();
                if (_typeNames.TryGetValue(key, out var name))
                    return name;
                var type = Doc.GetElement(typeId) as ElementType;
                name = type == null
                    ? element.Name
                    : string.IsNullOrEmpty(type.FamilyName) ? type.Name : $"{type.FamilyName}: {type.Name}";
                _typeNames[key] = name;
                return name;
            }

            public List<Solid> Solids(ElementBox box)
            {
                if (_solids.TryGetValue(box.Id, out var cached))
                    return cached;
                var solids = new List<Solid>();
                try
                {
                    CollectSolids(box.Element.get_Geometry(_options), solids);
                }
                catch (Exception)
                {
                    // no usable geometry
                }

                _solids[box.Id] = solids;
                return solids;
            }

            public double VolumeFt3(ElementBox box)
            {
                if (_volumes.TryGetValue(box.Id, out var v))
                    return v;
                v = Solids(box).Sum(s => s.Volume);
                _volumes[box.Id] = v;
                return v;
            }

            /// <summary>Volume (ft3) of the intersection of the two elements' solids.</summary>
            public double IntersectionFt3(ElementBox a, ElementBox b)
            {
                double volume = 0;
                foreach (var sa in Solids(a))
                foreach (var sb in Solids(b))
                {
                    try
                    {
                        var result = BooleanOperationsUtils.ExecuteBooleanOperation(sa, sb, BooleanOperationsType.Intersect);
                        if (result != null && result.Volume > 0)
                            volume += result.Volume;
                    }
                    catch (Exception)
                    {
                        BooleanFailures++;
                    }
                }

                return volume;
            }
        }

        private static void CollectSolids(GeometryElement geometry, List<Solid> solids)
        {
            if (geometry == null)
                return;
            foreach (var obj in geometry)
            {
                if (obj is Solid solid && solid.Volume > 1e-9 && solid.Faces.Size > 0)
                    solids.Add(solid);
                else if (obj is GeometryInstance instance)
                    CollectSolids(instance.GetInstanceGeometry(), solids);
            }
        }

        /// <summary>
        ///     Broad phase: pairs of boxes from <paramref name="a" /> and <paramref name="b" /> whose
        ///     bounding boxes overlap (gaps up to <paramref name="toleranceFeet" />; a negative value
        ///     requires a real overlap). Sweep along Z, so cost is ~n x elements per storey.
        ///     When a and b are the same list each unordered pair is returned once.
        /// </summary>
        public static List<KeyValuePair<ElementBox, ElementBox>> SweepPairs(IList<ElementBox> a, IList<ElementBox> b,
            double toleranceFeet)
        {
            var same = ReferenceEquals(a, b);
            var events = new List<KeyValuePair<ElementBox, int>>(a.Count + (same ? 0 : b.Count));
            events.AddRange(a.Select(x => new KeyValuePair<ElementBox, int>(x, 0)));
            if (!same)
                events.AddRange(b.Select(x => new KeyValuePair<ElementBox, int>(x, 1)));
            events.Sort((x, y) =>
            {
                var c = x.Key.Min.Z.CompareTo(y.Key.Min.Z);
                return c != 0 ? c : x.Key.Id.CompareTo(y.Key.Id);
            });

            var active = new[] { new List<ElementBox>(), new List<ElementBox>() };
            var pairs = new List<KeyValuePair<ElementBox, ElementBox>>();
            foreach (var ev in events)
            {
                var box = ev.Key;
                var side = ev.Value;
                var other = same ? 0 : 1 - side;
                var list = active[other];
                list.RemoveAll(o => o.Max.Z + toleranceFeet < box.Min.Z);
                foreach (var o in list)
                {
                    if (o.Id == box.Id || !OverlapXY(o, box, toleranceFeet))
                        continue;
                    pairs.Add(side == 0
                        ? new KeyValuePair<ElementBox, ElementBox>(box, o)
                        : new KeyValuePair<ElementBox, ElementBox>(o, box));
                }

                active[same ? 0 : side].Add(box);
            }

            return pairs;
        }

        private static bool OverlapXY(ElementBox a, ElementBox b, double tol)
        {
            return a.Min.X <= b.Max.X + tol && b.Min.X <= a.Max.X + tol &&
                   a.Min.Y <= b.Max.Y + tol && b.Min.Y <= a.Max.Y + tol;
        }

        public static bool BoxContains(ElementBox box, XYZ p, double tol)
        {
            return p.X >= box.Min.X - tol && p.X <= box.Max.X + tol &&
                   p.Y >= box.Min.Y - tol && p.Y <= box.Max.Y + tol &&
                   p.Z >= box.Min.Z - tol && p.Z <= box.Max.Z + tol;
        }

        /// <summary>Uniform XY grid of boxes for point queries (supports near beam ends / column bases).</summary>
        public class XYGrid
        {
            private readonly double _cell;
            private readonly Dictionary<long, List<ElementBox>> _cells = new Dictionary<long, List<ElementBox>>();

            public XYGrid(IEnumerable<ElementBox> boxes, double cellFeet, double padFeet)
            {
                _cell = cellFeet;
                foreach (var box in boxes)
                {
                    var x0 = Index(box.Min.X - padFeet);
                    var x1 = Index(box.Max.X + padFeet);
                    var y0 = Index(box.Min.Y - padFeet);
                    var y1 = Index(box.Max.Y + padFeet);
                    // Guard against degenerate huge elements: cap the number of cells per element.
                    if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 250000)
                    {
                        Oversized.Add(box);
                        continue;
                    }

                    for (var i = x0; i <= x1; i++)
                    for (var j = y0; j <= y1; j++)
                    {
                        var key = Key(i, j);
                        if (!_cells.TryGetValue(key, out var list))
                            _cells[key] = list = new List<ElementBox>();
                        list.Add(box);
                    }
                }
            }

            private List<ElementBox> Oversized { get; } = new List<ElementBox>();

            private int Index(double value) => (int)Math.Floor(value / _cell);

            private static long Key(int i, int j) => ((long)i << 32) ^ (uint)j;

            public IEnumerable<ElementBox> Query(XYZ p)
            {
                if (_cells.TryGetValue(Key(Index(p.X), Index(p.Y)), out var list))
                    foreach (var box in list)
                        yield return box;
                foreach (var box in Oversized)
                    yield return box;
            }
        }

        /// <summary>Writes the JSON to a local file, creating the folder.</summary>
        public static void WriteJson(string path, JToken json)
        {
            var full = Path.GetFullPath(path);
            var folder = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            File.WriteAllText(full, json.ToString(Newtonsoft.Json.Formatting.Indented));
        }
    }
}
