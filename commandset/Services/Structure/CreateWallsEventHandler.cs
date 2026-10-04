using System.Globalization;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services.Foundations;
using RevitMCPCommandSet.Services.Framing;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Structure
{
    /// <summary>
    ///     create_walls: straight or arc walls from plan points (mm) with an
    ///     independent base (level + offset) and top (level + offset, or an
    ///     unconnected height), type by id, name or thickness (finds or creates
    ///     "&lt;prefix&gt; &lt;t&gt;mm"), location line, flip, mark and comments.
    ///     One transaction per call (chunk), one sub-transaction per wall.
    /// </summary>
    public class CreateWallsEventHandler : JsonParameterEventHandler
    {
        private const double MinHeightFeet = 1.0 / DocumentationUtils.MmPerFoot;

        public override string GetName() => "Create Walls";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "walls");
            var prefix = parameters.Value<string>("typeNamePrefix");
            var state = new State(doc, string.IsNullOrWhiteSpace(prefix) ? "Wall" : prefix.Trim());
            var results = DocumentationUtils.RunBatch(doc, "MCP: Create Walls", items, item =>
            {
                if (!(item is JObject wall))
                    throw new ArgumentException("Each wall must be an object.");
                return Create(state, wall);
            });
            return Ok($"Created {results.Count(r => r.Value<bool>("success"))} of {results.Count} walls.",
                DocumentationUtils.Summarize(results));
        }

        private class State
        {
            public State(Document doc, string prefix)
            {
                Levels = new FoundationModelUtils.Context(doc);
                Prefix = prefix;
            }

            public FoundationModelUtils.Context Levels { get; }
            public Document Doc => Levels.Doc;
            public string Prefix { get; }
            public Dictionary<string, WallType> TypesByName { get; } = new Dictionary<string, WallType>(StringComparer.OrdinalIgnoreCase);
        }

        private static JObject Create(State state, JObject item)
        {
            var doc = state.Doc;
            var warnings = new List<string>();

            var baseLevel = FoundationModelUtils.ResolveLevel(state.Levels, item["baseLevel"], warnings, out var baseExtra, "baseLevel");
            var baseOffset = FoundationModelUtils.ToFeet(item.Value<double?>("baseOffset") ?? 0) + baseExtra;
            var baseElevation = baseLevel.Elevation + baseOffset;

            Level topLevel = null;
            double topOffset = 0, height;
            var topToken = item["topLevel"];
            if (topToken != null && topToken.Type != JTokenType.Null)
            {
                topLevel = FoundationModelUtils.ResolveLevel(state.Levels, topToken, warnings, out var topExtra, "topLevel");
                topOffset = FoundationModelUtils.ToFeet(item.Value<double?>("topOffset") ?? 0) + topExtra;
                height = topLevel.Elevation + topOffset - baseElevation;
                if (height < MinHeightFeet)
                    throw new ArgumentException(
                        $"Top ({FoundationModelUtils.ToMm(topLevel.Elevation + topOffset)} mm) is not above the base ({FoundationModelUtils.ToMm(baseElevation)} mm).");
            }
            else
            {
                var heightMm = item.Value<double?>("height")
                               ?? throw new ArgumentException("Give topLevel (+ topOffset) or height (mm).");
                if (heightMm <= 0)
                    throw new ArgumentException("'height' must be positive (mm).");
                height = FoundationModelUtils.ToFeet(heightMm);
            }

            var type = ResolveType(state, item, warnings);
            var curve = ReadCurve(doc, item, baseLevel.Elevation);
            var structural = item.Value<bool?>("structural") ?? true;
            var flip = item.Value<bool?>("flip") ?? false;
            var locationLine = ParseLocationLine(item.Value<string>("locationLine"));

            var wall = Wall.Create(doc, curve, type.Id, baseLevel.Id, height, baseOffset, flip, structural)
                       ?? throw new InvalidOperationException("Revit did not create the wall.");

            if (topLevel != null)
            {
                FramingLookup.SetParameter(wall, BuiltInParameter.WALL_HEIGHT_TYPE, p => p.Set(topLevel.Id), "topLevel", warnings);
                FramingLookup.SetParameter(wall, BuiltInParameter.WALL_TOP_OFFSET, p => p.Set(topOffset), "topOffset", warnings);
            }

            if (locationLine != WallLocationLine.WallCenterline)
            {
                // Changing the location line keeps the wall in place and moves its location curve;
                // put the curve back so the given line is the chosen reference (e.g. a finish face).
                FramingLookup.SetParameter(wall, BuiltInParameter.WALL_KEY_REF_PARAM, p => p.Set((int)locationLine), "locationLine", warnings);
                doc.Regenerate();
                try
                {
                    if (wall.Location is LocationCurve location)
                        location.Curve = curve;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not place the {item.Value<string>("locationLine")} on the given line: {ex.Message}");
                }
            }

            FramingLookup.SetIdentity(wall, item, warnings);

            var result = new JObject
            {
                ["id"] = wall.Id.GetValue(),
                ["typeId"] = type.Id.GetValue(),
                ["typeName"] = type.Name,
                ["thicknessMm"] = FoundationModelUtils.ToMm(type.Width),
                ["structural"] = structural,
                ["baseLevel"] = baseLevel.Name,
                ["baseOffsetMm"] = FoundationModelUtils.ToMm(baseOffset),
                ["baseElevationMm"] = FoundationModelUtils.ToMm(baseElevation),
                ["topLevel"] = topLevel?.Name,
                ["topOffsetMm"] = topLevel == null ? null : (JToken)FoundationModelUtils.ToMm(topOffset),
                ["topElevationMm"] = FoundationModelUtils.ToMm(baseElevation + height),
                ["heightMm"] = FoundationModelUtils.ToMm(height),
                ["lengthMm"] = FoundationModelUtils.ToMm(curve.Length)
            };
            if (locationLine != WallLocationLine.WallCenterline)
                result["locationLine"] = item.Value<string>("locationLine");
            FoundationModelUtils.AddWarnings(result, warnings);
            return result;
        }

        private static Curve ReadCurve(Document doc, JObject item, double z)
        {
            var start = FoundationModelUtils.ReadXY(item["start"], z, "start");
            var end = FoundationModelUtils.ReadXY(item["end"], z, "end");
            if (start.DistanceTo(end) < doc.Application.ShortCurveTolerance)
                throw new ArgumentException("Wall start and end are the same point.");
            if (!(item["mid"] is JObject mid) || !mid.HasValues)
                return Line.CreateBound(start, end);
            try
            {
                return Arc.Create(start, end, FoundationModelUtils.ReadXY(mid, z, "mid"));
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Cannot make an arc through start, mid and end: {ex.Message}");
            }
        }

        private static WallLocationLine ParseLocationLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return WallLocationLine.WallCenterline;
            switch (value.Trim().ToLowerInvariant())
            {
                case "wallcenterline": return WallLocationLine.WallCenterline;
                case "corecenterline": return WallLocationLine.CoreCenterline;
                case "finishfaceexterior": return WallLocationLine.FinishFaceExterior;
                case "finishfaceinterior": return WallLocationLine.FinishFaceInterior;
                case "coreexterior": return WallLocationLine.CoreExterior;
                case "coreinterior": return WallLocationLine.CoreInterior;
                default:
                    throw new ArgumentException(
                        $"Invalid locationLine '{value}' (wallCenterline, coreCenterline, finishFaceExterior, finishFaceInterior, coreExterior, coreInterior).");
            }
        }

        /// <summary>Wall type from typeId, typeName, or thickness (find or create "&lt;prefix&gt; &lt;t&gt;mm"), else the default wall type.</summary>
        private static WallType ResolveType(State state, JObject item, List<string> warnings)
        {
            var doc = state.Doc;
            var typeId = DocumentationUtils.ReadId(item, "typeId");
            var typeName = item.Value<string>("typeName");
            var thickness = item.Value<double?>("thickness");

            if (typeId != null)
                return doc.GetElement(typeId.Value.ToRevitElementId()) as WallType
                       ?? throw new ArgumentException($"typeId {typeId} is not a wall type.");

            if (!string.IsNullOrWhiteSpace(typeName))
            {
                var name = typeName.Trim();
                if (state.TypesByName.TryGetValue(name, out var cached) && cached.IsValidObject)
                    return cached;
                var match = WallTypes(doc).FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                            ?? throw new ArgumentException($"Wall type '{name}' not found.");
                state.TypesByName[name] = match;
                return match;
            }

            if (thickness.HasValue)
            {
                if (thickness.Value <= 0)
                    throw new ArgumentException("'thickness' must be positive (mm).");
                return TypeForThickness(state, thickness.Value);
            }

            var fallback = doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.WallType)) as WallType
                           ?? WallTypes(doc).FirstOrDefault(t => t.Kind == WallKind.Basic)
                           ?? throw new ArgumentException("The project has no wall type.");
            warnings.Add($"No type given; used '{fallback.Name}' ({FoundationModelUtils.ToMm(fallback.Width)} mm).");
            return fallback;
        }

        private static IEnumerable<WallType> WallTypes(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>();
        }

        /// <summary>
        ///     Finds or creates "&lt;prefix&gt; &lt;t&gt;mm" by duplicating the basic wall type with the
        ///     fewest layers (concrete, then generic preferred) and resizing its core layer.
        /// </summary>
        private static WallType TypeForThickness(State state, double thicknessMm)
        {
            var name = $"{state.Prefix} {thicknessMm.ToString("0.#", CultureInfo.InvariantCulture)}mm";
            if (state.TypesByName.TryGetValue(name, out var cached) && cached.IsValidObject)
                return cached;

            var doc = state.Doc;
            var existing = WallTypes(doc).FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                state.TypesByName[name] = existing;
                return existing;
            }

            var source = WallTypes(doc)
                             .Where(t => t.Kind == WallKind.Basic && t.GetCompoundStructure() != null)
                             .OrderBy(t => t.GetCompoundStructure().LayerCount)
                             .ThenBy(t => SourceRank(doc, t))
                             .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                             .FirstOrDefault()
                         ?? throw new ArgumentException("No basic wall type to duplicate; give typeId or typeName.");

            var created = (WallType)source.Duplicate(name);
            var structure = created.GetCompoundStructure();
            var core = Math.Max(structure.GetFirstCoreLayerIndex(), 0);
            var others = structure.GetWidth() - structure.GetLayerWidth(core);
            var target = FoundationModelUtils.ToFeet(thicknessMm);
            if (target - others <= 0.001)
                throw new ArgumentException(
                    $"Thickness {thicknessMm} mm is smaller than the non-core layers of '{source.Name}' ({FoundationModelUtils.ToMm(others)} mm).");
            structure.SetLayerWidth(core, target - others);
            created.SetCompoundStructure(structure);
            state.TypesByName[name] = created;
            return created;
        }

        /// <summary>0 for concrete (type or core material name), 1 for generic, 2 otherwise.</summary>
        private static int SourceRank(Document doc, WallType type)
        {
            var structure = type.GetCompoundStructure();
            var core = Math.Max(structure.GetFirstCoreLayerIndex(), 0);
            var material = doc.GetElement(structure.GetMaterialId(core)) as Material;
            bool Has(string text, string word) => text != null && text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
            if (Has(type.Name, "Concrete") || Has(material?.Name, "Concrete")) return 0;
            if (Has(type.Name, "Generic")) return 1;
            return 2;
        }
    }
}
