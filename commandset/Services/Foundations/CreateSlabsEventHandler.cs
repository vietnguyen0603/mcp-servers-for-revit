using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Foundations
{
    /// <summary>
    ///     create_slabs: floors / structural slabs / foundation slabs from polygons or
    ///     segment loops, with openings, level offsets, slope arrow and mark. One
    ///     transaction per call (chunk), one sub-transaction per slab.
    /// </summary>
    public class CreateSlabsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Create Slabs";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "slabs");
            var ctx = new FoundationModelUtils.Context(doc);
            var results = DocumentationUtils.RunBatch(doc, "MCP: Create Slabs", items, item =>
            {
                if (!(item is JObject slab))
                    throw new ArgumentException("Each slab must be an object.");
                return CreateFromItem(ctx, slab, "offset", slab.Value<bool?>("foundation") ?? false);
            });
            return Ok($"Created {results.Count(r => r.Value<bool>("success"))} of {results.Count} slabs.",
                DocumentationUtils.Summarize(results));
        }

        /// <summary>Creates one slab from its JSON item; shared with create_foundations (capSlab).</summary>
        internal static JObject CreateFromItem(FoundationModelUtils.Context ctx, JObject item, string offsetField, bool foundation)
        {
            var doc = ctx.Doc;
            var warnings = new List<string>();
            var level = FoundationModelUtils.ResolveLevel(ctx, item["level"], warnings, out var extraOffset);
            var offset = FoundationModelUtils.ToFeet(item.Value<double?>(offsetField) ?? 0) + extraOffset;
            var structural = item.Value<bool?>("structural") ?? true;
            var type = FoundationModelUtils.ResolveSlabType(ctx, item, foundation, warnings);

            var z = level.Elevation;
            var outer = FoundationModelUtils.ReadOuterLoop(item, z);
            var openings = FoundationModelUtils.ReadOpenings(item, z);

            Line slopeArrow = null;
            double slope = 0;
            if (item["slopeArrow"] is JObject arrow && arrow.HasValues)
            {
                var start = FoundationModelUtils.ReadXY(arrow["start"], z, "slopeArrow.start");
                var end = FoundationModelUtils.ReadXY(arrow["end"], z, "slopeArrow.end");
                var run = start.DistanceTo(end);
                if (run < 10 / 304.8)
                    throw new ArgumentException("slopeArrow is shorter than 10 mm.");
                var rise = FoundationModelUtils.ToFeet(arrow.Value<double?>("riseMm")
                                                       ?? throw new ArgumentException("slopeArrow.riseMm is required."));
                slopeArrow = Line.CreateBound(start, end);
                slope = rise / run;
            }

            var floor = FoundationModelUtils.CreateSlab(ctx, type, level, outer, openings, structural, slopeArrow, slope, warnings);

            var offsetParameter = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
            if (offsetParameter != null && !offsetParameter.IsReadOnly)
                offsetParameter.Set(offset);
            else if (Math.Abs(offset) > 1e-9)
                warnings.Add("Could not set the height offset from level.");

            FoundationModelUtils.SetMark(ctx, floor, item, warnings);
            doc.Regenerate();
            ctx.NeedsRegenerate = false;

            var result = new JObject
            {
                ["id"] = floor.Id.GetValue(),
                ["category"] = floor.Category?.Name,
                ["typeId"] = type.Id.GetValue(),
                ["typeName"] = type.Name,
                ["thicknessMm"] = FoundationModelUtils.TypeThicknessMm(type),
                ["levelId"] = level.Id.GetValue(),
                ["levelName"] = level.Name,
                ["offsetMm"] = FoundationModelUtils.ToMm(offset),
                ["topElevationMm"] = FoundationModelUtils.ToMm(level.Elevation + offset),
                ["openings"] = openings.Count,
                ["areaM2"] = FoundationModelUtils.AreaM2(floor)
            };
            FoundationModelUtils.AddWarnings(result, warnings);
            return result;
        }
    }
}
