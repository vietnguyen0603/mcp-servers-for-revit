using System.Globalization;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Foundations
{
    /// <summary>
    ///     create_foundations: isolated footings / pile caps (family instances), piles
    ///     (structural columns or foundation families spanning [top - length, top]) and
    ///     polygonal caps (foundation slabs). One transaction per call (chunk), one
    ///     sub-transaction per item.
    /// </summary>
    public class CreateFoundationsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Create Foundations";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "foundations");
            var ctx = new FoundationModelUtils.Context(doc);
            var results = DocumentationUtils.RunBatch(doc, "MCP: Create Foundations", items, token =>
            {
                if (!(token is JObject item))
                    throw new ArgumentException("Each foundation must be an object.");
                var kind = item.Value<string>("kind");
                JObject result;
                switch ((kind ?? "").ToLowerInvariant())
                {
                    case "isolated":
                        result = CreateIsolated(ctx, item);
                        break;
                    case "pile":
                        result = CreatePile(ctx, item);
                        break;
                    case "capslab":
                        result = CreateFoundationSlab(ctx, item);
                        break;
                    default:
                        throw new ArgumentException($"Unknown kind '{kind}' (isolated, pile, capSlab).");
                }
                result.AddFirst(new JProperty("kind", kind));
                return result;
            });
            return Ok($"Created {results.Count(r => r.Value<bool>("success"))} of {results.Count} foundations.",
                DocumentationUtils.Summarize(results));
        }

        private static JObject CreateIsolated(FoundationModelUtils.Context ctx, JObject item)
        {
            var doc = ctx.Doc;
            var warnings = new List<string>();
            var symbol = ResolveSymbol(ctx, item);
            if (!DocumentationUtils.IsCategory(symbol, BuiltInCategory.OST_StructuralFoundation))
                warnings.Add($"'{symbol.FamilyName}' is in category '{symbol.Category?.Name}', not Structural Foundations.");

            var level = FoundationModelUtils.ResolveLevel(ctx, item["level"], warnings, out var extraOffset);
            var offset = FoundationModelUtils.ToFeet(item.Value<double?>("topOffset") ?? 0) + extraOffset;
            var x = FoundationModelUtils.ToFeet(RequireNumber(item, "x"));
            var y = FoundationModelUtils.ToFeet(RequireNumber(item, "y"));

            var instance = doc.Create.NewFamilyInstance(new XYZ(x, y, 0), symbol, level, StructuralType.Footing)
                           ?? throw new InvalidOperationException("Revit did not place the footing.");
            SetLevelOffset(instance, offset, warnings);

            var rotation = item.Value<double?>("rotationDeg") ?? 0;
            if (Math.Abs(rotation) > 1e-9)
            {
                var origin = new XYZ(x, y, level.Elevation);
                ElementTransformUtils.RotateElement(doc, instance.Id, Line.CreateBound(origin, origin + XYZ.BasisZ),
                    rotation * Math.PI / 180);
            }

            FoundationModelUtils.SetMark(ctx, instance, item, warnings);
            ctx.NeedsRegenerate = true;

            var result = new JObject
            {
                ["id"] = instance.Id.GetValue(),
                ["typeId"] = symbol.Id.GetValue(),
                ["familyName"] = symbol.FamilyName,
                ["typeName"] = symbol.Name,
                ["levelName"] = level.Name,
                ["topElevationMm"] = FoundationModelUtils.ToMm(level.Elevation + offset)
            };
            FoundationModelUtils.AddWarnings(result, warnings);
            return result;
        }

        private static JObject CreatePile(FoundationModelUtils.Context ctx, JObject item)
        {
            var doc = ctx.Doc;
            var warnings = new List<string>();
            var symbol = ResolveSymbol(ctx, item);
            var x = FoundationModelUtils.ToFeet(RequireNumber(item, "x"));
            var y = FoundationModelUtils.ToFeet(RequireNumber(item, "y"));
            var length = FoundationModelUtils.ToFeet(RequireNumber(item, "length"));
            if (length <= 0)
                throw new ArgumentException("'length' must be positive (mm).");

            var top = ResolvePileTop(ctx, item, warnings);
            var bottom = top - length;
            var level = item["baseLevel"] != null && item["baseLevel"].Type != JTokenType.Null
                ? FoundationModelUtils.ResolveLevel(ctx, item["baseLevel"], warnings, out _, "baseLevel")
                : FoundationModelUtils.LevelAtOrBelow(ctx, top);

            FamilyInstance instance;
            string placedAs;
            if (DocumentationUtils.IsCategory(symbol, BuiltInCategory.OST_StructuralColumns))
            {
                // Revit attaches an isolated footing to the base of any structural column inside its footprint
                // when the transaction commits: the cap would drop to the pile bottom.
                var footing = IsolatedFootingOver(ctx, x, y, top);
                if (footing != null)
                    throw new ArgumentException(
                        $"'{symbol.FamilyName}' is a structural column family; Revit would attach footing {footing.Id.GetValue()} to the pile bottom and drop it {FoundationModelUtils.ToMm(length)} mm. " +
                        "Use a Structural Foundations pile family (e.g. 'Pile-Steel Pipe' via load_family, length = type parameter Depth/Length), or make the cap a capSlab.");
                instance = doc.Create.NewFamilyInstance(new XYZ(x, y, level.Elevation), symbol, level, StructuralType.Column)
                           ?? throw new InvalidOperationException("Revit did not place the pile.");
                // Base offset first (top is still above), then top on the same level, then the top offset.
                SetRequired(instance, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, level.Id);
                SetRequired(instance, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM, bottom - level.Elevation);
                SetRequired(instance, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, level.Id);
                SetRequired(instance, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM, top - level.Elevation);
                placedAs = "structuralColumn";
            }
            else if (DocumentationUtils.IsCategory(symbol, BuiltInCategory.OST_StructuralFoundation))
            {
                instance = doc.Create.NewFamilyInstance(new XYZ(x, y, 0), symbol, level, StructuralType.Footing)
                           ?? throw new InvalidOperationException("Revit did not place the pile.");
                SetLevelOffset(instance, top - level.Elevation, warnings);
                SetPileLength(instance, symbol, length, item.Value<string>("lengthParameter"), warnings);
                placedAs = "foundation";
            }
            else
            {
                throw new ArgumentException($"'{symbol.FamilyName}' is in category '{symbol.Category?.Name}'; piles need a Structural Column or Structural Foundation family.");
            }

            var pileRotation = item.Value<double?>("rotationDeg") ?? 0;
            if (Math.Abs(pileRotation) > 1e-9)
            {
                var axisOrigin = new XYZ(x, y, level.Elevation);
                ElementTransformUtils.RotateElement(doc, instance.Id, Line.CreateBound(axisOrigin, axisOrigin + XYZ.BasisZ),
                    pileRotation * Math.PI / 180);
            }

            FoundationModelUtils.SetMark(ctx, instance, item, warnings);
            ctx.NeedsRegenerate = true;

            var result = new JObject
            {
                ["id"] = instance.Id.GetValue(),
                ["placedAs"] = placedAs,
                ["typeId"] = symbol.Id.GetValue(),
                ["familyName"] = symbol.FamilyName,
                ["typeName"] = symbol.Name,
                ["levelName"] = level.Name,
                ["topElevationMm"] = FoundationModelUtils.ToMm(top),
                ["bottomElevationMm"] = FoundationModelUtils.ToMm(bottom)
            };
            FoundationModelUtils.AddWarnings(result, warnings);
            return result;
        }

        private static JObject CreateFoundationSlab(FoundationModelUtils.Context ctx, JObject item)
        {
            var slab = new JObject(item);
            slab["structural"] = true;
            slab.Remove("typeName");
            return CreateSlabsEventHandler.CreateFromItem(ctx, slab, "topOffset", true);
        }

        private static double RequireNumber(JObject item, string name)
        {
            return item.Value<double?>(name) ?? throw new ArgumentException($"'{name}' is required (mm).");
        }

        /// <summary>typeId, or familyName (+ typeName, default the first type); cached per call.</summary>
        private static FamilySymbol ResolveSymbol(FoundationModelUtils.Context ctx, JObject item)
        {
            var doc = ctx.Doc;
            var typeId = DocumentationUtils.ReadId(item, "typeId");
            var familyName = item.Value<string>("familyName")?.Trim();
            var typeName = item.Value<string>("typeName")?.Trim();
            var key = typeId != null ? "#" + typeId : familyName + "|" + typeName;
            if (ctx.Symbols.TryGetValue(key, out var cached) && cached.IsValidObject)
                return cached;

            FamilySymbol symbol;
            if (typeId != null)
            {
                symbol = doc.GetElement(typeId.Value.ToRevitElementId()) as FamilySymbol
                         ?? throw new ArgumentException($"typeId {typeId} is not a family type.");
            }
            else if (!string.IsNullOrWhiteSpace(familyName))
            {
                var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .Where(s => string.Equals(s.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(s => s.Name).ToList();
                if (symbols.Count == 0)
                    throw new ArgumentException($"Family '{familyName}' is not loaded (use load_family).");
                symbol = string.IsNullOrWhiteSpace(typeName)
                    ? symbols[0]
                    : symbols.FirstOrDefault(s => string.Equals(s.Name, typeName, StringComparison.OrdinalIgnoreCase))
                      ?? throw new ArgumentException($"Type '{typeName}' of '{familyName}' not found. Types: {string.Join(", ", symbols.Select(s => s.Name).Take(40))} (create one with create_family_type).");
            }
            else
            {
                throw new ArgumentException("Give typeId or familyName (+ typeName).");
            }

            if (!symbol.IsActive)
            {
                symbol.Activate();
                doc.Regenerate();
            }
            ctx.Symbols[key] = symbol;
            return symbol;
        }

        private static double ResolvePileTop(FoundationModelUtils.Context ctx, JObject item, List<string> warnings)
        {
            var topElevation = item.Value<double?>("topElevation");
            if (topElevation.HasValue)
                return FoundationModelUtils.ToFeet(topElevation.Value);

            var id = DocumentationUtils.ReadId(item, "underFoundationId");
            if (id != null)
            {
                var element = ctx.Doc.GetElement(id.Value.ToRevitElementId())
                              ?? throw new ArgumentException($"underFoundationId {id} not found.");
                return Underside(ctx, element);
            }

            var markToken = item["underFoundationMark"];
            if (markToken == null || markToken.Type == JTokenType.Null)
                throw new ArgumentException("Give topElevation, underFoundationId or underFoundationMark.");
            var mark = markToken.Type == JTokenType.Float || markToken.Type == JTokenType.Integer
                ? markToken.Value<double>().ToString(CultureInfo.InvariantCulture)
                : markToken.ToString();

            if (ctx.FoundationMarks == null)
            {
                ctx.FoundationMarks = new Dictionary<string, List<ElementId>>(StringComparer.OrdinalIgnoreCase);
                var foundations = new FilteredElementCollector(ctx.Doc).OfCategory(BuiltInCategory.OST_StructuralFoundation)
                    .WhereElementIsNotElementType();
                foreach (var element in foundations)
                {
                    var value = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    if (!ctx.FoundationMarks.TryGetValue(value, out var list))
                        ctx.FoundationMarks[value] = list = new List<ElementId>();
                    list.Add(element.Id);
                }
            }

            var matches = ctx.FoundationMarks.TryGetValue(mark, out var ids)
                ? ids.Select(i => ctx.Doc.GetElement(i)).Where(e => e != null && e.IsValidObject).Distinct().ToList()
                : new List<Element>();
            if (matches.Count == 0)
                throw new ArgumentException($"No structural foundation with Mark '{mark}'.");
            if (matches.Count > 1)
                warnings.Add($"{matches.Count} foundations have Mark '{mark}'; used id {matches[0].Id.GetValue()}.");
            return Underside(ctx, matches[0]);
        }

        /// <summary>Bottom of the element's bounding box (feet); regenerates once after new elements.</summary>
        private static double Underside(FoundationModelUtils.Context ctx, Element element)
        {
            var key = element.Id.GetValue();
            if (ctx.Undersides.TryGetValue(key, out var cached))
                return cached;
            if (ctx.NeedsRegenerate)
            {
                ctx.Doc.Regenerate();
                ctx.NeedsRegenerate = false;
            }
            var box = element.get_BoundingBox(null)
                      ?? throw new ArgumentException($"Element {key} has no geometry to take the underside from.");
            ctx.Undersides[key] = box.Min.Z;
            return box.Min.Z;
        }

        /// <summary>An isolated footing (family instance) whose plan box contains (x, y) and sits at or above the pile top.</summary>
        private static Element IsolatedFootingOver(FoundationModelUtils.Context ctx, double x, double y, double top)
        {
            if (ctx.NeedsRegenerate)
            {
                ctx.Doc.Regenerate();
                ctx.NeedsRegenerate = false;
            }
            var probe = new Outline(new XYZ(x - 0.01, y - 0.01, top - 1), new XYZ(x + 0.01, y + 0.01, top + 100));
            return new FilteredElementCollector(ctx.Doc).OfCategory(BuiltInCategory.OST_StructuralFoundation)
                .OfClass(typeof(FamilyInstance)).WherePasses(new BoundingBoxIntersectsFilter(probe))
                .Cast<FamilyInstance>().FirstOrDefault(f => f.StructuralType == StructuralType.Footing
                                                            && !DocumentationUtils.IsCategory(f.Symbol, BuiltInCategory.OST_StructuralColumns));
        }

        private static void SetRequired(Element element, BuiltInParameter id, double value)
        {
            var parameter = element.get_Parameter(id);
            if (parameter == null || parameter.IsReadOnly || !parameter.Set(value))
                throw new InvalidOperationException($"Could not set {id}.");
        }

        private static void SetRequired(Element element, BuiltInParameter id, ElementId value)
        {
            var parameter = element.get_Parameter(id);
            if (parameter == null || parameter.IsReadOnly)
                throw new InvalidOperationException($"Could not set {id}.");
            if (parameter.AsElementId() != value && !parameter.Set(value))
                throw new InvalidOperationException($"Could not set {id}.");
        }

        /// <summary>Offset from the level for level-based foundation families (origin = top of footing).</summary>
        private static void SetLevelOffset(FamilyInstance instance, double offset, List<string> warnings)
        {
            var candidates = new[]
            {
                BuiltInParameter.INSTANCE_ELEVATION_PARAM,
                BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM,
                BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM
            };
            foreach (var id in candidates)
            {
                var parameter = instance.get_Parameter(id);
                if (parameter != null && !parameter.IsReadOnly && parameter.Set(offset))
                    return;
            }
            if (Math.Abs(offset) > 1e-9)
                warnings.Add("Could not set the offset from level; the footing sits at the level.");
        }

        /// <summary>Foundation-family piles: set an instance length parameter, else check the type's length.</summary>
        private static void SetPileLength(FamilyInstance instance, FamilySymbol symbol, double length, string lengthParameter,
            List<string> warnings)
        {
            // "Depth" before "Length": barrette families (create_family) use Width x Length for the plan section.
            var names = string.IsNullOrWhiteSpace(lengthParameter)
                ? new[] { "Pile Length", "Depth", "Length", "L" }
                : new[] { lengthParameter };
            foreach (var name in names)
            {
                var parameter = instance.LookupParameter(name);
                if (parameter != null && !parameter.IsReadOnly && parameter.StorageType == StorageType.Double)
                {
                    if (parameter.Set(length)) return;
                }
            }
            foreach (var name in names)
            {
                var parameter = symbol.LookupParameter(name);
                if (parameter != null && parameter.StorageType == StorageType.Double)
                {
                    if (Math.Abs(parameter.AsDouble() - length) > 1 / 304.8)
                        warnings.Add($"Pile length is the type parameter '{name}' = {FoundationModelUtils.ToMm(parameter.AsDouble())} mm, not {FoundationModelUtils.ToMm(length)} mm; create a type per length with create_family_type.");
                    return;
                }
            }
            warnings.Add("The foundation family has no Length parameter; its own geometry defines the pile length.");
        }
    }
}
