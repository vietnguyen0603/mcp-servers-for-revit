using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Framing
{
    /// <summary>
    ///     attach_columns: attaches the top and/or base of placed structural columns to beams,
    ///     floors, roofs (or any explicit target Revit accepts, e.g. levels, ceilings, trusses'
    ///     chords) with ColumnAttachment, or removes those attachments. targets 'auto' probes a
    ///     small vertical prism at the column's top (base) location and picks the framing / floor /
    ///     roof whose geometry it hits closest in z to the column end. One sub-transaction per
    ///     column; dryRun only reports the targets that would be used.
    /// </summary>
    public class AttachColumnsEventHandler : JsonParameterEventHandler
    {
        private const int Base = 0;
        private const int Top = 1;
        private const double ProbeHalfSize = 25.0 / DocumentationUtils.MmPerFoot;

        public override string GetName() => "Attach Columns";

        private class EndRequest
        {
            public string Key;
            public int BaseOrTop;
            public bool Auto;
            public List<ElementId> TargetIds = new List<ElementId>();
            public HashSet<BuiltInCategory> Categories;
            public ColumnAttachmentJustification Justification = ColumnAttachmentJustification.Minimum;
            public ColumnAttachmentCutStyle CutStyle = ColumnAttachmentCutStyle.CutColumn;
            public double Offset;
            public double Range;
        }

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var requests = new List<EndRequest>();
            var top = ReadRequest(doc, parameters["attachTop"], "attachTop", Top);
            var bottom = ReadRequest(doc, parameters["attachBase"], "attachBase", Base);
            if (top != null) requests.Add(top);
            if (bottom != null) requests.Add(bottom);
            var detachTop = parameters.Value<bool?>("detachTop") ?? false;
            var detachBase = parameters.Value<bool?>("detachBase") ?? false;
            if (requests.Count == 0 && !detachTop && !detachBase)
                throw new ArgumentException("Give attachTop, attachBase, detachTop or detachBase.");
            if ((top != null && detachTop) || (bottom != null && detachBase))
                throw new ArgumentException("Do not attach and detach the same end in one call (attach replaces an existing attachment).");

            var filter = (parameters["filter"] as JObject)?.DeepClone() as JObject ?? new JObject();
            if (filter["category"] == null)
                filter["category"] = "columns";
            var members = StructuralMemberSelector.Select(doc, filter).Where(StructuralMemberSelector.IsColumn).ToList();
            var maxElements = parameters.Value<int?>("maxElements") ?? 2000;
            if (members.Count > maxElements)
                throw new ArgumentException($"{members.Count} columns match, more than maxElements ({maxElements}); narrow the filter.");

            var dryRun = parameters.Value<bool?>("dryRun") ?? false;
            List<JObject> results;
            if (dryRun)
            {
                results = new List<JObject>();
                for (var index = 0; index < members.Count; index++)
                {
                    JObject result;
                    try
                    {
                        result = JObject.FromObject(Process(doc, members[index], requests, detachTop, detachBase, true));
                        result.AddFirst(new JProperty("success", true));
                    }
                    catch (Exception ex)
                    {
                        result = new JObject { ["success"] = false, ["id"] = members[index].Id.GetValue(), ["message"] = ex.Message };
                    }
                    result.AddFirst(new JProperty("index", index));
                    results.Add(result);
                }
            }
            else
            {
                results = DocumentationUtils.RunBatch(doc, "MCP: Attach Columns", new JArray(Enumerable.Range(0, members.Count)),
                    token => Process(doc, members[token.Value<int>()], requests, detachTop, detachBase, false));
            }

            var summary = new JObject
            {
                ["dryRun"] = dryRun,
                ["matched"] = members.Count,
                ["succeeded"] = results.Count(r => r.Value<bool>("success")),
                ["failed"] = results.Count(r => !r.Value<bool>("success")),
                ["results"] = new JArray(results)
            };
            return Ok($"{(dryRun ? "Would process" : "Processed")} {summary["succeeded"]} of {members.Count} columns.", summary);
        }

        private static EndRequest ReadRequest(Document doc, JToken token, string key, int baseOrTop)
        {
            if (!(token is JObject spec))
                return null;
            var request = new EndRequest { Key = key, BaseOrTop = baseOrTop };

            var targets = spec["targets"];
            if (targets == null || targets.Type == JTokenType.Null || (targets.Type == JTokenType.String && string.Equals(targets.ToString(), "auto", StringComparison.OrdinalIgnoreCase)))
            {
                request.Auto = true;
            }
            else if (targets is JArray ids && ids.Count > 0)
            {
                foreach (var id in ids)
                {
                    var element = doc.GetElement(id.Value<long>().ToRevitElementId())
                                  ?? throw new ArgumentException($"{key}.targets: element {id} not found.");
                    request.TargetIds.Add(element.Id);
                }
            }
            else
            {
                throw new ArgumentException($"{key}.targets must be 'auto' or a list of element ids.");
            }

            request.Categories = new HashSet<BuiltInCategory>();
            var categories = spec["categories"] as JArray;
            foreach (var name in categories?.Select(c => c.ToString()) ?? new[] { "framing", "floors", "roofs" })
            {
                switch (name.Trim().ToLowerInvariant())
                {
                    case "framing": request.Categories.Add(BuiltInCategory.OST_StructuralFraming); break;
                    case "floors": request.Categories.Add(BuiltInCategory.OST_Floors); break;
                    case "roofs": request.Categories.Add(BuiltInCategory.OST_Roofs); break;
                    case "ceilings": request.Categories.Add(BuiltInCategory.OST_Ceilings); break;
                    default: throw new ArgumentException($"{key}.categories: unknown '{name}' (framing, floors, roofs, ceilings).");
                }
            }

            switch ((spec.Value<string>("justification") ?? "minimum").Trim().ToLowerInvariant())
            {
                case "minimum": request.Justification = ColumnAttachmentJustification.Minimum; break;
                case "midpoint": request.Justification = ColumnAttachmentJustification.Midpoint; break;
                case "maximum": request.Justification = ColumnAttachmentJustification.Maximum; break;
                case "tangent": request.Justification = ColumnAttachmentJustification.Tangent; break;
                default: throw new ArgumentException($"{key}.justification must be minimum, midpoint, maximum or tangent.");
            }

            switch ((spec.Value<string>("cutStyle") ?? "cutColumn").Trim().ToLowerInvariant())
            {
                case "cutcolumn": request.CutStyle = ColumnAttachmentCutStyle.CutColumn; break;
                case "cuttarget": request.CutStyle = ColumnAttachmentCutStyle.CutTarget; break;
                case "none": request.CutStyle = ColumnAttachmentCutStyle.None; break;
                default: throw new ArgumentException($"{key}.cutStyle must be cutColumn, cutTarget or none.");
            }

            request.Offset = DocumentationUtils.MmToFeet(spec.Value<double?>("offsetMm") ?? 0);
            request.Range = DocumentationUtils.MmToFeet(spec.Value<double?>("searchRangeMm") ?? 5000);
            return request;
        }

        private static object Process(Document doc, FamilyInstance column, List<EndRequest> requests, bool detachTop, bool detachBase, bool dryRun)
        {
            if (!ColumnAttachment.IsValidColumn(column))
                throw new ArgumentException("Column does not support attachments (in-place family?).");

            var result = new JObject
            {
                ["id"] = column.Id.GetValue(),
                ["type"] = column.Symbol.FamilyName + ": " + column.Symbol.Name
            };

            if (detachTop) result["top"] = Detach(column, Top, dryRun);
            if (detachBase) result["base"] = Detach(column, Base, dryRun);

            foreach (var request in requests)
            {
                var label = request.BaseOrTop == Top ? "top" : "base";
                var target = FindTarget(doc, column, request, out var note);
                if (target == null)
                {
                    result[label] = new JObject { ["action"] = "none", ["reason"] = note };
                    continue;
                }

                if (!ColumnAttachment.IsValidTarget(column, target))
                {
                    result[label] = new JObject { ["action"] = "none", ["reason"] = $"{Describe(target)} is not a valid attachment target for this column." };
                    continue;
                }

                var entry = new JObject
                {
                    ["targetId"] = target.Id.GetValue(),
                    ["target"] = Describe(target)
                };
                if (note != null) entry["note"] = note;

                var existing = ColumnAttachment.GetColumnAttachment(column, request.BaseOrTop);
                if (existing != null)
                    entry["previousTargetId"] = existing.TargetId.GetValue();

                if (dryRun)
                {
                    entry["action"] = existing == null ? "wouldAttach" : "wouldReplace";
                }
                else
                {
                    if (existing != null)
                        ColumnAttachment.RemoveColumnAttachment(column, request.BaseOrTop);
                    // A column can attach to a given target only once (top or base).
                    if (ColumnAttachment.GetColumnAttachment(column, target.Id) != null)
                        throw new ArgumentException($"{label}: column is already attached to {Describe(target)} at its other end.");
                    ColumnAttachment.AddColumnAttachment(doc, column, target, request.BaseOrTop, request.CutStyle, request.Justification, request.Offset);
                    entry["action"] = existing == null ? "attached" : "replaced";
                }
                result[label] = entry;
            }

            return result;
        }

        private static JObject Detach(FamilyInstance column, int baseOrTop, bool dryRun)
        {
            var existing = ColumnAttachment.GetColumnAttachment(column, baseOrTop);
            if (existing == null)
                return new JObject { ["action"] = "none", ["reason"] = "not attached" };
            var entry = new JObject { ["previousTargetId"] = existing.TargetId.GetValue() };
            if (dryRun)
            {
                entry["action"] = "wouldDetach";
            }
            else
            {
                ColumnAttachment.RemoveColumnAttachment(column, baseOrTop);
                entry["action"] = "detached";
            }
            return entry;
        }

        private static string Describe(Element element) =>
            $"{element.Category?.Name ?? element.GetType().Name} '{element.Name}' ({element.Id.GetValue()})";

        // ---------------------------------------------------------------- target search

        private static Element FindTarget(Document doc, FamilyInstance column, EndRequest request, out string note)
        {
            note = null;
            if (!request.Auto && request.TargetIds.Count == 1)
                return doc.GetElement(request.TargetIds[0]);

            if (!TryColumnEnds(doc, column, out var basePoint, out var topPoint))
            {
                note = "column end points could not be determined";
                return null;
            }

            var isTop = request.BaseOrTop == Top;
            var end = isTop ? topPoint : basePoint;
            var mid = (basePoint.Z + topPoint.Z) / 2;
            var zMin = isTop ? mid : end.Z - request.Range;
            var zMax = isTop ? end.Z + request.Range : mid;
            if (zMax - zMin < 1e-3)
            {
                note = "column has no height";
                return null;
            }

            var probe = Probe(end, zMin, zMax);
            var collector = request.Auto
                ? new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(request.Categories.ToList()))
                : new FilteredElementCollector(doc, request.TargetIds);
            var candidates = collector.WhereElementIsNotElementType()
                .WherePasses(new ElementIntersectsSolidFilter(probe))
                .Where(e => e.Id != column.Id)
                .ToList();

            Element best = null;
            var bestDistance = double.MaxValue;
            foreach (var candidate in candidates)
            {
                var range = HitRange(candidate, probe);
                if (range == null) continue;
                var (low, high) = range.Value;
                var distance = end.Z < low ? low - end.Z : end.Z > high ? end.Z - high : 0;
                if (distance < bestDistance - 1e-6 || (Math.Abs(distance - bestDistance) <= 1e-6 && best != null && candidate.Id.GetValue() < best.Id.GetValue()))
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }

            if (best == null)
            {
                note = request.Auto
                    ? $"no {string.Join("/", request.Categories.Select(CategoryLabel))} found {(isTop ? "above" : "below")} the column within {request.Range * DocumentationUtils.MmPerFoot:0} mm"
                    : $"none of the given targets is {(isTop ? "above" : "below")} the column";
                return null;
            }

            if (candidates.Count > 1)
                note = $"{candidates.Count} candidates; nearest is {Math.Round(bestDistance * DocumentationUtils.MmPerFoot)} mm from the column {(isTop ? "top" : "base")}";
            return best;
        }

        private static string CategoryLabel(BuiltInCategory category)
        {
            switch (category)
            {
                case BuiltInCategory.OST_StructuralFraming: return "framing";
                case BuiltInCategory.OST_Floors: return "floors";
                case BuiltInCategory.OST_Roofs: return "roofs";
                default: return "ceilings";
            }
        }

        /// <summary>Column base and top points from its location and base/top level + offset parameters.</summary>
        private static bool TryColumnEnds(Document doc, FamilyInstance column, out XYZ basePoint, out XYZ topPoint)
        {
            basePoint = topPoint = null;
            if (column.Location is LocationCurve curve)
            {
                var a = curve.Curve.GetEndPoint(0);
                var b = curve.Curve.GetEndPoint(1);
                basePoint = a.Z <= b.Z ? a : b;
                topPoint = a.Z <= b.Z ? b : a;
                return true;
            }

            if (!(column.Location is LocationPoint point))
                return false;
            var baseZ = LevelZ(doc, column, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM);
            var topZ = LevelZ(doc, column, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM);
            var box = column.get_BoundingBox(null);
            baseZ ??= box?.Min.Z;
            topZ ??= box?.Max.Z;
            if (!baseZ.HasValue || !topZ.HasValue)
                return false;
            basePoint = new XYZ(point.Point.X, point.Point.Y, baseZ.Value);
            topPoint = new XYZ(point.Point.X, point.Point.Y, topZ.Value);
            return true;
        }

        private static double? LevelZ(Document doc, FamilyInstance column, BuiltInParameter levelParameter, BuiltInParameter offsetParameter)
        {
            var level = doc.GetElement(column.get_Parameter(levelParameter)?.AsElementId() ?? ElementId.InvalidElementId) as Level;
            if (level == null)
                return null;
            return level.Elevation + (column.get_Parameter(offsetParameter)?.AsDouble() ?? 0);
        }

        /// <summary>A 50 x 50 mm vertical prism around <paramref name="at" /> between zMin and zMax.</summary>
        private static Solid Probe(XYZ at, double zMin, double zMax)
        {
            var h = ProbeHalfSize;
            var corners = new[]
            {
                new XYZ(at.X - h, at.Y - h, zMin), new XYZ(at.X + h, at.Y - h, zMin),
                new XYZ(at.X + h, at.Y + h, zMin), new XYZ(at.X - h, at.Y + h, zMin)
            };
            var loop = new CurveLoop();
            for (var i = 0; i < 4; i++)
                loop.Append(Line.CreateBound(corners[i], corners[(i + 1) % 4]));
            return GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, zMax - zMin);
        }

        /// <summary>Z range where the element's solids meet the probe (bounding box of the element as fallback).</summary>
        private static (double, double)? HitRange(Element element, Solid probe)
        {
            double low = double.MaxValue, high = double.MinValue;
            foreach (var solid in Solids(element))
            {
                try
                {
                    var hit = BooleanOperationsUtils.ExecuteBooleanOperation(solid, probe, BooleanOperationsType.Intersect);
                    if (hit == null || hit.Volume < 1e-9) continue;
                    var box = hit.GetBoundingBox();
                    var min = box.Transform.OfPoint(box.Min).Z;
                    var max = box.Transform.OfPoint(box.Max).Z;
                    low = Math.Min(low, Math.Min(min, max));
                    high = Math.Max(high, Math.Max(min, max));
                }
                catch (Exception)
                {
                    // boolean failures fall back to the bounding box below
                }
            }

            if (low <= high)
                return (low, high);
            var elementBox = element.get_BoundingBox(null);
            return elementBox == null ? ((double, double)?)null : (elementBox.Min.Z, elementBox.Max.Z);
        }

        private static IEnumerable<Solid> Solids(Element element)
        {
            var geometry = element.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
            if (geometry == null)
                yield break;
            foreach (var solid in Flatten(geometry))
                yield return solid;
        }

        private static IEnumerable<Solid> Flatten(GeometryElement geometry)
        {
            foreach (var item in geometry)
            {
                if (item is Solid solid && solid.Volume > 1e-9)
                    yield return solid;
                else if (item is GeometryInstance instance)
                    foreach (var inner in Flatten(instance.GetInstanceGeometry()))
                        yield return inner;
            }
        }
    }
}
