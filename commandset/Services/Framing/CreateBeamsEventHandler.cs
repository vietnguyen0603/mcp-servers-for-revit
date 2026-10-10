using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Framing
{
    /// <summary>
    ///     Places structural framing on a level from plan points, straight or arc,
    ///     with start/end level offsets, z/y justification, cross-section rotation,
    ///     structural usage and optional disallowed end joins. One sub-transaction
    ///     per beam.
    /// </summary>
    public class CreateBeamsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Create Beams";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "beams");
            var lookup = new FramingLookup(doc, BuiltInCategory.OST_StructuralFraming, "Structural Framing");
            var results = DocumentationUtils.RunBatch(doc, "MCP: Create Beams", items,
                item => Create(doc, lookup, (JObject)item));
            return Ok($"Created {results.Count(r => r.Value<bool>("success"))} of {results.Count} beams.",
                DocumentationUtils.Summarize(results));
        }

        private static object Create(Document doc, FramingLookup lookup, JObject item)
        {
            var symbol = lookup.ResolveType(item);
            var level = lookup.ResolveLevel(item["level"], "level");
            var startOffset = FramingLookup.ReadMm(item, "startOffset") ?? 0;
            var endOffset = FramingLookup.ReadMm(item, "endOffset") ?? startOffset;
            var zJustification = ParseZJustification(item.Value<string>("zJustification"));
            var yJustification = ParseYJustification(item.Value<string>("yJustification"));
            var usage = ParseUsage(item.Value<string>("structuralUsage"));
            var warnings = new List<string>();

            var start = FramingLookup.RequireXy(item, "start", level.Elevation + startOffset);
            var end = FramingLookup.RequireXy(item, "end", level.Elevation + endOffset);
            if (start.DistanceTo(end) < doc.Application.ShortCurveTolerance)
                throw new ArgumentException("Beam start and end are the same point.");

            Curve curve;
            if (item["mid"] is JObject)
            {
                var mid = FramingLookup.RequireXy(item, "mid", (start.Z + end.Z) / 2);
                try
                {
                    curve = Arc.Create(start, end, mid);
                }
                catch (Exception ex)
                {
                    throw new ArgumentException($"Cannot make an arc through start, mid and end: {ex.Message}");
                }
            }
            else
            {
                curve = Line.CreateBound(start, end);
            }

            if (!symbol.IsActive)
                symbol.Activate();
            var beam = doc.Create.NewFamilyInstance(curve, symbol, level, StructuralType.Beam)
                       ?? throw new InvalidOperationException("Revit did not create the beam.");

            FramingLookup.SetParameter(beam, BuiltInParameter.STRUCTURAL_BEAM_END0_ELEVATION, p => p.Set(startOffset), "startOffset", warnings);
            FramingLookup.SetParameter(beam, BuiltInParameter.STRUCTURAL_BEAM_END1_ELEVATION, p => p.Set(endOffset), "endOffset", warnings);
            FramingLookup.SetParameter(beam, BuiltInParameter.Z_JUSTIFICATION, p => p.Set((int)zJustification), "zJustification", warnings);
            if (yJustification.HasValue)
                FramingLookup.SetParameter(beam, BuiltInParameter.Y_JUSTIFICATION, p => p.Set((int)yJustification.Value), "yJustification", warnings);

            var rotation = item.Value<double?>("rotationDeg");
            if (rotation.HasValue && Math.Abs(rotation.Value) > 1e-9)
                FramingLookup.SetParameter(beam, BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE, p => p.Set(rotation.Value * Math.PI / 180), "rotationDeg", warnings);

            if (usage.HasValue)
            {
                try
                {
                    beam.StructuralUsage = usage.Value;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not set structuralUsage: {ex.Message}");
                }
            }

            if (item.Value<bool?>("disallowJoin") == true)
            {
                foreach (var end0 in new[] { 0, 1 })
                {
                    try
                    {
                        StructuralFramingUtils.DisallowJoinAtEnd(beam, end0);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Could not disallow join at end {end0}: {ex.Message}");
                    }
                }
            }

            FramingLookup.SetIdentity(beam, item, warnings);

            var result = new JObject
            {
                ["id"] = beam.Id.GetValue(),
                ["typeId"] = symbol.Id.GetValue(),
                ["level"] = level.Name,
                ["startOffset"] = DocumentationUtils.FeetToMm(startOffset),
                ["endOffset"] = DocumentationUtils.FeetToMm(endOffset),
                ["length"] = DocumentationUtils.FeetToMm(curve.Length)
            };
            if (warnings.Count > 0)
                result["warnings"] = new JArray(warnings);
            return result;
        }

        internal static ZJustification ParseZJustification(string value)
        {
            switch ((value ?? "top").Trim().ToLowerInvariant())
            {
                case "top": return ZJustification.Top;
                case "center": return ZJustification.Center;
                case "bottom": return ZJustification.Bottom;
                case "origin": return ZJustification.Origin;
                default: throw new ArgumentException($"Invalid zJustification '{value}' (top, center, bottom, origin).");
            }
        }

        internal static YJustification? ParseYJustification(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            switch (value.Trim().ToLowerInvariant())
            {
                case "left": return YJustification.Left;
                case "center": return YJustification.Center;
                case "right": return YJustification.Right;
                case "origin": return YJustification.Origin;
                default: throw new ArgumentException($"Invalid yJustification '{value}' (left, center, right, origin).");
            }
        }

        internal static StructuralInstanceUsage? ParseUsage(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            switch (value.Trim().ToLowerInvariant())
            {
                case "girder": return StructuralInstanceUsage.Girder;
                case "joist": return StructuralInstanceUsage.Joist;
                case "horizontalbracing": return StructuralInstanceUsage.HorizontalBracing;
                case "purlin": return StructuralInstanceUsage.Purlin;
                case "other": return StructuralInstanceUsage.Other;
                default: throw new ArgumentException($"Invalid structuralUsage '{value}' (girder, joist, horizontalBracing, purlin, other).");
            }
        }
    }
}
