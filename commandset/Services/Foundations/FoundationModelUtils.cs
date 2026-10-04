using System.Globalization;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Foundations
{
    /// <summary>
    ///     Helpers shared by create_slabs and create_foundations: level lookup,
    ///     boundary loops, floor/foundation-slab types by thickness and slab
    ///     creation across Revit versions. All wire lengths are millimetres.
    /// </summary>
    internal static class FoundationModelUtils
    {
        private const double Mm = 304.8;
        private const double LevelTolerance = 1.0 / Mm;

        /// <summary>Per-run lookup state (levels, types, foundation marks) so large batches stay fast.</summary>
        internal class Context
        {
            public Context(Document doc)
            {
                Doc = doc;
                Levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.Elevation).ToList();
            }

            public Document Doc { get; }
            public List<Level> Levels { get; }
            public Dictionary<string, FamilySymbol> Symbols { get; } = new Dictionary<string, FamilySymbol>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, FloorType> SlabTypes { get; } = new Dictionary<string, FloorType>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<ElementId>> FoundationMarks { get; set; }
            public Dictionary<long, double> Undersides { get; } = new Dictionary<long, double>();
            public bool NeedsRegenerate { get; set; }
        }

        internal static double ToFeet(double mm) => mm / Mm;

        internal static double ToMm(double feet) => Math.Round(feet * Mm, 1);

        /// <summary>
        ///     Level from a name (string), an element id or an elevation in mm (number).
        ///     A number that is not a Level id is an elevation: the level within 1 mm, else the
        ///     nearest level; <paramref name="extraOffsetFeet" /> then holds the difference.
        /// </summary>
        internal static Level ResolveLevel(Context ctx, JToken token, List<string> warnings, out double extraOffsetFeet,
            string field = "level")
        {
            extraOffsetFeet = 0;
            if (token == null || token.Type == JTokenType.Null)
                throw new ArgumentException($"'{field}' is required.");
            if (ctx.Levels.Count == 0)
                throw new InvalidOperationException("The project has no levels.");

            if (token.Type == JTokenType.String)
            {
                var name = token.Value<string>().Trim();
                var byName = ctx.Levels.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
                if (byName != null) return byName;
                if (double.TryParse(name, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
                    return ResolveLevel(ctx, new JValue(numeric), warnings, out extraOffsetFeet, field);
                throw new ArgumentException($"Level '{name}' not found. Levels: {string.Join(", ", ctx.Levels.Select(l => l.Name))}.");
            }

            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                throw new ArgumentException($"'{field}' must be a level name, level id or elevation in mm.");

            var value = token.Value<double>();
            if (token.Type == JTokenType.Integer && value > 0)
            {
                var byId = ctx.Levels.FirstOrDefault(l => l.Id.GetValue() == (long)value);
                if (byId != null) return byId;
            }

            var elevation = ToFeet(value);
            var exact = ctx.Levels.FirstOrDefault(l => Math.Abs(l.Elevation - elevation) < LevelTolerance);
            if (exact != null) return exact;

            var nearest = ctx.Levels.OrderBy(l => Math.Abs(l.Elevation - elevation)).First();
            extraOffsetFeet = elevation - nearest.Elevation;
            warnings.Add($"No level at elevation {value} mm; used '{nearest.Name}' with an extra offset of {ToMm(extraOffsetFeet)} mm.");
            return nearest;
        }

        /// <summary>The highest level at or below the elevation (feet), else the lowest level.</summary>
        internal static Level LevelAtOrBelow(Context ctx, double elevationFeet)
        {
            if (ctx.Levels.Count == 0)
                throw new InvalidOperationException("The project has no levels.");
            return ctx.Levels.LastOrDefault(l => l.Elevation <= elevationFeet + LevelTolerance) ?? ctx.Levels.First();
        }

        internal static XYZ ReadXY(JToken token, double z, string field)
        {
            var x = token?.Value<double?>("x");
            var y = token?.Value<double?>("y");
            if (x == null || y == null)
                throw new ArgumentException($"'{field}' needs numeric x and y (mm).");
            return new XYZ(ToFeet(x.Value), ToFeet(y.Value), z);
        }

        /// <summary>Closed loop from points (closing segment added; repeated points dropped).</summary>
        internal static CurveLoop LoopFromPoints(JArray points, double z, string field)
        {
            if (points == null || points.Count < 3)
                throw new ArgumentException($"'{field}' needs at least 3 points.");
            var list = new List<XYZ>();
            foreach (var token in points)
            {
                var p = ReadXY(token, z, field);
                if (list.Count == 0 || list[list.Count - 1].DistanceTo(p) > LevelTolerance)
                    list.Add(p);
            }
            if (list.Count > 1 && list[0].DistanceTo(list[list.Count - 1]) <= LevelTolerance)
                list.RemoveAt(list.Count - 1);
            if (list.Count < 3)
                throw new ArgumentException($"'{field}' needs at least 3 distinct points.");

            var loop = new CurveLoop();
            for (var i = 0; i < list.Count; i++)
                loop.Append(Line.CreateBound(list[i], list[(i + 1) % list.Count]));
            return loop;
        }

        /// <summary>Closed loop from {start, end, mid?} segments (mid makes an arc); ends must meet within 1 mm.</summary>
        internal static CurveLoop LoopFromSegments(JArray segments, double z, string field)
        {
            if (segments == null || segments.Count < 2)
                throw new ArgumentException($"'{field}' needs at least 2 segments.");
            var loop = new CurveLoop();
            XYZ first = null, previous = null;
            for (var i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                var start = ReadXY(segment["start"], z, $"{field}[{i}].start");
                var end = ReadXY(segment["end"], z, $"{field}[{i}].end");
                if (previous != null)
                {
                    if (previous.DistanceTo(start) > LevelTolerance)
                        throw new ArgumentException($"'{field}[{i}]' does not start where the previous segment ends.");
                    start = previous;
                }
                if (first == null) first = start;
                if (i == segments.Count - 1)
                {
                    if (end.DistanceTo(first) > LevelTolerance)
                        throw new ArgumentException($"'{field}' is not closed (last end != first start).");
                    end = first;
                }

                Curve curve = segment["mid"] is JObject mid && mid.HasValues
                    ? (Curve)Arc.Create(start, end, ReadXY(mid, z, $"{field}[{i}].mid"))
                    : Line.CreateBound(start, end);
                loop.Append(curve);
                previous = end;
            }
            return loop;
        }

        internal static CurveLoop ReadOuterLoop(JObject item, double z)
        {
            if (item["boundarySegments"] is JArray segments && segments.Count > 0)
                return LoopFromSegments(segments, z, "boundarySegments");
            return LoopFromPoints(item["boundary"] as JArray, z, "boundary");
        }

        internal static List<CurveLoop> ReadOpenings(JObject item, double z)
        {
            var loops = new List<CurveLoop>();
            if (item["openings"] is JArray openings)
            {
                for (var i = 0; i < openings.Count; i++)
                    loops.Add(LoopFromPoints(openings[i] as JArray, z, $"openings[{i}]"));
            }
            return loops;
        }

        internal static double TypeThicknessMm(FloorType type)
        {
            var width = type.GetCompoundStructure()?.GetWidth();
            if (width.HasValue) return ToMm(width.Value);
            var parameter = type.get_Parameter(BuiltInParameter.FLOOR_ATTR_DEFAULT_THICKNESS_PARAM);
            return parameter != null ? ToMm(parameter.AsDouble()) : 0;
        }

        /// <summary>
        ///     Floor type from typeId, typeName, or thickness (find or create "Slab &lt;t&gt;mm" /
        ///     "Foundation Slab &lt;t&gt;mm" by duplicating the simplest type and resizing its core),
        ///     else the default (foundation) floor type.
        /// </summary>
        internal static FloorType ResolveSlabType(Context ctx, JObject item, bool foundation, List<string> warnings)
        {
            var doc = ctx.Doc;
            var thickness = item.Value<double?>("thickness");
            FloorType type = null;

            var typeId = DocumentationUtils.ReadId(item, "typeId");
            var typeName = item.Value<string>("typeName");
            if (typeId != null)
            {
                type = doc.GetElement(typeId.Value.ToRevitElementId()) as FloorType
                       ?? throw new ArgumentException($"typeId {typeId} is not a floor or foundation slab type.");
            }
            else if (!string.IsNullOrWhiteSpace(typeName))
            {
                var matches = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>()
                    .Where(t => string.Equals(t.Name, typeName.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                type = matches.FirstOrDefault(t => t.IsFoundationSlab == foundation) ?? matches.FirstOrDefault()
                       ?? throw new ArgumentException($"Floor type '{typeName}' not found.");
            }
            else if (thickness.HasValue)
            {
                if (thickness.Value <= 0)
                    throw new ArgumentException("'thickness' must be positive (mm).");
                return SlabTypeForThickness(ctx, thickness.Value, foundation);
            }
            else
            {
                if (foundation)
                {
                    type = SlabTypes(doc, true).FirstOrDefault()
                           ?? throw new ArgumentException("The project has no foundation slab type; give typeId or thickness after adding one.");
                }
                else
                {
                    type = doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.FloorType)) as FloorType
                           ?? SlabTypes(doc, false).FirstOrDefault()
                           ?? throw new ArgumentException("The project has no floor type.");
                }
                warnings.Add($"No type given; used '{type.Name}' ({TypeThicknessMm(type)} mm).");
            }

            if (type.IsFoundationSlab != foundation)
                warnings.Add(foundation
                    ? $"Type '{type.Name}' is not a foundation slab type; a regular floor was created."
                    : $"Type '{type.Name}' is a foundation slab type; the element is a structural foundation.");
            if (thickness.HasValue && Math.Abs(TypeThicknessMm(type) - thickness.Value) > 0.5)
                warnings.Add($"thickness {thickness} mm ignored; type '{type.Name}' is {TypeThicknessMm(type)} mm.");
            return type;
        }

        private static IEnumerable<FloorType> SlabTypes(Document doc, bool foundation)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>()
                .Where(t => t.IsFoundationSlab == foundation);
        }

        private static FloorType SlabTypeForThickness(Context ctx, double thicknessMm, bool foundation)
        {
            var name = (foundation ? "Foundation Slab " : "Slab ") +
                       thicknessMm.ToString("0.#", CultureInfo.InvariantCulture) + "mm";
            if (ctx.SlabTypes.TryGetValue(name, out var cached) && cached.IsValidObject)
                return cached;

            var doc = ctx.Doc;
            var existing = SlabTypes(doc, foundation).FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                ctx.SlabTypes[name] = existing;
                return existing;
            }

            var source = SlabTypes(doc, foundation)
                             .Where(t => t.GetCompoundStructure() != null)
                             .OrderBy(t => t.GetCompoundStructure().LayerCount)
                             .ThenBy(t => t.Name.IndexOf("Concrete", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                          t.Name.IndexOf("Generic", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                             .FirstOrDefault()
                         ?? throw new ArgumentException(foundation
                             ? "No layered foundation slab type to duplicate; give typeId."
                             : "No layered floor type to duplicate; give typeId.");

            var created = (FloorType)source.Duplicate(name);
            var structure = created.GetCompoundStructure();
            var core = Math.Max(structure.GetFirstCoreLayerIndex(), 0);
            var others = structure.GetWidth() - structure.GetLayerWidth(core);
            var target = ToFeet(thicknessMm);
            if (target - others <= 0.001)
                throw new ArgumentException($"Thickness {thicknessMm} mm is smaller than the non-core layers of '{source.Name}' ({ToMm(others)} mm).");
            structure.SetLayerWidth(core, target - others);
            created.SetCompoundStructure(structure);
            ctx.SlabTypes[name] = created;
            return created;
        }

        /// <summary>
        ///     Creates a floor or foundation slab with openings (inner loops) and an optional slope arrow.
        ///     Revit 2023+: Floor.Create with all loops; older: NewFloor/NewSlab/NewFoundationSlab + NewOpening.
        /// </summary>
        internal static Floor CreateSlab(Context ctx, FloorType type, Level level, CurveLoop outer, List<CurveLoop> openings,
            bool structural, Line slopeArrow, double slope, List<string> warnings)
        {
            var doc = ctx.Doc;
            Floor floor;
#if REVIT2023_OR_GREATER
            var loops = new List<CurveLoop> { outer };
            loops.AddRange(openings);
            floor = slopeArrow != null
                ? Floor.Create(doc, loops, type.Id, level.Id, structural, slopeArrow, slope)
                : Floor.Create(doc, loops, type.Id, level.Id, structural, null, 0);
#else
            var curves = new CurveArray();
            foreach (var curve in outer)
                curves.Append(curve);
            if (type.IsFoundationSlab)
            {
                floor = doc.Create.NewFoundationSlab(curves, type, level, structural, XYZ.BasisZ);
                if (slopeArrow != null)
                    warnings.Add("slopeArrow is not supported for foundation slabs before Revit 2023; created flat.");
            }
            else if (slopeArrow != null)
            {
                floor = doc.Create.NewSlab(curves, level, slopeArrow, slope, structural);
                floor.FloorType = type;
            }
            else
            {
                floor = doc.Create.NewFloor(curves, type, level, structural);
            }

            if (floor != null && openings.Count > 0)
            {
                doc.Regenerate();
                foreach (var opening in openings)
                {
                    var profile = new CurveArray();
                    foreach (var curve in opening)
                        profile.Append(curve);
                    doc.Create.NewOpening(floor, profile, true);
                }
            }
#endif
            if (floor == null)
                throw new InvalidOperationException("Revit did not create the slab.");
            ctx.NeedsRegenerate = true;
            return floor;
        }

        /// <summary>Sets the mark (ALL_MODEL_MARK) and records it for underFoundationMark lookups.</summary>
        internal static void SetMark(Context ctx, Element element, JObject item, List<string> warnings)
        {
            var mark = item["mark"];
            if (mark == null || mark.Type == JTokenType.Null) return;
            var text = mark.Type == JTokenType.Float || mark.Type == JTokenType.Integer
                ? mark.Value<double>().ToString(CultureInfo.InvariantCulture)
                : mark.ToString();
            var parameter = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            if (parameter == null || parameter.IsReadOnly || !parameter.Set(text))
            {
                warnings.Add($"Could not set mark '{text}'.");
                return;
            }
            if (ctx.FoundationMarks != null && DocumentationUtils.IsCategory(element, BuiltInCategory.OST_StructuralFoundation))
            {
                if (!ctx.FoundationMarks.TryGetValue(text, out var ids))
                    ctx.FoundationMarks[text] = ids = new List<ElementId>();
                ids.Add(element.Id);
            }
        }

        internal static double? AreaM2(Element element)
        {
            var parameter = element.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
            return parameter == null ? (double?)null : Math.Round(parameter.AsDouble() * 0.09290304, 3);
        }

        internal static void AddWarnings(JObject result, List<string> warnings)
        {
            if (warnings.Count > 0)
                result["warnings"] = new JArray(warnings);
        }
    }
}
