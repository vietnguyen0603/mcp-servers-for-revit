using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Framing
{
    /// <summary>
    ///     Places structural trusses (Truss.Create) along a plan line on a level,
    ///     with start/end level offsets, bearing chord and justification, truss
    ///     height, rotation and chord/web framing types. One sub-transaction per
    ///     truss. Arrays of parallel trusses are expanded by the MCP server.
    /// </summary>
    public class CreateTrussesEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Create Trusses";

        private const string LoadHint =
            "No structural truss family is loaded. Load one with load_family from the Revit library folder " +
            "'Structural Trusses' (e.g. ...\\Libraries\\<country>\\Structural Trusses\\Howe Flat Truss.rfa), then retry.";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "trusses");
            var anyTruss = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_Truss).FirstElementId();
            if (anyTruss == null || anyTruss == ElementId.InvalidElementId)
                throw new InvalidOperationException(LoadHint);

            var trussLookup = new FramingLookup(doc, BuiltInCategory.OST_Truss, "Structural Trusses");
            var framingLookup = new FramingLookup(doc, BuiltInCategory.OST_StructuralFraming, "Structural Framing");
            var results = DocumentationUtils.RunBatch(doc, "MCP: Create Trusses", items,
                item => Create(doc, trussLookup, framingLookup, (JObject)item));
            return Ok($"Created {results.Count(r => r.Value<bool>("success"))} of {results.Count} trusses.",
                DocumentationUtils.Summarize(results));
        }

        private static object Create(Document doc, FramingLookup trussLookup, FramingLookup framingLookup, JObject item)
        {
            var symbol = trussLookup.ResolveType(item);
            var level = trussLookup.ResolveLevel(item["level"], "level");
            var startOffset = FramingLookup.ReadMm(item, "startOffset") ?? 0;
            var endOffset = FramingLookup.ReadMm(item, "endOffset") ?? startOffset;
            var warnings = new List<string>();

            var start = FramingLookup.RequireXy(item, "start", level.Elevation + startOffset);
            var end = FramingLookup.RequireXy(item, "end", level.Elevation + endOffset);
            var planLength = new XYZ(end.X - start.X, end.Y - start.Y, 0).GetLength();
            if (planLength < doc.Application.ShortCurveTolerance)
                throw new ArgumentException("Truss start and end must differ in plan (a truss line cannot be vertical).");
            var line = Line.CreateBound(start, end);

            // Resolve member types before creating anything so a bad name fails fast.
            var memberTypes = ResolveMemberTypes(framingLookup, item["memberTypes"] as JObject);
            var scope = (item.Value<string>("memberTypeScope") ?? "instance").Trim().ToLowerInvariant();
            if (scope != "instance" && scope != "type")
                throw new ArgumentException($"Invalid memberTypeScope '{scope}' (instance, type).");

            if (!symbol.IsActive)
                symbol.Activate();

            var workPlane = item.Value<string>("workPlane")?.Trim().ToLowerInvariant();
            if (workPlane != null && workPlane != "vertical" && workPlane != "level")
                throw new ArgumentException($"Invalid workPlane '{workPlane}' (vertical, level).");
            if (workPlane == "level" && Math.Abs(startOffset - endOffset) > 1e-9)
                throw new ArgumentException("workPlane 'level' needs equal start and end offsets; use 'vertical' for a sloped truss line.");

            Truss truss;
            if (workPlane != null)
            {
                truss = CreateOnPlane(doc, symbol, line, workPlane == "vertical");
            }
            else
            {
                // Default: the vertical plane through the line; fall back to a horizontal plane at the line's height.
                try
                {
                    truss = CreateOnPlane(doc, symbol, line, true);
                }
                catch (Exception ex) when (Math.Abs(startOffset - endOffset) < 1e-9)
                {
                    truss = CreateOnPlane(doc, symbol, line, false);
                    warnings.Add($"Vertical work plane rejected ({ex.Message}); placed on a horizontal plane instead.");
                }
            }

            SetLevelAndOffsets(truss, level, startOffset, endOffset, warnings);

            var bearingChord = item.Value<string>("bearingChord");
            if (!string.IsNullOrWhiteSpace(bearingChord))
                SetChoice(truss, BuiltInParameter.TRUSS_BEARING_CHORD_TOP_BOTTOM_PARAM, bearingChord, new[] { "top", "bottom" }, "bearingChord", warnings);
            var justification = item.Value<string>("bearingJustification");
            if (!string.IsNullOrWhiteSpace(justification))
                SetChoice(truss, BuiltInParameter.TRUSS_ELEMENT_BEARING_JUST_PARAM, justification, new[] { "top", "center", "bottom" }, "bearingJustification", warnings);

            var height = FramingLookup.ReadMm(item, "height");
            if (height.HasValue)
            {
                if (height.Value <= 0)
                    throw new ArgumentException("height must be positive.");
                FramingLookup.SetParameter(truss, BuiltInParameter.TRUSS_HEIGHT, p => p.Set(height.Value), "height", warnings);
            }

            var rotation = item.Value<double?>("rotationDeg");
            if (rotation.HasValue && Math.Abs(rotation.Value) > 1e-9)
                FramingLookup.SetParameter(truss, BuiltInParameter.TRUSS_ELEMENT_ANGLE_PARAM, p => p.Set(rotation.Value * Math.PI / 180), "rotationDeg", warnings);

            FramingLookup.SetIdentity(truss, item, warnings);

            if (memberTypes.Count > 0 && scope == "type")
                SetTypeMemberTypes(doc, truss, memberTypes, warnings);

            doc.Regenerate();
            var members = new List<ElementId>();
            foreach (ElementId id in truss.Members)
                members.Add(id);

            var changed = 0;
            if (memberTypes.Count > 0 && scope == "instance")
                changed = SetInstanceMemberTypes(doc, truss, members, memberTypes, warnings);

            var result = new JObject
            {
                ["id"] = truss.Id.GetValue(),
                ["typeId"] = symbol.Id.GetValue(),
                ["level"] = level.Name,
                ["startOffset"] = DocumentationUtils.FeetToMm(startOffset),
                ["endOffset"] = DocumentationUtils.FeetToMm(endOffset),
                ["span"] = DocumentationUtils.FeetToMm(line.Length),
                ["memberCount"] = members.Count
            };
            var actualHeight = truss.get_Parameter(BuiltInParameter.TRUSS_HEIGHT);
            if (actualHeight != null && actualHeight.StorageType == StorageType.Double)
                result["height"] = DocumentationUtils.FeetToMm(actualHeight.AsDouble());
            if (scope == "instance" && memberTypes.Count > 0)
                result["membersRetyped"] = changed;
            if (warnings.Count > 0)
                result["warnings"] = new JArray(warnings);
            return result;
        }

        private static Truss CreateOnPlane(Document doc, FamilySymbol symbol, Line line, bool vertical)
        {
            var start = line.GetEndPoint(0);
            var direction = line.Direction;
            Plane plane;
            if (vertical)
            {
                var horizontal = new XYZ(direction.X, direction.Y, 0).Normalize();
                plane = Plane.CreateByNormalAndOrigin(horizontal.CrossProduct(XYZ.BasisZ).Normalize(), start);
            }
            else
            {
                plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, start);
            }

            var sketchPlane = SketchPlane.Create(doc, plane);
            try
            {
                return Truss.Create(doc, symbol.Id, sketchPlane.Id, line)
                       ?? throw new InvalidOperationException("Revit did not create the truss.");
            }
            catch
            {
                try
                {
                    doc.Delete(sketchPlane.Id);
                }
                catch
                {
                    // the sub-transaction rollback removes it anyway
                }

                throw;
            }
        }

        /// <summary>Reference level, then start/end offsets, so the result does not depend on the level Revit picked.</summary>
        private static void SetLevelAndOffsets(Truss truss, Level level, double startOffset, double endOffset, List<string> warnings)
        {
            var reference = truss.get_Parameter(BuiltInParameter.TRUSS_ELEMENT_REFERENCE_LEVEL_PARAM);
            if (reference != null && reference.StorageType == StorageType.ElementId && reference.AsElementId() != level.Id)
            {
                if (reference.IsReadOnly)
                    warnings.Add($"Reference level is read-only; the truss references level id {reference.AsElementId().GetValue()}.");
                else
                    reference.Set(level.Id);
            }

            SetOffset(truss, BuiltInParameter.TRUSS_ELEMENT_END0_ELEVATION, startOffset, "startOffset", warnings);
            SetOffset(truss, BuiltInParameter.TRUSS_ELEMENT_END1_ELEVATION, endOffset, "endOffset", warnings);
        }

        private static void SetOffset(Element element, BuiltInParameter id, double value, string label, List<string> warnings)
        {
            var parameter = element.get_Parameter(id);
            if (parameter == null || parameter.StorageType != StorageType.Double)
                return;
            if (Math.Abs(parameter.AsDouble() - value) < 1e-6)
                return;
            if (parameter.IsReadOnly)
            {
                warnings.Add($"Could not set {label}: parameter is read-only.");
                return;
            }

            parameter.Set(value);
        }

        /// <summary>
        ///     Sets an enumerated integer parameter by its displayed label: tries the
        ///     candidate values and keeps the one whose value string matches.
        /// </summary>
        private static void SetChoice(Element element, BuiltInParameter id, string wanted, string[] allowed, string label, List<string> warnings)
        {
            var key = wanted.Trim().ToLowerInvariant();
            if (!allowed.Contains(key))
                throw new ArgumentException($"Invalid {label} '{wanted}' ({string.Join(", ", allowed)}).");

            var parameter = element.get_Parameter(id);
            if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.Integer)
            {
                warnings.Add($"Could not set {label}: parameter missing or read-only.");
                return;
            }

            var original = parameter.AsInteger();
            if (Matches(parameter.AsValueString(), key))
                return;
            for (var value = 0; value <= 4; value++)
            {
                try
                {
                    if (!parameter.Set(value))
                        continue;
                }
                catch
                {
                    continue;
                }

                if (Matches(parameter.AsValueString(), key))
                    return;
            }

            parameter.Set(original);
            warnings.Add($"Could not set {label} to '{wanted}': no value of the parameter is labelled that way.");
        }

        private static bool Matches(string text, string key)
        {
            return text != null && text.Trim().ToLowerInvariant().StartsWith(key, StringComparison.Ordinal);
        }

        private static readonly (string Key, TrussMemberType Kind, BuiltInParameter TypeParameter)[] MemberKinds =
        {
            ("topChord", TrussMemberType.ETK_TrussTopChord, BuiltInParameter.TRUSS_FAMILY_TOP_CHORD_STRUCTURAL_TYPES_PARAM),
            ("bottomChord", TrussMemberType.ETK_TrussBottomChord, BuiltInParameter.TRUSS_FAMILY_BOTTOM_CHORD_STRUCTURAL_TYPES_PARAM),
            ("verticalWeb", TrussMemberType.ETK_TrussVertWeb, BuiltInParameter.TRUSS_FAMILY_VERT_WEB_STRUCTURAL_TYPES_PARAM),
            ("diagonalWeb", TrussMemberType.ETK_TrussDiagWeb, BuiltInParameter.TRUSS_FAMILY_DIAG_WEB_STRUCTURAL_TYPES_PARAM)
        };

        private static Dictionary<TrussMemberType, FamilySymbol> ResolveMemberTypes(FramingLookup lookup, JObject memberTypes)
        {
            var map = new Dictionary<TrussMemberType, FamilySymbol>();
            if (memberTypes == null)
                return map;
            foreach (var property in memberTypes.Properties())
            {
                var kind = MemberKinds.FirstOrDefault(k => string.Equals(k.Key, property.Name, StringComparison.OrdinalIgnoreCase));
                if (kind.Key == null)
                    throw new ArgumentException($"Unknown memberTypes key '{property.Name}' (topChord, bottomChord, verticalWeb, diagonalWeb).");
                if (!(property.Value is JObject reference))
                    throw new ArgumentException($"memberTypes.{property.Name} must be {{typeId}} or {{familyName, typeName}}.");
                try
                {
                    map[kind.Kind] = lookup.ResolveType(reference);
                }
                catch (ArgumentException ex)
                {
                    throw new ArgumentException($"memberTypes.{property.Name}: {ex.Message}");
                }
            }

            return map;
        }

        /// <summary>Writes the chord/web framing types on the truss type (affects every truss of that type).</summary>
        private static void SetTypeMemberTypes(Document doc, Truss truss, Dictionary<TrussMemberType, FamilySymbol> memberTypes, List<string> warnings)
        {
            var trussType = doc.GetElement(truss.GetTypeId());
            if (trussType == null)
            {
                warnings.Add("Could not read the truss type to set member types.");
                return;
            }

            foreach (var kind in MemberKinds)
            {
                if (!memberTypes.TryGetValue(kind.Kind, out var symbol))
                    continue;
                if (!symbol.IsActive)
                    symbol.Activate();
                var parameter = trussType.get_Parameter(kind.TypeParameter);
                if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.ElementId)
                {
                    warnings.Add($"Could not set {kind.Key} type on the truss type: parameter missing or read-only.");
                    continue;
                }

                if (parameter.AsElementId() != symbol.Id && !parameter.Set(symbol.Id))
                    warnings.Add($"Revit rejected {kind.Key} type '{symbol.FamilyName}: {symbol.Name}' on the truss type.");
            }
        }

        /// <summary>Changes the type of this truss's members by role (top/bottom chord, vertical/diagonal web).</summary>
        private static int SetInstanceMemberTypes(Document doc, Truss truss, List<ElementId> members,
            Dictionary<TrussMemberType, FamilySymbol> memberTypes, List<string> warnings)
        {
            var changed = 0;
            var found = new HashSet<TrussMemberType>();
            var failures = new Dictionary<string, int>();
            foreach (var symbol in memberTypes.Values.Where(s => !s.IsActive))
                symbol.Activate();

            foreach (var id in members)
            {
                TrussMemberType kind;
                try
                {
                    kind = truss.GetTrussMemberInfo(id).memberTypeKey;
                }
                catch
                {
                    continue;
                }

                if (!memberTypes.TryGetValue(kind, out var symbol))
                    continue;
                found.Add(kind);
                var member = doc.GetElement(id);
                if (member == null || member.GetTypeId() == symbol.Id)
                    continue;
                try
                {
                    member.ChangeTypeId(symbol.Id);
                    changed++;
                }
                catch (Exception ex)
                {
                    failures[ex.Message] = failures.TryGetValue(ex.Message, out var count) ? count + 1 : 1;
                }
            }

            foreach (var kind in MemberKinds.Where(k => memberTypes.ContainsKey(k.Kind) && !found.Contains(k.Kind)))
                warnings.Add($"No {kind.Key} members found on the truss.");
            foreach (var failure in failures)
                warnings.Add($"Could not change {failure.Value} member type(s): {failure.Key}");
            return changed;
        }
    }
}
